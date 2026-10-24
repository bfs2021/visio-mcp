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

/// <summary>H02 on the real stack: the window's own maintenance pass asks the live editor what it holds and reclaims only what nobody needs.</summary>
public class BlobMaintenanceLiveTests
{
    static CancellationToken Ct(int s = 60) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;

    static byte[] Png(byte blue, int side = 64)
    {
        var px = new byte[side * side * 4];
        var rnd = new Random(blue);
        for (var i = 0; i < px.Length; i += 4) { px[i] = (byte)(blue ^ rnd.Next(32)); px[i + 1] = (byte)rnd.Next(256); px[i + 2] = 0x20; px[i + 3] = 0xFF; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, px, side * 4)));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    static async Task<McpClient> Connect(string pipe)
    {
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "h02", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: Ct());
    }

    [Fact]
    public async Task TheWindowsPassKeepsWhatTheEditorTheLibraryAndUploadsHoldAndReclaimsTheRest()
    {
        var pipe = $"h02-{Guid.NewGuid():N}";
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var w = h.Window;
        var root = Path.Combine(h.Root, "blobs");
        void Age(string sha) => File.SetLastWriteTimeUtc(Path.Combine(root, sha), DateTime.UtcNow - TimeSpan.FromDays(30));

        async Task<PreparedUpload> Prepare(byte[] bytes, string name)
        {
            var r = await w.Preparer.PrepareAsync(new MemoryStream(bytes), "image/png", name, Ct());
            Assert.True(r.IsOk, r.Error?.Message);
            return r.Value;
        }

        // a picture placed on the page, from the library
        var placed = await Prepare(Png(0x10), "placed.png");
        Assert.True((await w.Library.ImportAsync(placed, [], Ct())).IsOk);
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: Ct())).StructuredContent!.Value;
        var put = await client.CallToolAsync("execute_script", new Dictionary<string, object?>
        {
            ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString(),
            ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = summary.GetProperty("revision").GetInt32(),
            ["script"] = "asset import prepared=\"asset:placed\"\nadd image id=mark asset=\"asset:placed\" x=10mm y=10mm w=20mm h=20mm",
            ["libraryAssets"] = new[] { "asset:placed" },
        }, cancellationToken: Ct());
        Assert.True(put.IsError != true, put.StructuredContent?.ToString());
        Assert.True((await w.Library.DeleteAsync("asset:placed", Ct())).IsOk); // now only the document holds it

        var libraryOnly = await Prepare(Png(0x30), "library.png");
        Assert.True((await w.Library.ImportAsync(libraryOnly, [], Ct())).IsOk);
        var orphans = new[] { await w.Blobs.PutDurableAsync(Png(0x50, 96), Ct()), await w.Blobs.PutDurableAsync(Png(0x70, 128), Ct()) };
        foreach (var sha in new[] { placed.Ref.Asset.Sha256, libraryOnly.Ref.Asset.Sha256 }.Concat(orphans)) Age(sha);
        var recentOrphan = await w.Blobs.PutDurableAsync(Png(0x90), Ct()); // nobody holds it, but it was stored a moment ago
        var orphanBytes = orphans.Sum(o => new FileInfo(Path.Combine(root, o)).Length);
        var before = Directory.GetFiles(root).Length;

        var run = await w.Maintenance.RunOnceAsync(Ct());

        Assert.Equal(BlobMaintenanceRun.Collected, run.Outcome);
        Assert.Equal(2, run.Removed);
        Assert.Equal(orphanBytes, run.BytesFreed);
        Assert.All(orphans, o => Assert.False(w.Blobs.Contains(o)));
        foreach (var sha in new[] { placed.Ref.Asset.Sha256, libraryOnly.Ref.Asset.Sha256, recentOrphan }) Assert.True(w.Blobs.Contains(sha), sha);
        var page = (await h.Send("doc.render", new JsonObject { ["scope"] = "page", ["format"] = "png" })); // the placed picture still draws
        Assert.Null(page.Error);

        var again = await w.Maintenance.RunOnceAsync(Ct()); // nothing more to do: a second pass is harmless
        Assert.Equal(0, again.Removed);

        var evidence = Path.Combine(Harness.RepoRoot, "docs", "acceptance", "evidence", "H02");
        Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "maintenance-live.json"), new JsonObject
        {
            ["recordedUtc"] = DateTime.UtcNow.ToString("O"), ["test"] = nameof(TheWindowsPassKeepsWhatTheEditorTheLibraryAndUploadsHoldAndReclaimsTheRest),
            ["blobFilesBefore"] = before, ["blobFilesAfter"] = Directory.GetFiles(root).Length,
            ["firstPass"] = JsonSerializer.SerializeToNode(run), ["secondPass"] = JsonSerializer.SerializeToNode(again),
            ["kept"] = new JsonObject { ["placedOnPageOnly"] = placed.Ref.Asset.Sha256, ["inLibraryOnly"] = libraryOnly.Ref.Asset.Sha256, ["recentUnreferenced"] = recentOrphan },
            ["reclaimed"] = new JsonArray(orphans.Select(o => (JsonNode)o).ToArray()), ["reclaimedBytes"] = orphanBytes,
            ["grace"] = BlobMaintenanceOptions.Default.Grace.ToString(),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
