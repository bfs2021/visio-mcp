using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

namespace Diagram.Visio.Tests;

/// <summary>R13: a connector's label position and the shapes pinned to a connector survive the trip through Visio, with the limits reported.</summary>
public class ConnectorAttachmentTests
{
    static object Attach(int connectorN, double position, double offsetPt) => new { connectorId = Doc.Id(connectorN), position, offsetPt };

    static ExportSnapshot Scene(object? connectorOver = null, bool route = false)
    {
        var elements = new object[]
        {
            Doc.Shape(1, 40, 100, 60, 40, new { alias = "a" }),
            Doc.Shape(2, 340, 100, 60, 40, new { alias = "b" }),
            Doc.Connector(10, Doc.Glue(1), Doc.Glue(2), "straight", connectorOver),
            Doc.Shape(3, 150, 105, 60, 30, new { alias = "tag", attachment = Attach(10, 0.5, 0) }),
            TextEl(4, 150, 40, new { alias = "note", attachment = Attach(10, 0.25, -20) }),
        };
        return Doc.Snapshot(Doc.Make([Doc.Page(elements)]));
    }

    [Fact]
    public void AShapeAndATextBoxPinnedToAConnectorComeBackPinnedWithTheirPositions()
    {
        var (exported, back) = RT.Trip(Scene());
        Assert.Contains(exported.Diagnostics, d => d.Code == "attachment_not_native" && d.Severity == "info");
        Assert.DoesNotContain(exported.Diagnostics, d => d.Severity == "warning");
        var tag = RT.El(back.Document, Doc.Id(3))["attachment"]!;
        Assert.Equal(Doc.Id(10), tag["connectorId"]!.GetValue<string>());
        Assert.Equal(0.5, tag["position"]!.GetValue<double>(), 6);
        var note = RT.El(back.Document, Doc.Id(4))["attachment"]!;
        Assert.Equal(0.25, note["position"]!.GetValue<double>(), 6);
        Assert.Equal(-20, note["offsetPt"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void ALabelPositionIsWrittenAsTheTextPinAndComesBack()
    {
        var (exported, back) = RT.Trip(Scene(new { label = TextBlock("feed"), labelAnchor = new { position = 0.25, offsetPt = -12.0 } }));
        var anchor = RT.El(back.Document, Doc.Id(10))["labelAnchor"]!;
        Assert.Equal(0.25, anchor["position"]!.GetValue<double>(), 6);
        Assert.Equal(-12, anchor["offsetPt"]!.GetValue<double>(), 6);
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        string Cell(string n) => (string?)xml.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == n).Attribute("V") ?? "";
        // the line runs from the first shape's edge (100,120) to the second's (340,120); a quarter along is 60pt from its start
        var pinX = double.Parse(Cell("TxtPinX"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(60.0 / 72, pinX, 4);
        var pinY = double.Parse(Cell("TxtPinY"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(12.0 / 72, pinY, 4); // 12pt to the left of travel is up the page, which is the positive way in Visio
        Assert.Contains(exported.Diagnostics, d => d.Code == "label_position_fixed" && d.Severity == "info");
    }

    [Fact]
    public void ADamagedPinIsIgnoredAndAPinToAMissingConnectorIsReported()
    {
        var (exported, _) = RT.Trip(Scene());
        var broken = Fx.Edit(exported.Package, "visio/pages/page1.xml", d =>
        {
            foreach (var row in d.Descendants().Where(e => e.Name.LocalName == "Row" && (string?)e.Attribute("N") == "AgentAttachment"))
                row.Elements().First(c => (string?)c.Attribute("N") == "Value").SetAttributeValue("V", "{not json");
        });
        var back = new NativeVsdxImporter().Import(new MemoryStream(broken), new ImportOptions());
        Assert.True(back.IsOk, back.Error?.Message);
        Assert.Null(RT.El(back.Value.Document, Doc.Id(3))["attachment"]);

        var gone = Fx.Edit(exported.Package, "visio/pages/page1.xml", d =>
        {
            foreach (var row in d.Descendants().Where(e => e.Name.LocalName == "Row" && (string?)e.Attribute("N") == "AgentAttachment"))
                row.Elements().First(c => (string?)c.Attribute("N") == "Value").SetAttributeValue("V", $"{{\"connectorId\":\"{Doc.Id(99)}\",\"position\":0.5,\"offsetPt\":0}}");
        });
        var again = new NativeVsdxImporter().Import(new MemoryStream(gone), new ImportOptions());
        Assert.Null(RT.El(again.Value.Document, Doc.Id(3))["attachment"]);
        Assert.Contains(again.Value.Diagnostics, d => d.Code == "attachment_connector_missing");
    }

    static object TextEl(int n, double x, double y, object over)
    {
        var o = System.Text.Json.JsonSerializer.SerializeToNode(new { id = Doc.Id(n), kind = "text", layerIds = Array.Empty<string>(), zIndex = n, locked = false, hidden = false, bounds = new { x, y, width = 60.0, height = 20.0 }, rotationDeg = 0, metadata = new { }, text = TextBlock("Report") })!.AsObject();
        foreach (var kv in System.Text.Json.JsonSerializer.SerializeToNode(over)!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    static object TextBlock(string value) => new { value, fontFamily = "Segoe UI", fontSizePt = 12, bold = false, italic = false, underline = false, colour = "#000000", horizontalAlign = "center", verticalAlign = "middle", wrap = true, paddingPt = 4 };
}
