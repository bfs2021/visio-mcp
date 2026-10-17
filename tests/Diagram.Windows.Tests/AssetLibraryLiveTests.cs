using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>The picture library on the real stack: the host catalogue, the page's host.library.* calls, and an MCP client placing a library logo.</summary>
public class AssetLibraryLiveTests
{
    static CancellationToken Ct(int s = 60) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;
    static JsonElement Body(CallToolResult r) => r.StructuredContent!.Value;

    static byte[] Png(byte blue)
    {
        var px = new byte[16 * 16 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = blue; px[i + 1] = 0x40; px[i + 2] = 0x20; px[i + 3] = 0xFF; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, px, 16 * 4)));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    static async Task<McpClient> Connect(string pipe)
    {
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "lib", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: Ct());
    }

    static async Task<(string Doc, string Session, int Revision)> Summary(McpClient c)
    {
        var b = Body(await c.CallToolAsync("get_document_summary", cancellationToken: Ct()));
        return (b.GetProperty("documentId").GetString()!, b.GetProperty("sessionId").GetString()!, b.GetProperty("revision").GetInt32());
    }

    static async Task<PreparedUpload> Prepare(Harness h, byte[] bytes, string name)
    {
        var r = await h.Window.Preparer.PrepareAsync(new MemoryStream(bytes), "image/png", name, Ct());
        Assert.True(r.IsOk, r.Error?.Message);
        return r.Value;
    }

    static async Task<JsonElement> Assets(McpClient c)
    {
        var s = await Summary(c);
        var r = await c.CallToolAsync("list_assets", new Dictionary<string, object?> { ["documentId"] = s.Doc, ["sessionId"] = s.Session }, cancellationToken: Ct());
        Assert.True(r.IsError != true, r.StructuredContent?.ToString() ?? string.Join("", r.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        return Body(r).GetProperty("assets");
    }

    static Dictionary<string, object?> Script(string doc, string session, string tx, int rev, string script, params string[] library) => new()
    {
        ["documentId"] = doc, ["sessionId"] = session, ["transactionId"] = tx, ["baseRevision"] = rev, ["script"] = script, ["libraryAssets"] = library,
    };

    [Fact]
    public async Task AnAgentSeesLibraryAndDocumentPicturesAndPlacesALibraryLogo()
    {
        var pipe = $"lib-{Guid.NewGuid():N}";
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var v1 = await Prepare(h, Png(0x10), "logo.png");
        Assert.True((await h.Window.Library.ImportAsync(v1, ["brand"], Ct())).IsOk);

        // listing: the library entry, scope library, nothing pinned in the document yet
        var list = await Assets(client);
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal("asset:logo", entry.GetProperty("id").GetString());
        Assert.Equal("library", entry.GetProperty("scope").GetString());

        // placing it: the script imports it by id; the document pins the descriptor and the image in one transaction
        var s0 = await Summary(client);
        var tx = Guid.NewGuid().ToString();
        var script = "asset import prepared=\"asset:logo\"\nadd image id=mark asset=\"asset:logo\" x=10mm y=10mm w=20mm h=20mm";
        var placed = await client.CallToolAsync("execute_script", Script(s0.Doc, s0.Session, tx, s0.Revision, script, "asset:logo"), cancellationToken: Ct());
        Assert.True(placed.IsError != true, placed.StructuredContent?.ToString());
        Assert.Equal(1, Body(placed).GetProperty("transaction").GetProperty("revision").GetInt32());
        var snap = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        var pinned = Assert.Single(snap.Document.Assets);
        Assert.Equal(v1.Ref.Asset.Sha256, pinned.Sha256);

        // a retry with the same transaction id returns the same result even though the host issued a new preparation
        var retry = await client.CallToolAsync("execute_script", Script(s0.Doc, s0.Session, tx, s0.Revision, script, "asset:logo"), cancellationToken: Ct());
        Assert.True(retry.IsError != true, retry.StructuredContent?.ToString());
        Assert.Equal(Body(placed).GetProperty("transaction").GetRawText(), Body(retry).GetProperty("transaction").GetRawText());
        Assert.Equal(1, (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!.Revision);

        // the document entry now wins the id and reports use
        var after = (await Assets(client)).EnumerateArray().Single();
        Assert.Equal("document", after.GetProperty("scope").GetString());
        Assert.True(after.GetProperty("inLibrary").GetBoolean());
        Assert.False(after.TryGetProperty("newerInLibrary", out _));

        // the library source changes: the document keeps its pin and is told a newer version exists
        var v2 = await Prepare(h, Png(0xE0), "logo.png");
        Assert.True((await h.Window.Library.ReplaceSourceAsync("asset:logo", v2, Ct())).IsOk);
        var newer = (await Assets(client)).EnumerateArray().Single();
        Assert.True(newer.GetProperty("newerInLibrary").GetBoolean());
        Assert.Equal(v1.Ref.Asset.Sha256, newer.GetProperty("sha256").GetString());

        // importing the newer version registers a separate full-hash version; the first picture is untouched
        var s1 = await Summary(client);
        var again = await client.CallToolAsync("execute_script", Script(s1.Doc, s1.Session, Guid.NewGuid().ToString(), s1.Revision,
            "asset import prepared=\"asset:logo\"\nadd image id=second asset=\"asset:logo\" x=40mm y=10mm w=20mm h=20mm", "asset:logo"), cancellationToken: Ct());
        Assert.True(again.IsError != true, again.StructuredContent?.ToString());
        var snap2 = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        Assert.Equal(["asset:logo", $"asset:logo~{v2.Ref.Asset.Sha256}"], snap2.Document.Assets.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(v1.Ref.Asset.Sha256, snap2.Document.Assets.First(a => a.Id == "asset:logo").Sha256);

        // asking for the old version by its hash is possible, and an unknown id or hash is a clear error
        var old = await client.CallToolAsync("execute_script", Script(s1.Doc, s1.Session, Guid.NewGuid().ToString(), snap2.Revision,
            "asset import prepared=\"asset:logo@" + v1.Ref.Asset.Sha256 + "\"", "asset:logo@" + v1.Ref.Asset.Sha256), cancellationToken: Ct());
        Assert.True(old.IsError != true, old.StructuredContent?.ToString());
        var missing = await client.CallToolAsync("execute_script", Script(s1.Doc, s1.Session, Guid.NewGuid().ToString(), snap2.Revision, "asset import prepared=\"asset:nope\"", "asset:nope"), cancellationToken: Ct());
        Assert.True(missing.IsError);
        Assert.Equal("not_found", Body(missing).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ThePagesLibraryCallsListAddRenameAndRemove()
    {
        await using var h = await Harness.StartAsync();
        var path = Path.Combine(h.Root, "brand.png");
        await File.WriteAllBytesAsync(path, Png(0x30));
        h.Window.Services.DialogOverride = kind => kind == "libraryImport" ? path : null;

        async Task<JsonElement> Call(string method, JsonObject? p = null)
        {
            var r = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = method, Params = p ?? new JsonObject() }, Ct());
            Assert.Null(r.Error);
            return r.Result!.Value;
        }

        var imported = await Call("host.library.import", new JsonObject { ["tags"] = new JsonArray("Brand") });
        Assert.True(imported.GetProperty("created").GetBoolean());
        Assert.Equal("asset:brand", imported.GetProperty("asset").GetProperty("id").GetString());
        var again = await Call("host.library.import");
        Assert.False(again.GetProperty("created").GetBoolean()); // the same file twice is one entry

        var renamed = await Call("host.library.setMetadata", new JsonObject { ["id"] = "asset:brand", ["name"] = "Brand mark", ["tags"] = new JsonArray("a", "b") });
        Assert.Equal("Brand mark", renamed.GetProperty("asset").GetProperty("name").GetString());
        var listed = await Call("host.library.list", new JsonObject { ["tags"] = new JsonArray("b") });
        Assert.Single(listed.GetProperty("assets").EnumerateArray());

        var use = await Call("host.library.use", new JsonObject { ["id"] = "asset:brand" });
        Assert.StartsWith("prep-", use.GetProperty("ref").GetProperty("preparationId").GetString());
        Assert.Equal(16, use.GetProperty("width").GetInt32());

        await Call("host.library.delete", new JsonObject { ["id"] = "asset:brand" });
        Assert.Empty((await Call("host.library.list")).GetProperty("assets").EnumerateArray());
        var gone = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "x", Method = "host.library.use", Params = new JsonObject { ["id"] = "asset:brand" } }, Ct());
        Assert.Equal("not_found", gone.Error!.Code);
    }
}
