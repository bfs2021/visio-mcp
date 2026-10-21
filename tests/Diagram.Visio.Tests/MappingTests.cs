using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

static class RT
{
    public static readonly NativeVsdxExporter Exporter = new();
    public static readonly NativeVsdxImporter Importer = new();

    public static ExportResult Export(ExportSnapshot snap, Blobs? blobs = null)
    {
        var r = Exporter.ExportAsync(snap, blobs ?? new Blobs(), new ExportOptions(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(r.IsOk, r.Error?.Message);
        return r.Value;
    }

    public static ImportResult Import(byte[] package, ImportOptions? o = null)
    {
        var r = Importer.Import(new MemoryStream(package), o ?? new ImportOptions());
        Assert.True(r.IsOk, r.Error?.Message);
        return r.Value;
    }

    public static (ExportResult Out, ImportResult Back) Trip(ExportSnapshot snap, Blobs? blobs = null) { var e = Export(snap, blobs); return (e, Import(e.Package)); }

    public static JsonObject El(DiagramDocument d, string id, int page = 0) =>
        JsonSerializer.SerializeToNode(d.Pages[page].Elements.First(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!["id"]!.GetValue<string>() == id), ContractJson.Options)!.AsObject();

    public static IEnumerable<JsonObject> All(DiagramDocument d, int page = 0) => d.Pages[page].Elements.Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!.AsObject());
}

public class MappingTests
{
    const double Tol = 0.01; // points and degrees, per the native tolerance in the design

    static void Near(double expected, double? actual, double tol = Tol) => Assert.True(actual is { } a && Math.Abs(a - expected) <= tol, $"expected {expected} but was {actual}");

    [Fact]
    public void EditedAndDeletedGuidesRetainIdentityAcrossRepeatedNativeRoundTrips()
    {
        var page = Doc.Page([Doc.Shape(1, 50, 50, 100, 60)], guides: [
            new { id = Doc.Id(90), orientation = "horizontal", positionPt = -20.5 },
            new { id = Doc.Id(91), orientation = "vertical", positionPt = 120.25 }]);
        var (_, first) = RT.Trip(Doc.Snapshot(page));
        var edited = JsonSerializer.SerializeToNode(first.Document.Pages[0], ContractJson.Options)!.AsObject();
        edited["guides"] = new JsonArray(new JsonObject { ["id"] = Doc.Id(90), ["orientation"] = "horizontal", ["positionPt"] = 240.75 });
        var (_, second) = RT.Trip(Doc.Snapshot(edited));
        var guide = Assert.Single(second.Document.Pages[0].Guides);
        Assert.Equal(Doc.Id(90), guide.Id); Assert.Equal("horizontal", guide.Orientation); Near(240.75, guide.PositionPt);
        Assert.Equal(Doc.Id(1), Assert.Single(RT.All(second.Document))["id"]!.GetValue<string>());
    }

    [Fact]
    public void PageSizeNameGridAndGuidesRoundTrip()
    {
        var page = Doc.Page([Doc.Shape(1, 50, 50, 100, 60)], new { name = "Architecture", widthPt = 595.2755905511812, heightPt = 841.8897637795276 },
            guides: [new { id = Doc.Id(90), orientation = "horizontal", positionPt = 200.5 }, new { id = Doc.Id(91), orientation = "vertical", positionPt = 120.25 }]);
        var (exp, back) = RT.Trip(Doc.Snapshot(page));
        var p = back.Document.Pages[0];
        Assert.Equal("Architecture", p.Name);
        Near(595.2755905511812, p.WidthPt); Near(841.8897637795276, p.HeightPt);
        Assert.Equal(Doc.PageId, p.Id);
        Assert.Equal(14.173228346456693, p.Grid.SpacingPt, 6);
        Assert.True(p.Grid.Snap && p.Grid.Visible);
        Assert.Collection(p.Guides.OrderBy(g => g.Orientation),
            h => { Assert.Equal("horizontal", h.Orientation); Near(200.5, h.PositionPt); Assert.Equal(Doc.Id(90), h.Id); },
            v => { Assert.Equal("vertical", v.Orientation); Near(120.25, v.PositionPt); Assert.Equal(Doc.Id(91), v.Id); });
        // the page sheet is in inches with a metric display unit, as Visio keeps it
        var pages = Fx.Part(exp.Package, "visio/pages/pages.xml");
        var w = pages.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "PageWidth");
        Assert.Equal("MM", (string?)w.Attribute("U"));
        Assert.Equal(595.2755905511812 / 72, double.Parse((string)w.Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture), 9);
    }

