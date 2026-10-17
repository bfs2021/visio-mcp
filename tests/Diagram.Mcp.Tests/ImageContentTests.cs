using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Ipc;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

public class ImageContentTests
{
    const string Doc = "00000000-0000-4000-8000-000000000001", Session = "00000000-0000-4000-8000-000000000002", Page = "00000000-0000-4000-8000-000000000004";
    // a real 1x1 PNG
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    static string ShimDll()
    {
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll"));
    }

    [Fact]
    public async Task RenderResultIsAnImageBlockPlusJsonMetadataNeverHiddenBase64()
    {
        var pipe = $"img-test-{Guid.NewGuid():N}";
        RequestEnvelope? seen = null;
        await using var server = PipeServer.Start(pipe, (c, r, ct) =>
        {
            seen = r;
            return Task.FromResult(Wire.Ok(r.RequestId, new JsonObject
            {
                ["mimeType"] = "image/png", ["base64"] = Convert.ToBase64String(Png), ["byteLength"] = Png.Length, ["widthPx"] = 1, ["heightPx"] = 1,
                ["cropPt"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 10, ["height"] = 10 }, ["revision"] = 7, ["mode"] = "debug", ["format"] = "png", ["pageId"] = Page,
            }));
        });
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "img", Command = "dotnet", Arguments = [ShimDll()], EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

        var result = await client.CallToolAsync("render_page", new Dictionary<string, object?> { ["documentId"] = Doc, ["sessionId"] = Session, ["pageId"] = Page, ["mode"] = "debug" });
        Assert.True(result.IsError != true);
        var image = Assert.IsType<ImageContentBlock>(result.Content.First());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Png, image.DecodedData.ToArray()); // the picture itself, as an MCP image
        var text = Assert.IsType<TextContentBlock>(result.Content.Last());
        Assert.DoesNotContain(Convert.ToBase64String(Png), text.Text); // the text is metadata only
        Assert.DoesNotContain("base64", text.Text);
        var meta = JsonNode.Parse(text.Text)!;
        Assert.Equal(7, meta["revision"]!.GetValue<int>()); // which revision the picture shows
        Assert.Equal("debug", meta["mode"]!.GetValue<string>());
        Assert.Equal(1, meta["widthPx"]!.GetValue<int>());
        Assert.False(result.StructuredContent!.Value.TryGetProperty("base64", out _));

        Assert.Equal("doc.render", seen!.Method);
        Assert.Equal("page", seen.Params["scope"]!.GetValue<string>());
        Assert.Equal("debug", seen.Params["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task RenderRegionForwardsBoundsOrElementIds()
    {
        var pipe = $"img-test-{Guid.NewGuid():N}";
        RequestEnvelope? seen = null;
        await using var server = PipeServer.Start(pipe, (c, r, ct) => { seen = r; return Task.FromResult(Wire.Error(r.RequestId, "invalid_request", "stop here")); });
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "img", Command = "dotnet", Arguments = [ShimDll()], EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        var r = await client.CallToolAsync("render_region", new Dictionary<string, object?> { ["documentId"] = Doc, ["sessionId"] = Session, ["pageId"] = Page, ["elementIds"] = new[] { "api", "db" }, ["paddingPt"] = 20.0 });
        Assert.True(r.IsError); // the application's refusal comes back as a tool error with its own code
        Assert.Equal("region", seen!.Params["scope"]!.GetValue<string>());
        Assert.Equal(new[] { "api", "db" }, seen.Params["elementIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Null(seen.Params["bounds"]);
        Assert.Equal(20.0, seen.Params["paddingPt"]!.GetValue<double>());
    }
}
