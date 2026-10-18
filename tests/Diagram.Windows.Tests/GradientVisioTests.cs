using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>R11: gradients made through MCP are drawn by the editor, exported as native Visio gradients, drawn by Visio itself, saved by Visio and read back.</summary>
public class GradientVisioTests
{
    [Fact]
    public async Task GradientsMadeThroughMcpAreNativeInVisioAndComeBackWithTheirAnglesAndStops()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(170)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r11-gradients"); Directory.CreateDirectory(evidence);
        var pipe = "grad-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "grad", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString() };
        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: ct); Assert.True(r.IsError != true, r.StructuredContent?.ToString()); return r;
        }
        var pageId = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0].GetProperty("id").GetString()!;
        object Paint(double angle, params (double o, string c, double a)[] s) => new { kind = "linear-gradient", angleDeg = angle, stops = s.Select(x => new { offset = x.o, colour = x.c, opacity = x.a }).ToArray() };
        var ops = new object[]
        {
            new { op = "createShape", pageId, alias = "across", bounds = new { x = 40, y = 40, width = 200, height = 60 }, style = new { fill = Paint(0, (0, "#ffffff", 1), (1, "#0000ff", 1)) }, text = new { value = "0 degrees", colour = "#000000" } },
            new { op = "createShape", pageId, alias = "down", bounds = new { x = 280, y = 40, width = 200, height = 60 }, style = new { fill = Paint(90, (0, "#ffffff", 1), (1, "#ff0000", 1)) }, text = new { value = "90 degrees", colour = "#000000" } },
            new { op = "createShape", pageId, alias = "diagonal", bounds = new { x = 40, y = 140, width = 200, height = 60 }, style = new { fill = Paint(45, (0, "#ffff00", 1), (0.5, "#00aa00", 1), (1, "#000066", 1)) }, text = new { value = "45 degrees, 3 stops", colour = "#ffffff" } },
            new { op = "createShape", pageId, alias = "seethrough", bounds = new { x = 280, y = 140, width = 200, height = 60 }, style = new { fill = Paint(0, (0, "#000000", 1), (1, "#000000", 0)) }, text = new { value = "fades out", colour = "#808080" } },
            new { op = "createShape", pageId, alias = "round", bounds = new { x = 40, y = 240, width = 100, height = 100 }, geometry = new { preset = "ellipse" }, style = new { fill = Paint(135, (0, "#ffffff", 1), (1, "#8800aa", 1)) } },
        };
        var apply = Identity(); apply["baseRevision"] = 0; apply["transactionId"] = Guid.NewGuid().ToString(); apply["operations"] = JsonSerializer.SerializeToElement(ops);
        await Call("apply_operations", apply);
        var render = Identity(); render["pageId"] = pageId; render["maxWidth"] = 1000; render["maxHeight"] = 700;
        await File.WriteAllBytesAsync(Path.Combine(evidence, "editor-render.png"), Assert.IsType<ImageContentBlock>((await Call("render_page", render)).Content.First()).DecodedData.ToArray(), ct);

        // a stroke may not be a gradient: the editor refuses it and says why
        var refused = Identity(); refused["baseRevision"] = 1; refused["transactionId"] = Guid.NewGuid().ToString();
        refused["operations"] = JsonSerializer.SerializeToElement(new object[] { new { op = "createShape", pageId, bounds = new { x = 10, y = 10, width = 20, height = 20 }, style = new { stroke = Paint(0, (0, "#ffffff", 1), (1, "#000000", 1)) } } });
        var bad = await client.CallToolAsync("apply_operations", refused, cancellationToken: ct);
        Assert.True(bad.IsError);
        Assert.Contains("fill a shape", bad.StructuredContent!.Value.GetRawText());

        File.Delete(Path.Combine(evidence, "gradients.vsdx")); // replacing an existing file needs the person's consent
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "gradients.vsdx"; var exported = await Call("export_document", export);
        var codes = exported.StructuredContent!.Value.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.DoesNotContain("gradient_approximated", codes);
        var path = Path.Combine(evidence, "gradients.vsdx");
        void CheckBack(string file)
        {
            using var s = File.OpenRead(file);
            var imported = new NativeVsdxImporter().Import(s, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
            var elements = JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray();
            JsonNode Fill(string alias) => elements.Single(e => e!["alias"]?.GetValue<string>() == alias)!["style"]!["fill"]!;
            Assert.Equal(0, Fill("across")["angleDeg"]!.GetValue<double>(), 2);
            Assert.Equal(90, Fill("down")["angleDeg"]!.GetValue<double>(), 2);
            Assert.Equal(45, Fill("diagonal")["angleDeg"]!.GetValue<double>(), 2);
            Assert.Equal(3, Fill("diagonal")["stops"]!.AsArray().Count);
            Assert.Equal(0.5, Fill("diagonal")["stops"]![1]!["offset"]!.GetValue<double>(), 3);
            Assert.Equal(0.0, Fill("seethrough")["stops"]![1]!["opacity"]!.GetValue<double>(), 3);
            Assert.Equal(135, Fill("round")["angleDeg"]!.GetValue<double>(), 2);
        }
        CheckBack(path);

        dynamic? visio = OfficeAcceptanceTests.StartVisio();
        if (visio is null) { await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio unavailable: native drawing not checked", ct); return; }
        var resaved = Path.Combine(evidence, "gradients-visio-saved.vsdx"); File.Delete(resaved);
        try
        {
            dynamic document = visio.Documents.Open(path);
            try
            {
                dynamic page = document.Pages.Item(1);
                Assert.Equal(5, (int)page.Shapes.Count);
                foreach (dynamic shape in page.Shapes) Assert.Equal(1, (int)shape.CellsU["FillGradientEnabled"].ResultIU); // Visio itself reads each as a gradient
                page.Export(Path.Combine(evidence, "visio.png"));
                document.SaveAs(resaved);
            }
            finally { document.Saved = true; document.Close(); }
        }
        finally { visio.Quit(); }
        CheckBack(resaved); // and what Visio saved still carries the same gradients
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "native Visio read all five shapes as gradients, drew them (visio.png) and re-saved them with the same angles, stops and transparency", ct);
    }
}