    [Theory]
    [InlineData("rectangle")] [InlineData("rounded-rectangle")] [InlineData("ellipse")] [InlineData("diamond")] [InlineData("triangle")]
    [InlineData("hexagon")] [InlineData("parallelogram")] [InlineData("cylinder")] [InlineData("cloud")] [InlineData("callout")]
    public void EverySourcePresetBecomesAnEditableShapeAndComesBackAsTheSamePreset(string preset)
    {
        object geometry = preset == "rounded-rectangle" ? new { preset, cornerRadiusPt = 12.0 } : new { preset };
        var el = Doc.Shape(1, 100, 100, 160, 90, new { geometry });
        var (exp, back) = RT.Trip(Doc.Snapshot(Doc.Page([el])));
        var s = RT.El(back.Document, Doc.Id(1));
        Assert.Equal("shape", s["kind"]!.GetValue<string>());
        Assert.Equal(preset, s["geometry"]!["preset"]!.GetValue<string>());
        if (preset == "rounded-rectangle") Near(12, s["geometry"]!["cornerRadiusPt"]!.GetValue<double>());
        // an editable Visio shape: an ordinary shape with geometry formulas, not a picture
        var shape = Fx.Part(exp.Package, "visio/pages/page1.xml").Descendants().First(e => e.Name.LocalName == "Shape");
        Assert.Equal("Shape", (string?)shape.Attribute("Type"));
        Assert.Contains(shape.Descendants(), e => e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "Geometry");
        Assert.Contains(shape.Descendants(), e => e.Name.LocalName == "Cell" && (string?)e.Attribute("F") is { } f && f.StartsWith("Width*"));
        Assert.DoesNotContain(exp.Diagnostics, d => d.Severity == "warning");
        if (GeometryMap.IsApproximated(preset)) Assert.Contains(exp.Diagnostics, d => d.Code == "preset_approximated" && d.Severity == "info");
    }

    [Fact]
    public void ACustomPathIsKeptExactlyAndAnUnreadableOneIsReplacedWithAWarning()
    {
        const string path = "M 10 10 L 90 10 L 90 60 Q 50 100 10 60 Z";
        var ok = Doc.Shape(1, 100, 100, 120, 80, new { geometry = new { svgPath = path } });
        var (exp, back) = RT.Trip(Doc.Snapshot(Doc.Page([ok])));
        Assert.Equal(path, RT.El(back.Document, Doc.Id(1))["geometry"]!["svgPath"]!.GetValue<string>());
        Assert.Contains(exp.Diagnostics, d => d.Code == "curves_flattened");
        Assert.DoesNotContain(back.Diagnostics, d => d.Code == "outline_converted"); // it was ours, so it is not "converted"

        var bad = Doc.Shape(2, 100, 100, 120, 80, new { geometry = new { svgPath = "M 0 0 A 5 5 0 0 1 10 10" } });
        var exp2 = RT.Export(Doc.Snapshot(Doc.Page([bad])));
        Assert.Contains(exp2.Diagnostics, d => d.Code == "svg_path_not_exported" && d.Severity == "warning" && d.ElementId == Doc.Id(2));
        Assert.True(exp2.IsLossy);
    }

