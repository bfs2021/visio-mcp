using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Diagram.Core.Contracts;
using Xunit;

namespace Diagram.Visio.Tests;

/// <summary>R15: drawings made in Visio (not by this application) must import without errors and without drawing things that are not there.</summary>
public class ReferenceImportFidelityTests
{
    static readonly XNamespace V = "http://schemas.microsoft.com/office/visio/2012/main";

    static byte[] Package() => RT.Export(Doc.Snapshot(Doc.Make([Doc.Page([Doc.Shape(1, 50, 50, 80, 40, new { alias = "a" })])]))).Package;

    static XElement FirstShape(XDocument d) => d.Descendants(V + "Shape").First(s => s.Elements(V + "Section").Any(x => (string?)x.Attribute("N") == "Geometry"));
    static XElement Cell(XElement e, string n) => e.Elements(V + "Cell").First(c => (string?)c.Attribute("N") == n);

    static JsonNode ShapeA(ImportResult r) =>
        r.Document.Pages.SelectMany(p => p.Elements).Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!).First(j => j["alias"]?.GetValue<string>() == "a");

    [Fact]
    public void AShapeWithNoWidthBecomesAHairlineAroundTheSameCentreInsteadOfFailingTheImport()
    {
        var before = ShapeA(RT.Import(Package()))["bounds"]!;
        var pkg = Fx.Edit(Package(), "visio/pages/page1.xml", d => { var s = FirstShape(d); Cell(s, "Width").SetAttributeValue("V", "0"); Cell(s, "Width").SetAttributeValue("F", null); });
        var r = RT.Import(pkg);
        var b = ShapeA(r)["bounds"]!;
        Assert.True(b["width"]!.GetValue<double>() > 0);
        Assert.True(b["width"]!.GetValue<double>() < 0.1);
        Assert.Contains(r.Diagnostics, x => x.Code == "zero_size_adjusted");
        Assert.True(before["height"]!.GetValue<double>() > 0 && Math.Abs(b["height"]!.GetValue<double>() - before["height"]!.GetValue<double>()) < 0.01); // the other side is untouched
    }

    [Fact]
    public void ANurbsCurveIsFollowedNotReplacedByItsControlPoints()
    {
        // MoveTo (0,0), then one cubic segment to (W,0) bending up through control points at height H: it peaks at 75% of the height in the middle
        var pkg = Fx.Edit(Package(), "visio/pages/page1.xml", d =>
        {
            var geo = FirstShape(d).Elements(V + "Section").First(x => (string?)x.Attribute("N") == "Geometry");
            geo.Elements(V + "Row").Remove();
            var w = Cell(FirstShape(d), "Width").Attribute("V")!.Value; var h = Cell(FirstShape(d), "Height").Attribute("V")!.Value;
            geo.Add(new XElement(V + "Row", new XAttribute("T", "MoveTo"), new XAttribute("IX", 1), new XElement(V + "Cell", new XAttribute("N", "X"), new XAttribute("V", "0")), new XElement(V + "Cell", new XAttribute("N", "Y"), new XAttribute("V", "0"))));
            geo.Add(new XElement(V + "Row", new XAttribute("T", "NURBSTo"), new XAttribute("IX", 2),
                new XElement(V + "Cell", new XAttribute("N", "X"), new XAttribute("V", w)), new XElement(V + "Cell", new XAttribute("N", "Y"), new XAttribute("V", "0")),
                new XElement(V + "Cell", new XAttribute("N", "E"), new XAttribute("V", "NURBS(1, 3, 0, 0, 0.33,1,0,1, 0.66,1,0,1)"))));
            _ = h;
        });
        var r = RT.Import(pkg);
        var path = ShapeA(r)["geometry"]!["svgPath"]!.GetValue<string>();
        var points = path.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("M" or "L")).Select(t => double.Parse(t, CultureInfo.InvariantCulture)).Chunk(2).ToList();
        Assert.True(points.Count >= 8, path);
        Assert.Contains(points, p => Math.Abs(p[0] - 50) < 8 && Math.Abs(p[1] - 25) < 4); // the middle of the curve, not a corner of the control polygon
        Assert.DoesNotContain(r.Diagnostics, x => x.Code == "geometry_approximated");
    }

    [Fact]
    public void ACurveItCannotEvaluateFallsBackToALineToItsEndNotToItsControlPoints()
    {
        var pkg = Fx.Edit(Package(), "visio/pages/page1.xml", d =>
        {
            var geo = FirstShape(d).Elements(V + "Section").First(x => (string?)x.Attribute("N") == "Geometry");
            geo.Elements(V + "Row").Remove();
            geo.Add(new XElement(V + "Row", new XAttribute("T", "MoveTo"), new XAttribute("IX", 1), new XElement(V + "Cell", new XAttribute("N", "X"), new XAttribute("V", "0")), new XElement(V + "Cell", new XAttribute("N", "Y"), new XAttribute("V", "0"))));
            geo.Add(new XElement(V + "Row", new XAttribute("T", "NURBSTo"), new XAttribute("IX", 2),
                new XElement(V + "Cell", new XAttribute("N", "X"), new XAttribute("V", Cell(FirstShape(d), "Width").Attribute("V")!.Value)), new XElement(V + "Cell", new XAttribute("N", "Y"), new XAttribute("V", "0")),
                new XElement(V + "Cell", new XAttribute("N", "E"), new XAttribute("V", "NURBS(1, 3, 0, 0, 9,9,0,1)")))); // too few control points for a cubic
        });
        var r = RT.Import(pkg);
        Assert.Contains(r.Diagnostics, x => x.Code == "geometry_approximated");
        var numbers = ShapeA(r)["geometry"]!["svgPath"]!.GetValue<string>().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("M" or "L")).Select(t => double.Parse(t, CultureInfo.InvariantCulture));
        Assert.All(numbers, n => Assert.InRange(n, 0, 100)); // nothing is drawn out at the control point (9, 9)
    }

    [Fact]
    public void AnOutlineMarkedNoFillIsNotFilled()
    {
        var pkg = Fx.Edit(Package(), "visio/pages/page1.xml", d =>
        {
            var geo = FirstShape(d).Elements(V + "Section").First(x => (string?)x.Attribute("N") == "Geometry");
            geo.Elements(V + "Cell").Where(c => (string?)c.Attribute("N") == "NoFill").Remove();
            geo.AddFirst(new XElement(V + "Cell", new XAttribute("N", "NoFill"), new XAttribute("V", "1")));
        });
        Assert.Equal("none", ShapeA(RT.Import(pkg))["style"]!["fill"]!["kind"]!.GetValue<string>());
    }
}
