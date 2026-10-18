using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;
public class LayoutComponentTests
{
    [Fact]
    public async Task RealMcpComponentsAreReadOnlyUntilAppliedAndRemainReflowableAfterNativeRoundTrip()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r04-components"); Directory.CreateDirectory(evidence);
        var pipe = "components-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "components", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString() };
        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: ct); Assert.True(r.IsError != true, r.StructuredContent?.ToString()); return r;
        }
        var initial = (await h.Send("doc.snapshot")).Result!.Value; var pageId = initial.GetProperty("document").GetProperty("pages")[0].GetProperty("id").GetString()!;
        var resize = Identity(); resize["baseRevision"] = 0; resize["transactionId"] = Guid.NewGuid().ToString(); resize["operations"] = JsonSerializer.SerializeToElement(new[] { new { op = "setPage", pageId, patch = new { widthPt = 1000, heightPt = 2200 } } });
        await Call("apply_operations", resize);
        var catalog = await Call("list_layout_components", Identity()); Assert.Equal(4, catalog.StructuredContent!.Value.GetProperty("components").GetArrayLength());
        var groupIds = new List<string>(); var textIds = new Dictionary<string, string>(); var revision = 1; double y = 32;
        foreach (var template in new[] { "repository-card", "labelled-container", "environment-columns", "pipeline" })
        {
            var before = (await h.Send("doc.snapshot")).Result!.Value.GetRawText();
            var args = Identity(); args["pageId"] = pageId; args["templateId"] = template; args["x"] = 32; args["y"] = y; args["layout"] = new { widthPt = 700, target = "visio" };
            var proposal = await Call("get_layout_component", args); var meta = proposal.StructuredContent!.Value;
            Assert.True(meta.GetProperty("preview").GetBoolean()); Assert.Equal(revision, meta.GetProperty("baseRevision").GetInt32());
            Assert.Equal(before, (await h.Send("doc.snapshot")).Result!.Value.GetRawText());
            groupIds.Add(meta.GetProperty("groupId").GetString()!);
            await File.WriteAllBytesAsync(Path.Combine(evidence, template + "-preview.png"), Assert.IsType<ImageContentBlock>(proposal.Content.First()).DecodedData.ToArray(), ct);
            var apply = Identity(); apply["baseRevision"] = revision; apply["transactionId"] = Guid.NewGuid().ToString(); apply["operations"] = meta.GetProperty("operations"); await Call("apply_operations", apply); revision++;
            y += meta.GetProperty("bounds").GetProperty("height").GetDouble() + 32;
        }
        var final = (await h.Send("doc.snapshot")).Result!.Value;
        foreach (var e in final.GetProperty("document").GetProperty("pages")[0].GetProperty("elements").EnumerateArray())
            if (e.GetProperty("kind").GetString() == "text") textIds[e.GetProperty("id").GetString()!] = e.GetProperty("text").GetProperty("value").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(evidence, "snapshot.json"), (await h.Send("doc.exportSnapshot")).Result!.Value.GetRawText(), ct);
        var renderArgs = Identity(); renderArgs["pageId"] = pageId; renderArgs["maxWidth"] = 1200; renderArgs["maxHeight"] = 2000;
        var rendered = await Call("render_page", renderArgs); await File.WriteAllBytesAsync(Path.Combine(evidence, "editor.png"), Assert.IsType<ImageContentBlock>(rendered.Content.First()).DecodedData.ToArray(), ct);
        var output = Path.Combine(evidence, "components.vsdx"); File.Delete(output);
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "components.vsdx"; var exported = await Call("export_document", export);
        await File.WriteAllTextAsync(Path.Combine(evidence, "export.json"), exported.StructuredContent!.Value.GetRawText(), ct);
        using var stream = File.OpenRead(output); var imported = new NativeVsdxImporter().Import(stream, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
        var doc = JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!;
        var importedElements = doc["pages"]![0]!["elements"]!.AsArray();
        Assert.Equal(4, importedElements.Count(e => e!["kind"]!.GetValue<string>() == "group"));
        foreach (var pair in textIds) Assert.Equal(pair.Value, importedElements.First(e => e!["id"]!.GetValue<string>() == pair.Key)!["text"]!["value"]!.GetValue<string>());
        var replace = await h.Send("doc.replace", new JsonObject { ["document"] = doc, ["baseRevision"] = revision }); Assert.Null(replace.Error);
        summary = (await Call("get_document_summary", new())).StructuredContent!.Value;
        foreach (var id in groupIds)
        {
            var args = Identity(); args["pageId"] = pageId; args["componentId"] = id; args["layout"] = new { widthPt = 680 };
            var reflow = await Call("get_layout_component", args); Assert.Equal(id, reflow.StructuredContent!.Value.GetProperty("groupId").GetString());
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "native-roundtrip.json"), JsonSerializer.Serialize(new { groupCount = 4, textCount = textIds.Count, exactText = true, reflowableAfterNativeImport = true }), ct);
    }
}
