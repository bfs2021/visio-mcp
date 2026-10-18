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
        var catalog = await Call("list_layout_components", Identity()); Assert.Equal(5, catalog.StructuredContent!.Value.GetProperty("components").GetArrayLength());
        var groupIds = new List<string>(); var textIds = new Dictionary<string, string>(); var revision = 1; double y = 32;
        foreach (var template in new[] { "repository-card", "labelled-container", "environment-columns", "pipeline", "swimlane" })
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
        Assert.Equal(5, importedElements.Count(e => e!["kind"]!.GetValue<string>() == "group"));
        foreach (var pair in textIds) Assert.Equal(pair.Value, importedElements.First(e => e!["id"]!.GetValue<string>() == pair.Key)!["text"]!["value"]!.GetValue<string>());
        var replace = await h.Send("doc.replace", new JsonObject { ["document"] = doc, ["baseRevision"] = revision }); Assert.Null(replace.Error);
        summary = (await Call("get_document_summary", new())).StructuredContent!.Value;
        foreach (var id in groupIds)
        {
            var args = Identity(); args["pageId"] = pageId; args["componentId"] = id; args["layout"] = new { widthPt = 680 };
            var reflow = await Call("get_layout_component", args); Assert.Equal(id, reflow.StructuredContent!.Value.GetProperty("groupId").GetString());
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "native-roundtrip.json"), JsonSerializer.Serialize(new { groupCount = 5, textCount = textIds.Count, exactText = true, reflowableAfterNativeImport = true }), ct);
    }

    [Fact]
    public async Task ASwimlaneIsCreatedThroughMcpReportsMembershipIsUnderstoodByInspectionAndSurvivesNativeRoundTrip()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r09-swimlane"); Directory.CreateDirectory(evidence);
        var pipe = "swimlane-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "swimlane", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
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
        async Task<JsonElement> Apply(object operations)
        {
            var a = Identity(); a["baseRevision"] = revision; a["transactionId"] = Guid.NewGuid().ToString(); a["operations"] = JsonSerializer.SerializeToElement(operations);
            var r = (await Call("apply_operations", a)).StructuredContent!.Value; revision = r.GetProperty("transaction").GetProperty("revision").GetInt32(); return r;
        }
        JsonElement Find(string alias) => summary.GetProperty("pages")[0].GetProperty("elements").EnumerateArray().Single(e => e.TryGetProperty("alias", out var a) && a.GetString() == alias);

        // the PSGL shape: five lanes and three day columns
        var before = (await h.Send("doc.snapshot")).Result!.Value.GetRawText();
        var args = Identity(); args["pageId"] = pageId; args["templateId"] = "swimlane"; args["x"] = 20; args["y"] = 20; args["layout"] = new { widthPt = 780 };
        args["content"] = new
        {
            title = "PSGL Dataflow",
            lanes = new object[] { new { title = "Corona", heightPt = 70 }, new { title = "Sensical", heightPt = 70 }, new { title = "Pipeline", heightPt = 70 }, new { title = "Data lake", heightPt = 70 }, new { title = "Reporting", heightPt = 70 } },
            phases = new[] { new { title = "Day 1" }, new { title = "Day 2" }, new { title = "Day 3" } },
        };
        var proposal = await Call("get_layout_component", args); var meta = proposal.StructuredContent!.Value;
        Assert.Equal(before, (await h.Send("doc.snapshot")).Result!.Value.GetRawText()); // a preview changes nothing
        await File.WriteAllBytesAsync(Path.Combine(evidence, "swimlane-preview.png"), Assert.IsType<ImageContentBlock>(proposal.Content.First()).DecodedData.ToArray(), ct);
        await Apply(meta.GetProperty("operations"));
        var groupId = meta.GetProperty("groupId").GetString()!;

        // content goes on top: a step in lane 3 / day 2, one in lane 5 / day 3, and two that really collide
        await Apply(new object[]
        {
            new { op = "createShape", pageId, alias = "transform", bounds = new { x = 330, y = 200, width = 110, height = 40 }, geometry = new { preset = "rounded-rectangle" } },
            new { op = "createShape", pageId, alias = "report", bounds = new { x = 600, y = 372, width = 110, height = 40 }, geometry = new { preset = "rounded-rectangle" } },
            new { op = "createShape", pageId, alias = "clash-a", bounds = new { x = 140, y = 95, width = 100, height = 40 }, geometry = new { preset = "rectangle" } },
            new { op = "createShape", pageId, alias = "clash-b", bounds = new { x = 160, y = 105, width = 100, height = 40 }, geometry = new { preset = "rectangle" } },
        });
        summary = (await Call("get_document_summary", new())).StructuredContent!.Value;
        Assert.Equal("Pipeline", Find("transform").GetProperty("lane").GetProperty("lane").GetString());
        Assert.Equal("Day 2", Find("transform").GetProperty("lane").GetProperty("phase").GetString());
        Assert.Equal("Reporting", Find("report").GetProperty("lane").GetProperty("lane").GetString());
        Assert.Equal("Day 3", Find("report").GetProperty("lane").GetProperty("phase").GetString());
        Assert.Equal(5, summary.GetProperty("pages")[0].GetProperty("swimlanes")[0].GetProperty("lanes").GetArrayLength());

        // inspection: the two real collisions, none of the structure
        var inspected = (await Call("inspect_layout", Identity())).StructuredContent!.Value;
        var issues = inspected.GetProperty("issues").EnumerateArray().ToList();
        var overlaps = issues.Where(i => i.GetProperty("code").GetString() == "OVERLAP").ToList();
        Assert.Single(overlaps);
        Assert.Contains(Find("clash-a").GetProperty("id").GetString(), overlaps[0].GetProperty("elementIds").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(issues, i => i.GetProperty("code").GetString() == "ZERO_SIZE");
        await File.WriteAllTextAsync(Path.Combine(evidence, "layout.json"), inspected.GetRawText(), ct);
        var renderArgs = Identity(); renderArgs["pageId"] = pageId; renderArgs["maxWidth"] = 1400; renderArgs["maxHeight"] = 1000;
        var rendered = await Call("render_page", renderArgs);
        await File.WriteAllBytesAsync(Path.Combine(evidence, "editor.png"), Assert.IsType<ImageContentBlock>(rendered.Content.First()).DecodedData.ToArray(), ct);

        // reflow to another width keeps every id, then undo returns the previous layout
        var reflowArgs = Identity(); reflowArgs["pageId"] = pageId; reflowArgs["componentId"] = groupId; reflowArgs["layout"] = new { widthPt = 640 };
        var reflow = (await Call("get_layout_component", reflowArgs)).StructuredContent!.Value;
        Assert.Equal(groupId, reflow.GetProperty("groupId").GetString());
        var layoutBefore = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages").GetRawText();
        await Apply(reflow.GetProperty("operations"));
        Assert.NotEqual(layoutBefore, (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages").GetRawText());
        var undo = await h.Send("doc.undo", new JsonObject { ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = revision }); Assert.Null(undo.Error);
        revision = undo.Result!.Value.GetProperty("revision").GetInt32();
        Assert.Equal(layoutBefore, (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages").GetRawText()); // the pages are exactly as they were; the revision counter only moves forward

        // native Visio file: written, read back, still a swim-lane that reports membership and reflows
        var output = Path.Combine(evidence, "swimlane.vsdx"); File.Delete(output);
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "swimlane.vsdx"; var exported = await Call("export_document", export);
        await File.WriteAllTextAsync(Path.Combine(evidence, "export.json"), exported.StructuredContent!.Value.GetRawText(), ct);
        using var stream = File.OpenRead(output); var imported = new NativeVsdxImporter().Import(stream, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
        var doc = JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!;
        var replace = await h.Send("doc.replace", new JsonObject { ["document"] = doc, ["baseRevision"] = revision }); Assert.Null(replace.Error);
        summary = (await Call("get_document_summary", new())).StructuredContent!.Value;
        Assert.Equal("Pipeline", Find("transform").GetProperty("lane").GetProperty("lane").GetString());
        Assert.Equal("Day 3", Find("report").GetProperty("lane").GetProperty("phase").GetString());
        var again = Identity(); again["pageId"] = pageId; again["componentId"] = groupId; again["layout"] = new { widthPt = 700 };
        Assert.Equal(groupId, (await Call("get_layout_component", again)).StructuredContent!.Value.GetProperty("groupId").GetString());
    }
}
