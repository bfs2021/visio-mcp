using System.Text.Json.Nodes;
using Diagram.Visio;
using Xunit;

namespace Diagram.Visio.Tests;

/// <summary>R10: the flowchart symbols are written to Visio as native outlines (lines and true arcs) with connection points, and read back as the same presets.</summary>
public class FlowchartGeometryTests
{
    static readonly string[] Presets = ["terminator", "stored-data", "document", "lined-document"];

    static object Ports() => new[] { new { id = "north", x = 0.5, y = 0.0 }, new { id = "east", x = 1.0, y = 0.5 }, new { id = "south", x = 0.5, y = 1.0 }, new { id = "west", x = 0.0, y = 0.5 } };

    [Theory]
    [InlineData(140, 70)]
    [InlineData(90, 90)]
    [InlineData(220, 40)]
    [InlineData(50, 120)]
    public void EachSymbolIsRecognisedAgainAtAnyShape(double w, double h)
    {
        var shapes = Presets.Select((p, i) => Doc.Shape(i + 1, 40 + i * 260, 60, w, h, new { alias = p, ports = Ports() }, preset: p)).ToArray();
        var (exported, back) = RT.Trip(Doc.Snapshot(Doc.Page(shapes, new { widthPt = 1200, heightPt = 400 })));
        Assert.DoesNotContain(exported.Diagnostics, d => d.Severity == "warning" || d.Code is "outline_converted" or "preset_approximated" or "curves_flattened");
        for (var i = 0; i < Presets.Length; i++)
        {
            var shape = RT.El(back.Document, Doc.Id(i + 1));
            Assert.Equal(Presets[i], shape["geometry"]!["preset"]!.GetValue<string>()); // a preset again, not a custom path
            Assert.Null(shape["geometry"]!["svgPath"]);
            Assert.Equal(new[] { "north", "east", "south", "west" }, shape["ports"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).ToArray());
        }
    }

    [Fact]
    public void ArcsAreWrittenAsNativeVisioArcsWithFormulasThatFollowTheSize()
    {
        var (exported, _) = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 144, 48, null, preset: "terminator"), Doc.Shape(2, 300, 60, 100, 60, null, preset: "document")])));
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        var arcs = xml.Descendants().Where(e => e.Name.LocalName == "Row" && (string?)e.Attribute("T") == "ArcTo").ToList();
        Assert.Equal(4, arcs.Count); // two round ends and the two halves of the wave
        string F(System.Xml.Linq.XElement row, string cell) => (string?)row.Elements().First(c => (string?)c.Attribute("N") == cell).Attribute("F") ?? "";
        // the round ends are as big as half the shorter side whatever the stretch; the wave is a fraction of the height
        Assert.Contains("MIN(Width,Height)", F(arcs[0], "A"));
        Assert.Contains("MIN(Width,Height)", F(arcs[1], "A"));
        Assert.Contains("Height*", F(arcs[2], "A"));
        var a = double.Parse((string)arcs[0].Elements().First(c => (string?)c.Attribute("N") == "A").Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(24.0 / 72, Math.Abs(a), 3); // 24pt (half the 48pt height) in inches
    }

    [Fact]
    public void TheLineInALinedDocumentIsItsOwnOpenSectionAndDoesNotChangeTheOutline()
    {
        var (exported, back) = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 100, 80, null, preset: "lined-document")])));
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        var sections = xml.Descendants().Where(e => e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "Geometry").ToList();
        Assert.Equal(2, sections.Count);
        Assert.Equal("1", (string?)sections[1].Elements().First(c => (string?)c.Attribute("N") == "NoFill").Attribute("V"));
        Assert.Equal("lined-document", RT.El(back.Document, Doc.Id(1))["geometry"]!["preset"]!.GetValue<string>());
    }

    [Fact]
    public void ADifferentOutlineInVisioIsKeptAsACustomPathNotMistakenForThePreset()
    {
        var exported = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 100, 50, null, preset: "terminator")]))).Item1;
        // someone redraws the outline in Visio: the first arc's bulge becomes much smaller
        var edited = Fx.Edit(exported.Package, "visio/pages/page1.xml", d =>
        {
            var arc = d.Descendants().First(e => e.Name.LocalName == "Row" && (string?)e.Attribute("T") == "ArcTo");
            var a = arc.Elements().First(c => (string?)c.Attribute("N") == "A"); a.SetAttributeValue("V", "0.05"); a.Attribute("F")?.Remove();
        });
        var back = new NativeVsdxImporter().Import(new MemoryStream(edited), new ImportOptions());
        Assert.True(back.IsOk, back.Error?.Message);
        var geometry = System.Text.Json.JsonSerializer.SerializeToNode(back.Value.Document, Diagram.Core.Contracts.ContractJson.Options)!["pages"]![0]!["elements"]![0]!["geometry"]!;
        Assert.NotNull(geometry["svgPath"]);
        Assert.Null(geometry["preset"]);
        Assert.Contains(back.Value.Diagnostics, d => d.Code == "outline_converted");
    }

    [Fact]
    public void FlattenedArcsStayCircularAndHitTheirEndPoints()
    {
        var pts = GeometryMap.ArcPoints(0, 0, 100, 0, 50).ToList(); // a semicircle bulging to the right of travel: south on screen
        Assert.Equal((100, 0), pts[^1]);
        Assert.All(pts.Take(pts.Count - 1), p => Assert.Equal(50, Math.Sqrt((p.X - 50) * (p.X - 50) + p.Y * p.Y), 6));
        Assert.All(pts, p => Assert.True(p.Y >= -1e-9));
        var left = GeometryMap.ArcPoints(0, 0, 100, 0, -50).ToList();
        Assert.All(left, p => Assert.True(p.Y <= 1e-9)); // to the left of travel: north
        Assert.Single(GeometryMap.ArcPoints(0, 0, 100, 0, 0));
    }
}
