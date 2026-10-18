using System.Text.Json.Nodes;
using Diagram.Visio;
using Xunit;

namespace Diagram.Visio.Tests;

/// <summary>R11: linear gradient fills are written as Visio gradient fills and read back with the same angle, stops and transparency.</summary>
public class GradientTests
{
    static object Gradient(double angle, params (double Offset, string Colour, double Opacity)[] stops) =>
        new { kind = "linear-gradient", angleDeg = angle, stops = stops.Select(s => new { offset = s.Offset, colour = s.Colour, opacity = s.Opacity }).ToArray() };

    static object StyleWith(object fill) => new { fill, stroke = Doc.Stroke, strokeWidthPt = 1.5, dash = "solid", lineCap = "butt", lineJoin = "miter" };

    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(45.0)]
    [InlineData(180.0)]
    [InlineData(-30.0)]
    public void AnAngleAndEveryStopSurviveTheRoundTrip(double angle)
    {
        var fill = Gradient(angle, (0, "#ffffff", 1), (0.35, "#00aa00", 0.5), (1, "#000066", 1));
        var (exported, back) = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 200, 60, new { style = StyleWith(fill) })])));
        Assert.DoesNotContain(exported.Diagnostics, d => d.Severity == "warning");
        var got = RT.El(back.Document, Doc.Id(1))["style"]!["fill"]!;
        Assert.Equal("linear-gradient", got["kind"]!.GetValue<string>());
        Assert.Equal(angle, got["angleDeg"]!.GetValue<double>(), 3);
        var stops = got["stops"]!.AsArray();
        Assert.Equal(3, stops.Count);
        Assert.Equal(new[] { 0.0, 0.35, 1.0 }, stops.Select(s => s!["offset"]!.GetValue<double>()).ToArray());
        Assert.Equal(new[] { "#ffffff", "#00aa00", "#000066" }, stops.Select(s => s!["colour"]!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { 1.0, 0.5, 1.0 }, stops.Select(s => s!["opacity"]!.GetValue<double>()).ToArray());
    }

    [Fact]
    public void ItIsWrittenAsAVisioGradientWithItsStopsAndKeepsTheFirstColourAsThePlainFill()
    {
        var fill = Gradient(90, (0, "#ff0000", 1), (1, "#0000ff", 0.25));
        var (exported, _) = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 120, 60, new { style = StyleWith(fill) })])));
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        string Cell(string n) => (string?)xml.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == n).Attribute("V") ?? "";
        Assert.Equal("1", Cell("FillGradientEnabled"));
        Assert.Equal("0", Cell("FillGradientDir"));
        Assert.Equal(Math.PI / 2, double.Parse(Cell("FillGradientAngle"), System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal("#ff0000", Cell("FillForegnd"), ignoreCase: true);
        var section = xml.Descendants().Single(e => e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "FillGradient");
        var rows = section.Elements().Where(e => e.Name.LocalName == "Row").ToList();
        Assert.Equal(2, rows.Count);
        string RowCell(System.Xml.Linq.XElement row, string n) => (string?)row.Elements().First(c => (string?)c.Attribute("N") == n).Attribute("V") ?? "";
        Assert.Equal("0.75", RowCell(rows[1], "GradientStopColorTrans")); // opacity 0.25 is 75% transparent
        Assert.Equal("1", RowCell(rows[1], "GradientStopPosition"));
    }

    [Fact]
    public void ASolidFillCarriesNoGradientCells()
    {
        var (exported, _) = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 120, 60)])));
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        Assert.DoesNotContain(xml.Descendants(), e => (string?)e.Attribute("N") is "FillGradientEnabled" or "FillGradient");
    }

    [Fact]
    public void ARadialGradientFromVisioBecomesItsFirstColourWithANote()
    {
        var fill = Gradient(0, (0, "#ff0000", 1), (1, "#0000ff", 1));
        var (exported, _) = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 120, 60, new { style = StyleWith(fill) })])));
        var radial = Fx.Edit(exported.Package, "visio/pages/page1.xml", d => d.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "FillGradientDir").SetAttributeValue("V", "2"));
        var back = new NativeVsdxImporter().Import(new MemoryStream(radial), new ImportOptions());
        Assert.True(back.IsOk, back.Error?.Message);
        var style = System.Text.Json.JsonSerializer.SerializeToNode(back.Value.Document, Diagram.Core.Contracts.ContractJson.Options)!["pages"]![0]!["elements"]![0]!["style"]!["fill"]!;
        Assert.Equal("solid", style["kind"]!.GetValue<string>());
        Assert.Equal("#ff0000", style["colour"]!.GetValue<string>(), ignoreCase: true);
        Assert.Contains(back.Value.Diagnostics, d => d.Code == "gradient_approximated");
    }

    [Fact]
    public void AGradientOnAShapeWithNoFillPatternStaysNone()
    {
        var (exported, _) = RT.Trip(Doc.Snapshot(Doc.Page([Doc.Shape(1, 60, 60, 120, 60, new { style = StyleWith(Gradient(0, (0, "#ff0000", 1), (1, "#0000ff", 1))) })])));
        var none = Fx.Edit(exported.Package, "visio/pages/page1.xml", d => d.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "FillPattern").SetAttributeValue("V", "0"));
        var back = new NativeVsdxImporter().Import(new MemoryStream(none), new ImportOptions());
        var fill = System.Text.Json.JsonSerializer.SerializeToNode(back.Value.Document, Diagram.Core.Contracts.ContractJson.Options)!["pages"]![0]!["elements"]![0]!["style"]!["fill"]!;
        Assert.Equal("none", fill["kind"]!.GetValue<string>());
    }
}
