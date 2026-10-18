using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>R10: the flowchart symbols through the real editor and MCP server, into a native .vsdx drawn by Visio itself, and back.</summary>
public class FlowchartVisioTests
{
    [Fact]
    public async Task SymbolsDrawnInTheEditorAreNativeVisioOutlinesThatVisioDrawsAndTheyComeBackAsPresets()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(170)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r10-flowchart"); Directory.CreateDirectory(evidence);
        var pipe = "flow-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "flow", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString() };
        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: ct); Assert.True(r.IsError != true, r.StructuredContent?.ToString()); return r;
        }
        var pageId = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0].GetProperty("id").GetString()!;

        // the four symbols at two shapes each, with text, a process and a decision for company, and two connectors on their ports
        var presets = new[] { "terminator", "stored-data", "document", "lined-document" };
        var ops = new List<object>();
        for (var i = 0; i < presets.Length; i++)
        {
            ops.Add(new { op = "createShape", pageId, alias = presets[i] + "-wide", bounds = new { x = 40 + i * 190, y = 60, width = 160, height = 60 }, geometry = new { preset = presets[i] }, text = new { value = presets[i] } });
            ops.Add(new { op = "createShape", pageId, alias = presets[i] + "-tall", bounds = new { x = 60 + i * 190, y = 170, width = 80, height = 110 }, geometry = new { preset = presets[i] }, text = new { value = "tall" } });
        }
        ops.Add(new { op = "createShape", pageId, alias = "process", bounds = new { x = 40, y = 330, width = 120, height = 60 }, geometry = new { preset = "rectangle" }, text = new { value = "Process" } });
        ops.Add(new { op = "createShape", pageId, alias = "decision", bounds = new { x = 230, y = 320, width = 120, height = 80 }, geometry = new { preset = "diamond" }, text = new { value = "Decision" } });
        var apply = Identity(); apply["baseRevision"] = 0; apply["transactionId"] = Guid.NewGuid().ToString(); apply["operations"] = JsonSerializer.SerializeToElement(ops);
        await Call("apply_operations", apply);
        summary = (await Call("get_document_summary", new())).StructuredContent!.Value;
        string IdOf(string alias) => summary.GetProperty("pages")[0].GetProperty("elements").EnumerateArray().Single(e => e.TryGetProperty("alias", out var a) && a.GetString() == alias).GetProperty("id").GetString()!;
        var snapshot = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0].GetProperty("elements").EnumerateArray().ToList();
        foreach (var p in presets) // each symbol arrived with its four connection points
            Assert.Equal(4, snapshot.Single(e => e.TryGetProperty("alias", out var a) && a.GetString() == p + "-wide").GetProperty("ports").GetArrayLength());

        var render = Identity(); render["pageId"] = pageId; render["maxWidth"] = 1000; render["maxHeight"] = 700;
        var rendered = await Call("render_page", render);
        await File.WriteAllBytesAsync(Path.Combine(evidence, "editor.png"), Assert.IsType<ImageContentBlock>(rendered.Content.First()).DecodedData.ToArray(), ct);

        File.Delete(Path.Combine(evidence, "symbols.vsdx")); // replacing an existing file needs the person's consent
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "symbols.vsdx"; var exported = await Call("export_document", export);
        var diagnostics = exported.StructuredContent!.Value.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.DoesNotContain("preset_approximated", diagnostics); // native outlines, nothing approximated
        Assert.DoesNotContain("curves_flattened", diagnostics);
        var path = Path.Combine(evidence, "symbols.vsdx");
        using (var stream = File.OpenRead(path))
        {
            var imported = new NativeVsdxImporter().Import(stream, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
            var elements = JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray();
            foreach (var p in presets) foreach (var suffix in new[] { "wide", "tall" })
                Assert.Equal(p, elements.Single(e => e!["alias"]?.GetValue<string>() == $"{p}-{suffix}")!["geometry"]!["preset"]!.GetValue<string>());
        }

        // Visio itself draws the file, saves it, and what it saved still reads back as the same presets
        dynamic? visio = OfficeAcceptanceTests.StartVisio();
        if (visio is null) { await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio unavailable: native drawing not checked", ct); return; }
        var resaved = Path.Combine(evidence, "symbols-visio-saved.vsdx"); File.Delete(resaved);
        try
        {
            dynamic document = visio.Documents.Open(path);
            try
            {
                dynamic page = document.Pages.Item(1);
                Assert.Equal(10, (int)page.Shapes.Count);
                page.Export(Path.Combine(evidence, "visio.png"));
                document.SaveAs(resaved);
            }
            finally { document.Saved = true; document.Close(); }
        }
        finally { visio.Quit(); }
        using var saved = File.OpenRead(resaved);
        var again = new NativeVsdxImporter().Import(saved, new ImportOptions()); Assert.True(again.IsOk, again.Error?.Message);
        var back = JsonSerializer.SerializeToNode(again.Value.Document, ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray();
        foreach (var p in presets) foreach (var suffix in new[] { "wide", "tall" })
        {
            var e = back.Single(x => x!["alias"]?.GetValue<string>() == $"{p}-{suffix}")!;
            Assert.Equal(p, e["geometry"]!["preset"]!.GetValue<string>());
            Assert.Equal(4, e["ports"]!.AsArray().Count);
        }
        Assert.NotEqual(IdOf("process"), IdOf("decision"));
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "native Visio drew and re-saved the file; all eight symbols are presets again with four ports each", ct);
    }
}
