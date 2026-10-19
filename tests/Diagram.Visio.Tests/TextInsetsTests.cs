using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

namespace Diagram.Visio.Tests;

/// <summary>R14: per-side text margins and vertical text survive the trip through Visio as text block margins and a text angle.</summary>
public class TextInsetsTests
{
    static object Block(object? over = null)
    {
        var o = System.Text.Json.JsonSerializer.SerializeToNode(new { value = "Corona Sensical", fontFamily = "Segoe UI", fontSizePt = 12, bold = false, italic = false, underline = false, colour = "#000000", horizontalAlign = "center", verticalAlign = "middle", wrap = true, paddingPt = 4 })!.AsObject();
        if (over is not null) foreach (var kv in System.Text.Json.JsonSerializer.SerializeToNode(over)!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    static ExportSnapshot Scene() => Doc.Snapshot(Doc.Make([Doc.Page(
    [
        Doc.Shape(1, 40, 40, 60, 200, new { alias = "lane", text = Block(new { paddingPt = 0, orientation = "vertical" }) }),
        Doc.Shape(2, 200, 40, 120, 60, new { alias = "pad", text = Block(new { paddingPt = 4, insetsPt = new { top = 1, right = 2, bottom = 3, left = 30 } }) }),
        Doc.Shape(3, 400, 40, 120, 60, new { alias = "plain", text = Block() }),
    ])]));

    [Fact]
    public void VerticalTextAndPerSideMarginsComeBack()
    {
        var (exported, back) = RT.Trip(Scene());
        Assert.DoesNotContain(exported.Diagnostics, d => d.Severity == "warning");
        var lane = RT.El(back.Document, Doc.Id(1))["text"]!;
        Assert.Equal("vertical", lane["orientation"]!.GetValue<string>());
        Assert.Null(lane["insetsPt"]);
        var pad = RT.El(back.Document, Doc.Id(2))["text"]!;
        Assert.Null(pad["orientation"]);
        Assert.Equal(30, pad["insetsPt"]!["left"]!.GetValue<double>(), 3);
        Assert.Equal(1, pad["insetsPt"]!["top"]!.GetValue<double>(), 3);
        Assert.Equal(3, pad["insetsPt"]!["bottom"]!.GetValue<double>(), 3);
        Assert.Equal(2, pad["insetsPt"]!["right"]!.GetValue<double>(), 3);
        var plain = RT.El(back.Document, Doc.Id(3))["text"]!;
        Assert.Null(plain["insetsPt"]); Assert.Null(plain["orientation"]);
    }

    [Fact]
    public void AVerticalTitleIsAQuarterTurnedTextBlockTheSizeOfTheShapeTurned()
    {
        var (exported, _) = RT.Trip(Scene());
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        var shape = xml.Descendants().First(e => e.Name.LocalName == "Shape" && e.Descendants().Any(c => c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "TxtAngle"));
        string Cell(string n) => (string?)shape.Elements().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == n).Attribute("V") ?? "";
        double D(string n) => double.Parse(Cell(n), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Math.PI / 2, D("TxtAngle"), 4);
        Assert.Equal(200.0 / 72, D("TxtWidth"), 4); // the line runs the shape's height
        Assert.Equal(60.0 / 72, D("TxtHeight"), 4);
    }
}
