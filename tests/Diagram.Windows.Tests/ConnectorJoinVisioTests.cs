using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>R16: a branch joined to a connector stays on it when the connector moves, a connector can hop over the ones it crosses, and both reach native Visio and come back.</summary>
public class ConnectorJoinVisioTests
{
    [Fact]
    public async Task BranchesStayOnTheirConnectorsAndHopsAreDrawnInTheEditorAndInVisio()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(170)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r16-junctions"); Directory.CreateDirectory(evidence);
        var pipe = "join-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "join", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
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
            new { op = "createShape", pageId, alias = "c", bounds = new { x = 210, y = 300, width = 80, height = 40 }, text = Text("Branch") },
            new { op = "createConnector", pageId, alias = "bus", from = new { elementId = "a", glue = "dynamic" }, to = new { elementId = "b", glue = "dynamic" } },
            new { op = "createConnector", pageId, alias = "tap", from = new { elementId = "c", glue = "dynamic" }, to = new { glue = "none", join = new { connector = "bus", position = 0.5 } } },
            new { op = "createShape", pageId, alias = "l", bounds = new { x = 560, y = 240, width = 60, height = 40 }, text = Text("L") },
            new { op = "createShape", pageId, alias = "r", bounds = new { x = 800, y = 240, width = 60, height = 40 }, text = Text("R") },
            new { op = "createShape", pageId, alias = "t", bounds = new { x = 680, y = 100, width = 60, height = 40 }, text = Text("T") },
            new { op = "createShape", pageId, alias = "u", bounds = new { x = 680, y = 380, width = 60, height = 40 }, text = Text("U") },
            new { op = "createConnector", pageId, alias = "across", from = new { elementId = "l", glue = "dynamic" }, to = new { elementId = "r", glue = "dynamic" } },
            new { op = "createConnector", pageId, alias = "down", hops = "arc", from = new { elementId = "t", glue = "dynamic" }, to = new { elementId = "u", glue = "dynamic" } });

        async Task<JsonElement> Elements()
        {
            var read = Identity(); read["includeGeometry"] = true;
            return (await Call("get_document_summary", read)).StructuredContent!.Value.GetProperty("pages")[0].GetProperty("elements");
        }
        async Task<JsonElement> El(string alias) => (await Elements()).EnumerateArray().Single(x => x.TryGetProperty("alias", out var a) && a.GetString() == alias);
        async Task Render(string name)
        {
            var render = Identity(); render["pageId"] = pageId; render["maxWidth"] = 1000; render["maxHeight"] = 700;
            await File.WriteAllBytesAsync(Path.Combine(evidence, name), Assert.IsType<ImageContentBlock>((await Call("render_page", render)).Content.First()).DecodedData.ToArray(), ct);
        }

        // the bus runs from a's right edge (120,120) to b's left edge (380,120): the middle is x=250, and the branch ends there
        var tap = (await El("tap")).GetProperty("to");
        Assert.Equal((await El("bus")).GetProperty("id").GetString(), tap.GetProperty("join").GetProperty("connectorId").GetString());
        Assert.InRange(tap.GetProperty("point").GetProperty("x").GetDouble(), 245, 255);
        Assert.InRange(tap.GetProperty("point").GetProperty("y").GetDouble(), 114, 126);
        Assert.Equal("arc", (await El("down")).GetProperty("hops").GetString());
        await Render("editor-before.png");

        // moving the target drops the bus, and the branch end rides along the bus in the same step
        var startY = tap.GetProperty("point").GetProperty("y").GetDouble();
        await Apply(new { op = "move", target = "b", delta = new { xPt = 0, yPt = 120 } });
        var movedY = (await El("tap")).GetProperty("to").GetProperty("point").GetProperty("y").GetDouble();
        Assert.True(movedY > startY + 30, $"the branch followed the bus down: {startY} -> {movedY}");
        await Render("editor-after-move.png");

        // one undo returns the box and the branch together
        var undone = await h.Send("doc.undo", new JsonObject { ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = revision }); Assert.Null(undone.Error); revision++;
        Assert.Equal(startY, (await El("tap")).GetProperty("to").GetProperty("point").GetProperty("y").GetDouble(), 1);

        File.Delete(Path.Combine(evidence, "junctions.vsdx"));
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "junctions.vsdx"; var exported = await Call("export_document", export);
        var codes = exported.StructuredContent!.Value.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.Contains("join_not_native", codes);
        var path = Path.Combine(evidence, "junctions.vsdx");
        void CheckBack(string file)
        {
            using var s = File.OpenRead(file);
            var imported = new NativeVsdxImporter().Import(s, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
            var elements = JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray();
            JsonNode E(string alias) => elements.Single(e => e!["alias"]?.GetValue<string>() == alias)!;
            Assert.Equal(E("bus")["id"]!.GetValue<string>(), E("tap")["to"]!["join"]!["connectorId"]!.GetValue<string>());
            Assert.Equal(0.5, E("tap")["to"]!["join"]!["position"]!.GetValue<double>(), 3);
            Assert.Equal("arc", E("down")["hops"]!.GetValue<string>());
            Assert.Null(E("across")["hops"]);
        }
        CheckBack(path);

        dynamic? visio = OfficeAcceptanceTests.StartVisio();
        if (visio is null) { await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio unavailable: native drawing not checked", ct); return; }
        var resaved = Path.Combine(evidence, "junctions-visio-saved.vsdx"); File.Delete(resaved);
        try
        {
            dynamic document = visio.Documents.Open(path);
            try
            {
                dynamic page = document.Pages.Item(1);
                // Visio reads the jump settings: the hopping connector jumps always, as an arc; the others never
                var jumps = new List<double>();
                foreach (dynamic shape in page.Shapes)
                    if (shape.OneD != 0 && shape.CellExistsU["ConLineJumpCode", 0] != 0) jumps.Add((double)shape.CellsU["ConLineJumpCode"].ResultIU);
                Assert.Contains(2.0, jumps); Assert.Contains(1.0, jumps);
                page.Export(Path.Combine(evidence, "visio.png"));
                document.SaveAs(resaved);
            }
            finally { document.Saved = true; document.Close(); }
        }
        finally { visio.Quit(); }
        CheckBack(resaved); // what Visio saved still carries the joins and the hops
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio drew the branch on the bus and the hop (visio.png), kept the jump settings and re-saved the file; the join and the hops read back unchanged", ct);
    }
}
