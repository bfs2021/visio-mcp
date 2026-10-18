using System.IO;
using System.Text.Json;
using Diagram.Core.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

public class VectorIconTests
{
    [Fact]
    public async Task McpSearchAndPreviewAreReadOnlyAndProposalsCreateEditableVectors()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var ct = deadline.Token;
        var pipe = "icons-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "icons", Command = "dotnet",
            Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString() };
        var initial = (await h.Send("doc.snapshot")).Result!.Value;
        var pageId = initial.GetProperty("document").GetProperty("pages")[0].GetProperty("id").GetString();
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r03-icons"); Directory.CreateDirectory(evidence);
        var searchArgs = Identity(); searchArgs["limit"] = 50;
        var search = await client.CallToolAsync("search_icons", searchArgs, cancellationToken: ct);
        Assert.True(search.IsError != true, search.StructuredContent?.ToString());
        Assert.Equal(26, search.StructuredContent!.Value.GetProperty("total").GetInt32());
        Assert.False(search.StructuredContent!.Value.GetProperty("hasMore").GetBoolean());
        await File.WriteAllBytesAsync(Path.Combine(evidence, "catalog-preview.png"), Assert.IsType<ImageContentBlock>(search.Content.First()).DecodedData.ToArray(), ct);
        Assert.Equal(initial.GetRawText(), (await h.Send("doc.snapshot")).Result!.Value.GetRawText());
        var revision = 0;
        foreach (var name in new[] { "git-branch", "container" })
        {
            var args = Identity(); args["pageId"] = pageId; args["iconId"] = "lucide:" + name;
            args["sizePt"] = 48; args["colour"] = "#15803D"; args["strokeWidthPt"] = 2;
            args["x"] = 40 + revision * 80; args["y"] = 40;
            args["catalogVersion"] = search.StructuredContent!.Value.GetProperty("catalogVersion").GetString();
            var before = (await h.Send("doc.snapshot")).Result!.Value.GetRawText();
            var proposal = await client.CallToolAsync("get_icon", args, cancellationToken: ct);
            Assert.True(proposal.IsError != true, proposal.StructuredContent?.ToString());
            var meta = proposal.StructuredContent!.Value;
            Assert.True(meta.GetProperty("preview").GetBoolean()); Assert.Equal(revision, meta.GetProperty("baseRevision").GetInt32());
            Assert.Equal(before, (await h.Send("doc.snapshot")).Result!.Value.GetRawText());
            await File.WriteAllBytesAsync(Path.Combine(evidence, name + "-preview.png"), Assert.IsType<ImageContentBlock>(proposal.Content.First()).DecodedData.ToArray(), ct);
            var apply = Identity(); apply["baseRevision"] = revision; apply["transactionId"] = Guid.NewGuid().ToString(); apply["operations"] = meta.GetProperty("operations");
            var applied = await client.CallToolAsync("apply_operations", apply, cancellationToken: ct);
            Assert.True(applied.IsError != true, applied.StructuredContent?.ToString()); revision++;
            var snap = (await h.Send("doc.snapshot")).Result!.Value;
            var element = snap.GetProperty("document").GetProperty("pages")[0].GetProperty("elements")[revision - 1];
            Assert.Equal("shape", element.GetProperty("kind").GetString());
            Assert.Equal("lucide:" + name, element.GetProperty("metadata").GetProperty("icon.id").GetString());
            Assert.Equal(48, element.GetProperty("bounds").GetProperty("width").GetDouble());
            Assert.Equal("#15803D", element.GetProperty("style").GetProperty("stroke").GetProperty("colour").GetString());
            Assert.Equal(2, element.GetProperty("style").GetProperty("strokeWidthPt").GetDouble());
            args["catalogVersion"] = "unavailable";
            Assert.True((await client.CallToolAsync("get_icon", args, cancellationToken: ct)).IsError);
        }
        var renderArgs = Identity(); renderArgs["pageId"] = pageId;
        var render = await client.CallToolAsync("render_page", renderArgs, cancellationToken: ct);
        Assert.True(render.IsError != true, render.StructuredContent?.ToString());
        await File.WriteAllBytesAsync(Path.Combine(evidence, "placed-icons.png"), Assert.IsType<ImageContentBlock>(render.Content.First()).DecodedData.ToArray(), ct);
    }
}
