using System.IO;
using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace Diagram.Windows.Tests;

public class ReferenceDiagramTextFitTests
{
    [Fact]
    public async Task ReferenceArrowsFitThroughMeasuredProposalAndExportWithoutChangingOtherObjects()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts", "r02-text-fit");
        Directory.CreateDirectory(evidence);
        var pipe = "reference-fit-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "reference-fit", Command = "dotnet",
            Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct.Token);
        async Task<JsonElement> Call(string name, Dictionary<string, object?>? args = null)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: ct.Token);
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            return r.StructuredContent!.Value;
        }
        var identity = await Call("get_document_summary");
        Dictionary<string, object?> Scoped() => new() { ["documentId"] = identity.GetProperty("documentId").GetString(), ["sessionId"] = identity.GetProperty("sessionId").GetString() };
        var operations = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Harness.RepoRoot, "tests", "fixtures", "diagrams", "repository-deployment-light.operations.json")));
        using (operations)
        {
            var args = Scoped(); args["transactionId"] = Guid.NewGuid().ToString(); args["baseRevision"] = 0; args["operations"] = operations.RootElement;
            await Call("apply_operations", args);
        }
        var before = await Call("get_document_summary");
        var page = before.GetProperty("pages").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Repository deployment · light");
        var pageId = page.GetProperty("id").GetString()!;
        var arrowIds = page.GetProperty("elements").EnumerateArray()
            .Where(e => e.TryGetProperty("alias", out var a) && a.GetString() is "release-left-arrows" or "release-right-arrows")
            .Select(e => e.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(2, arrowIds.Length);
        var allIds = page.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
        var read = Scoped(); read["ids"] = allIds;
        var original = (await Call("get_objects", read)).GetProperty("objects").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetRawText());
        // Fixed narrow gutters are better served by shrinking glyphs than growing into the neighbouring card.
        var measure = Scoped(); measure["ids"] = arrowIds; measure["target"] = "visio"; measure["strategy"] = "shrink_font";
        var proposal = await Call("get_text_layout", measure);
        Assert.Equal(2, proposal.GetProperty("operations").GetArrayLength());
        await File.WriteAllTextAsync(Path.Combine(evidence, "proposal.json"), proposal.GetRawText());
        var apply = Scoped(); apply["baseRevision"] = proposal.GetProperty("revision").GetInt32(); apply["transactionId"] = Guid.NewGuid().ToString(); apply["operations"] = proposal.GetProperty("operations");
        await Call("apply_operations", apply);
        var after = await Call("get_text_layout", measure);
        Assert.All(after.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("current").GetProperty("fits").GetBoolean()));
        Assert.Empty(after.GetProperty("operations").EnumerateArray());
        var finalObjects = (await Call("get_objects", read)).GetProperty("objects").EnumerateArray().ToArray();
        Assert.Equal(62, finalObjects.Length);
        foreach (var e in finalObjects)
        {
            var id = e.GetProperty("id").GetString()!;
            if (!arrowIds.Contains(id)) Assert.Equal(original[id], e.GetRawText());
        }
        var render = Scoped(); render["pageId"] = pageId; render["maxWidth"] = 1600; render["maxHeight"] = 1300;
        var picture = await client.CallToolAsync("render_page", render, cancellationToken: ct.Token);
        Assert.True(picture.IsError != true);
        var image = Assert.IsType<ModelContextProtocol.Protocol.ImageContentBlock>(picture.Content.First());
        await File.WriteAllBytesAsync(Path.Combine(evidence, "editor.png"), image.DecodedData.ToArray());
        var export = Scoped(); export["format"] = "vsdx"; export["path"] = "reference-text-fit.vsdx";
        var exported = await Call("export_document", export);
        await File.WriteAllTextAsync(Path.Combine(evidence, "export.json"), exported.GetRawText());
        Assert.True(File.Exists(exported.GetProperty("path").GetString()));
    }
}
