using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>R13: a label placed along a connector and a shape pinned to one are drawn there by the editor, follow the connector when its end moves, survive undo, and land in the same place in Visio.</summary>
public class ConnectorAttachmentVisioTests
{
    [Fact]
    public async Task LabelsAndPinnedShapesSitOnTheirConnectorsAndFollowThemInTheEditorAndInVisio()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(170)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r13-connector-attachments"); Directory.CreateDirectory(evidence);
        var pipe = "att-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "att", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString() };
        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: ct); Assert.True(r.IsError != true, r.StructuredContent?.ToString()); return r;
        }
        var pageId = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0].GetProperty("id").GetString()!;
        int revision = 0;
        async Task<JsonElement> Apply(params object[] ops)
        {
            var apply = Identity(); apply["baseRevision"] = revision; apply["transactionId"] = Guid.NewGuid().ToString(); apply["operations"] = JsonSerializer.SerializeToElement(ops);
            var r = await Call("apply_operations", apply); revision++; return r.StructuredContent!.Value;
        }
        object Text(string value) => new { value, fontSizePt = 11, colour = "#111111" };
        await Apply(
            new { op = "createShape", pageId, alias = "a", bounds = new { x = 40, y = 100, width = 80, height = 40 }, text = Text("Source") },
            new { op = "createShape", pageId, alias = "b", bounds = new { x = 380, y = 100, width = 80, height = 40 }, text = Text("Target") },
            new { op = "createConnector", pageId, alias = "feed", from = new { elementId = "a", glue = "dynamic" }, to = new { elementId = "b", glue = "dynamic" }, label = Text("feed"), labelAnchor = new { position = 0.25, offsetPt = -14 } },
            new { op = "createShape", pageId, alias = "report", geometry = new { preset = "document" }, bounds = new { x = 0, y = 0, width = 56, height = 36 }, text = Text("PSGL Report"), attachment = new { connector = "feed", position = 0.6 } },
            new { op = "createShape", pageId, alias = "c", bounds = new { x = 40, y = 260, width = 80, height = 40 }, text = Text("Left") },
            new { op = "createShape", pageId, alias = "d", bounds = new { x = 380, y = 360, width = 80, height = 40 }, text = Text("Right") },
            new { op = "createConnector", pageId, alias = "bend", route = "orthogonal", from = new { elementId = "c", glue = "dynamic" }, to = new { elementId = "d", glue = "dynamic" }, label = Text("bent"), labelAnchor = new { position = 0.5, offsetPt = 12 } },
            new { op = "createText", pageId, alias = "ontop", bounds = new { x = 0, y = 0, width = 60, height = 18 }, text = Text("on the bend"), attachment = new { connector = "bend", position = 0.8, offsetPt = 0 } });

        async Task<JsonElement> Elements()
        {
            var read = Identity(); read["includeGeometry"] = true;
            return (await Call("get_document_summary", read)).StructuredContent!.Value.GetProperty("pages")[0].GetProperty("elements");
        }
        async Task<(double X, double Y)> Centre(string alias)
        {
            var e = (await Elements()).EnumerateArray().Single(x => x.TryGetProperty("alias", out var a) && a.GetString() == alias).GetProperty("bounds");
            return (e.GetProperty("x").GetDouble() + e.GetProperty("width").GetDouble() / 2, e.GetProperty("y").GetDouble() + e.GetProperty("height").GetDouble() / 2);
        }
        async Task Render(string name)
        {
            var render = Identity(); render["pageId"] = pageId; render["maxWidth"] = 1000; render["maxHeight"] = 700;
            await File.WriteAllBytesAsync(Path.Combine(evidence, name), Assert.IsType<ImageContentBlock>((await Call("render_page", render)).Content.First()).DecodedData.ToArray(), ct);
        }

        // the straight connector runs from the first box's right edge (120,120) to the second's left edge (380,120): 60% along is x=276
        var report = await Centre("report");
        Assert.InRange(report.X, 270, 282); Assert.InRange(report.Y, 114, 126);
        // the pinned text box sits on the elbow connector's own drawn route, wherever maxGraph put it
        var ontop = await Centre("ontop");
        Assert.True(ontop.X > 40 && ontop.X < 460 && ontop.Y > 260 && ontop.Y < 400, $"on the bend: {ontop}");
        await Render("editor-before.png");

        // moving the target drops the line, and what is pinned to it follows in the same step
        var before = report;
        await Apply(new { op = "move", target = "b", delta = new { xPt = 0, yPt = 120 } });
        var moved = await Centre("report");
        Assert.True(moved.Y > before.Y + 40, $"the pinned shape followed the line down: {before} -> {moved}");
        await Render("editor-after-move.png");

        // one undo returns the box and the pinned shape together
        var undone = await h.Send("doc.undo", new JsonObject { ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = revision }); Assert.Null(undone.Error); revision++;
        var back = await Centre("report");
        Assert.Equal(before.X, back.X, 1); Assert.Equal(before.Y, back.Y, 1);

        File.Delete(Path.Combine(evidence, "attachments.vsdx"));
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "attachments.vsdx"; var exported = await Call("export_document", export);
        var codes = exported.StructuredContent!.Value.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.Contains("attachment_not_native", codes); Assert.Contains("label_position_fixed", codes);
        var path = Path.Combine(evidence, "attachments.vsdx");
        void CheckBack(string file)
        {
            using var s = File.OpenRead(file);
            var imported = new NativeVsdxImporter().Import(s, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
            var elements = JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray();
            JsonNode El(string alias) => elements.Single(e => e!["alias"]?.GetValue<string>() == alias)!;
            Assert.Equal(0.25, El("feed")["labelAnchor"]!["position"]!.GetValue<double>(), 3);
            Assert.Equal(-14, El("feed")["labelAnchor"]!["offsetPt"]!.GetValue<double>(), 3);
            Assert.Equal(El("feed")["id"]!.GetValue<string>(), El("report")["attachment"]!["connectorId"]!.GetValue<string>());
            Assert.Equal(0.6, El("report")["attachment"]!["position"]!.GetValue<double>(), 3);
            Assert.Equal(El("bend")["id"]!.GetValue<string>(), El("ontop")["attachment"]!["connectorId"]!.GetValue<string>());
        }
        CheckBack(path);

        dynamic? visio = OfficeAcceptanceTests.StartVisio();
        if (visio is null) { await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio unavailable: native drawing not checked", ct); return; }
        var resaved = Path.Combine(evidence, "attachments-visio-saved.vsdx"); File.Delete(resaved);
        try
        {
            dynamic document = visio.Documents.Open(path);
            try
            {
                dynamic page = document.Pages.Item(1);
                page.Export(Path.Combine(evidence, "visio.png"));
                document.SaveAs(resaved);
            }
            finally { document.Saved = true; document.Close(); }
        }
        finally { visio.Quit(); }
        CheckBack(resaved); // what Visio saved still carries the pins and the label positions
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio drew the labels and pinned shapes (visio.png) and re-saved them; the label positions and pins read back unchanged", ct);
    }
}