    [Fact]
    public void TextIsAnIndependentObjectWithItsFormatting()
    {
        var text = Doc.Text("Hello\nsecond line", new { fontFamily = "Arial", fontSizePt = 18, bold = true, italic = true, underline = true, colour = "#aa3300", horizontalAlign = "left", verticalAlign = "top", wrap = false, paddingPt = 9 });
        var el = JsonSerializer.SerializeToNode(new { id = Doc.Id(2), kind = "text", layerIds = Array.Empty<string>(), zIndex = 2, locked = false, hidden = false, bounds = new { x = 40, y = 40, width = 200, height = 50 }, rotationDeg = 0, metadata = new { }, text })!;
        var (exp, back) = RT.Trip(Doc.Snapshot(Doc.Page([(object)el])));
        var t = RT.El(back.Document, Doc.Id(2));
        Assert.Equal("text", t["kind"]!.GetValue<string>());
        var tb = t["text"]!;
        Assert.Equal("Hello\nsecond line", tb["value"]!.GetValue<string>());
        Assert.Equal("Arial", tb["fontFamily"]!.GetValue<string>());
        Near(18, tb["fontSizePt"]!.GetValue<double>());
        Assert.True(tb["bold"]!.GetValue<bool>() && tb["italic"]!.GetValue<bool>() && tb["underline"]!.GetValue<bool>());
        Assert.Equal("#aa3300", tb["colour"]!.GetValue<string>());
        Assert.Equal("left", tb["horizontalAlign"]!.GetValue<string>());
        Assert.Equal("top", tb["verticalAlign"]!.GetValue<string>());
        Assert.False(tb["wrap"]!.GetValue<bool>());
        Near(9, tb["paddingPt"]!.GetValue<double>());
        Assert.Contains(exp.Diagnostics, d => d.Code == "text_wrap_not_mapped"); // Visio cannot switch wrapping off per shape: said so
        // real, editable Visio text in the file
        var shape = Fx.Part(exp.Package, "visio/pages/page1.xml").Descendants().First(e => e.Name.LocalName == "Shape");
        Assert.Contains("Hello", shape.Elements().First(e => e.Name.LocalName == "Text").Value);
    }

    [Fact]
    public void FillLineDashAlphaAndTextInsideAShapeRoundTrip()
    {
        var el = Doc.Shape(1, 100, 100, 160, 90, new
        {
            style = new { fill = new { kind = "solid", colour = "#c8501e", opacity = 0.5 }, stroke = new { kind = "solid", colour = "#112233", opacity = 0.75 }, strokeWidthPt = 3.5, dash = "dashed", lineCap = "round", lineJoin = "round" },
        }, text: Doc.Text("Label", new { colour = "#ffffff" }));
        var (exp, back) = RT.Trip(Doc.Snapshot(Doc.Page([el])));
        var s = RT.El(back.Document, Doc.Id(1));
        Assert.Equal("#c8501e", s["style"]!["fill"]!["colour"]!.GetValue<string>());
        Near(0.5, s["style"]!["fill"]!["opacity"]!.GetValue<double>(), 1e-6);
        Assert.Equal("#112233", s["style"]!["stroke"]!["colour"]!.GetValue<string>());
        Near(0.75, s["style"]!["stroke"]!["opacity"]!.GetValue<double>(), 1e-6);
        Near(3.5, s["style"]!["strokeWidthPt"]!.GetValue<double>());
        Assert.Equal("dashed", s["style"]!["dash"]!.GetValue<string>());
        Assert.Equal("round", s["style"]!["lineCap"]!.GetValue<string>());
        Assert.Equal("Label", s["text"]!["value"]!.GetValue<string>());
        Assert.Contains(exp.Diagnostics, d => d.Code == "line_join_not_mapped"); // Visio has no join setting: an unknown effect is reported
    }

    [Fact]
    public void NoFillAndNoLineStayNone()
    {
        var el = Doc.Shape(1, 100, 100, 160, 90, new { style = new { fill = Doc.None, stroke = Doc.None, strokeWidthPt = 2, dash = "solid", lineCap = "butt", lineJoin = "miter" } });
        var s = RT.El(RT.Trip(Doc.Snapshot(Doc.Page([el]))).Back.Document, Doc.Id(1));
        Assert.Equal("none", s["style"]!["fill"]!["kind"]!.GetValue<string>());
        Assert.Equal("none", s["style"]!["stroke"]!["kind"]!.GetValue<string>());
        Near(2, s["style"]!["strokeWidthPt"]!.GetValue<double>());
    }

    [Theory]
    [InlineData(0.0)] [InlineData(33.0)] [InlineData(-90.0)] [InlineData(180.0)] [InlineData(271.0)]
    public void BoundsAndRotationRoundTripWithinNativeTolerance(double rotation)
    {
        var el = Doc.Shape(1, 123.456, 78.9, 140.25, 61.125, new { rotationDeg = rotation });
        var s = RT.El(RT.Trip(Doc.Snapshot(Doc.Page([el]))).Back.Document, Doc.Id(1));
        var b = s["bounds"]!;
        Near(123.456, b["x"]!.GetValue<double>()); Near(78.9, b["y"]!.GetValue<double>());
        Near(140.25, b["width"]!.GetValue<double>()); Near(61.125, b["height"]!.GetValue<double>());
        var back = s["rotationDeg"]!.GetValue<double>();
        var diff = ((back - rotation) % 360 + 540) % 360 - 180;
        Assert.True(Math.Abs(diff) <= Tol, $"rotation {rotation} came back as {back}");
    }

    [Fact]
    public void RotationIsClockwiseOnTheCanvasAndCounterClockwiseInVisio()
    {
        var exp = RT.Export(Doc.Snapshot(Doc.Page([Doc.Shape(1, 100, 100, 100, 50, new { rotationDeg = 33 })])));
        var angle = Fx.Part(exp.Package, "visio/pages/page1.xml").Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "Angle");
        Assert.Equal(-33 * Math.PI / 180, double.Parse((string)angle.Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture), 9);
    }

    [Fact]
    public void MetadataAliasNameLockAndHiddenSurvive()
    {
        var el = Doc.Shape(1, 100, 100, 100, 50, new { alias = "gateway", name = "Public gateway", locked = true, hidden = true, metadata = new { owner = "platform", tier = "1" } });
        var (exp, back) = RT.Trip(Doc.Snapshot(Doc.Page([el])));
        var s = RT.El(back.Document, Doc.Id(1));
        Assert.Equal("gateway", s["alias"]!.GetValue<string>());
        Assert.Equal("Public gateway", s["name"]!.GetValue<string>());
        Assert.True(s["locked"]!.GetValue<bool>());
        Assert.True(s["hidden"]!.GetValue<bool>());
        Assert.Equal("platform", s["metadata"]!["owner"]!.GetValue<string>());
        // locked in Visio terms too
        var cells = Fx.Part(exp.Package, "visio/pages/page1.xml").Descendants().Where(e => e.Name.LocalName == "Cell" && ((string?)e.Attribute("N"))?.StartsWith("Lock") == true).ToList();
        Assert.True(cells.Count >= 6);
    }

    [Fact]
    public void DocumentTitleIdAndMetadataSurvive()
    {
        var doc = Doc.Make([Doc.Page([Doc.Shape(1, 10, 10, 50, 30)])], title: "Quarterly architecture");
        const string capture = "{\"grid\":{\"spacingPt\":27,\"visible\":false,\"snap\":false},\"font\":{\"family\":\"Aptos\",\"sizePt\":19}}";
        var node = JsonSerializer.SerializeToNode(doc, ContractJson.Options)!;
        node["metadata"]!["editor.defaults.v1"] = capture;
        doc = node.Deserialize<DiagramDocument>(ContractJson.Options)!;
        var back = RT.Trip(Doc.Snapshot(doc)).Back.Document;
        Assert.Equal("Quarterly architecture", back.Title);
        Assert.Equal(Doc.DocId, back.Id);
        Assert.Equal("tests", back.Metadata["author"]);
        Assert.Equal(capture, back.Metadata["editor.defaults.v1"]);
        Assert.Equal(Doc.Id(1), RT.All(back).Single()["id"]!.GetValue<string>());
        Assert.Equal(0, back.Revision);
        Assert.Equal("vsdx", back.Source!.Format);
    }
}
