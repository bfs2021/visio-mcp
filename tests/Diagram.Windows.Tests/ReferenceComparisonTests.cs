using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Diagram.Core.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

public class ReferenceComparisonTests
{
    static BitmapSource Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }
    static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    [Fact]
    public async Task ThinCanvasRegionsKeepTheRenderLetterboxingAndCoordinateTransform()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = cts.Token;
        await using var h = await Harness.StartAsync();
        var s = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        var pageId = s.Document.Pages[0].Id;
        var applied = await h.Send("doc.apply", JsonSerializer.SerializeToNode(new
        {
            baseRevision = 0, transactionId = Guid.NewGuid().ToString(), atomic = true,
            operations = new[] { new { op = "createShape", pageId, bounds = new { x = 0, y = 0, width = 1000, height = 1 },
                style = new { fill = new { kind = "solid", colour = "#000000", opacity = 1 }, stroke = new { kind = "none" }, strokeWidthPt = 0 } } },
        })!.AsObject());
        Assert.Null(applied.Error);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Harness.RepoRoot, "tests", "fixtures", "references", "branch.png"), ct);
        var prepared = await h.Window.Preparer.PrepareAsync(new MemoryStream(bytes), "image/png", "branch.png", ct);
        var asset = await h.Window.Library.ImportAsync(prepared.Value, [], ct);
        var bounds = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1000, ["height"] = 1 };
        var raw = await h.Send("doc.render", new JsonObject { ["scope"] = "region", ["pageId"] = pageId, ["bounds"] = bounds.DeepClone(), ["paddingPt"] = 0, ["maxWidth"] = 257, ["maxHeight"] = 257 });
        Assert.Null(raw.Error);
        var result = await h.Window.Comparisons.HandleAsync(new RequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString(), Method = "reference.compare", DocumentId = s.DocumentId, SessionId = s.SessionId,
            Params = new JsonObject { ["referenceAssetId"] = asset.Value.Asset.Id, ["referenceSha256"] = asset.Value.Asset.Sha256,
                ["cropPx"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 32, ["height"] = 34 }, ["pageId"] = pageId, ["bounds"] = bounds, ["panelSizePx"] = 257, ["paddingPt"] = 0 },
        }, ct);
        Assert.Null(result.Error);
        var body = result.Result!.Value; var canvas = body.GetProperty("canvas");
        Assert.Equal(1, canvas.GetProperty("rasterDisplayRectPx").GetProperty("height").GetDouble());
        Assert.Equal(0.257, canvas.GetProperty("pointsToOutput").GetProperty("scale").GetDouble(), 8);
        var rawImage = Decode(Convert.FromBase64String(raw.Result!.Value.GetProperty("base64").GetString()!));
        var pairedImage = Decode(Convert.FromBase64String(body.GetProperty("base64").GetString()!));
        Assert.Equal(Pixel(rawImage, 100, 0), Pixel(pairedImage, 289 + 100, 168));
    }
    [Fact]
    public async Task ApprovedReferenceAndCanvasDetailsCanBeComparedWithoutEditingTheDocument()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = cts.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts", "r07-comparison");
        Directory.CreateDirectory(evidence);
        var pipe = "comparison-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "comparison", Command = "dotnet",
            Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        async Task<CallToolResult> Call(string tool, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(tool, args, cancellationToken: ct);
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            return r;
        }
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString() };
        var operations = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Harness.RepoRoot, "tests", "fixtures", "diagrams", "application-release-flow-light.operations.json"), ct));
        using (operations)
        {
            var apply = Identity(); apply["baseRevision"] = 0; apply["transactionId"] = Guid.NewGuid().ToString(); apply["operations"] = operations.RootElement;
            await Call("apply_operations", apply);
        }
        var before = (await h.Send("doc.snapshot")).Result!.Value.GetRawText();
        var page = "6c1143cf-f076-4d34-840c-000000000001";
        foreach (var name in new[] { "branch", "container" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Harness.RepoRoot, "tests", "fixtures", "references", name + ".png"), ct);
            var prepared = await h.Window.Preparer.PrepareAsync(new MemoryStream(bytes), "image/png", name + ".png", ct);
            Assert.True(prepared.IsOk);
            var imported = await h.Window.Library.ImportAsync(prepared.Value, ["reference"], ct);
            Assert.True(imported.IsOk);
            Dictionary<string, object?> Args()
            {
                var a = Identity(); a["referenceAssetId"] = imported.Value.Asset.Id; a["referenceSha256"] = imported.Value.Asset.Sha256;
                a["cropPx"] = new { x = 0, y = 0, width = 32, height = 34 }; return a;
            }
            var crop = await Call("render_reference_region", Args());
            var cropMeta = crop.StructuredContent!.Value;
            Assert.Equal(256, cropMeta.GetProperty("widthPx").GetInt32());
            Assert.Equal(272, cropMeta.GetProperty("heightPx").GetInt32());
            Assert.Equal(32, cropMeta.GetProperty("reference").GetProperty("sourceWidthPx").GetInt32());
            var offsetArgs = Args(); offsetArgs["cropPx"] = new { x = 4, y = 2, width = 16, height = 16 }; offsetArgs["scale"] = 4;
            var offset = await Call("render_reference_region", offsetArgs);
            var offsetPixels = Decode(Assert.IsType<ImageContentBlock>(offset.Content.First()).DecodedData.ToArray());
            Assert.Equal(64, offsetPixels.PixelWidth); Assert.Equal(64, offsetPixels.PixelHeight);
            var original = Decode(bytes);
            for (var y = 0; y < 16; y++) for (var x = 0; x < 16; x++)
                Assert.Equal(Pixel(original, 4 + x, 2 + y), Pixel(offsetPixels, 4 * x + 2, 4 * y + 2));
            var mapping = offset.StructuredContent!.Value.GetProperty("reference").GetProperty("sourceToOutput");
            Assert.Equal(4, mapping.GetProperty("scale").GetDouble());
            Assert.Equal(-16, mapping.GetProperty("translateX").GetDouble());
            Assert.Equal(-8, mapping.GetProperty("translateY").GetDouble());
            var args = Args(); args["pageId"] = page; args["elementIds"] = new[] { name == "branch" ? "application-git-branch" : "dev-container-icon" }; args["panelSizePx"] = 256;
            var pair = await Call("compare_reference", args);
            var meta = pair.StructuredContent!.Value;
            Assert.Equal(1, meta.GetProperty("revision").GetInt32());
            Assert.Equal(imported.Value.Asset.Sha256, meta.GetProperty("reference").GetProperty("sha256").GetString());
            Assert.True(meta.GetProperty("reference").GetProperty("sourceToOutput").GetProperty("scale").GetDouble() > 0);
            Assert.True(meta.GetProperty("canvas").GetProperty("pointsToOutput").GetProperty("scale").GetDouble() > 0);
            var png = Assert.IsType<ImageContentBlock>(pair.Content.First()).DecodedData.ToArray();
            using var ms = new MemoryStream(png);
            var decoded = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(meta.GetProperty("widthPx").GetInt32(), decoded.PixelWidth);
            await File.WriteAllBytesAsync(Path.Combine(evidence, name + "-comparison.png"), png, ct);
            await File.WriteAllTextAsync(Path.Combine(evidence, name + "-metadata.json"), meta.GetRawText(), ct);
            args["mode"] = "overlay";
            var overlay = await Call("compare_reference", args);
            Assert.Equal("overlay", overlay.StructuredContent!.Value.GetProperty("mode").GetString());
            await File.WriteAllBytesAsync(Path.Combine(evidence, name + "-overlay.png"), Assert.IsType<ImageContentBlock>(overlay.Content.First()).DecodedData.ToArray(), ct);

            async Task Reject(Dictionary<string, object?> bad, string code)
            {
                var r = await client.CallToolAsync("render_reference_region", bad, cancellationToken: ct);
                Assert.True(r.IsError);
                Assert.Equal(code, r.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
            }
            var bad = Args(); bad["cropPx"] = new { x = 31, y = 0, width = 2, height = 34 }; await Reject(bad, "invalid_request");
            bad = Args(); bad["cropPx"] = new { x = -1, y = 0, width = 2, height = 34 }; await Reject(bad, "invalid_request");
            bad = Args(); bad["scale"] = 1000; await Reject(bad, "invalid_request");
            bad = Args(); bad["referenceSha256"] = new string('0', 64); await Reject(bad, "not_found");
            bad = Args(); bad["sessionId"] = Guid.NewGuid().ToString(); await Reject(bad, "session_mismatch");
            bad = Args(); bad["referenceAssetId"] = "C:\\secret.png"; await Reject(bad, "not_found");
            bad = Args(); bad["cropPx"] = new { x = 0.5, y = 0, width = 2, height = 2 }; await Reject(bad, "invalid_request");
            bad = Args(); bad["cropPx"] = new { x = 0, y = 0, width = 0, height = 2 }; await Reject(bad, "invalid_request");
            bad = Args(); bad["cropPx"] = null; await Reject(bad, "invalid_request");
            bad = Args(); bad["cropPx"] = new { x = int.MaxValue, y = 0, width = int.MaxValue, height = 2 }; await Reject(bad, "invalid_request");
            bad = Args(); bad["referenceSha256"] = null; await Reject(bad, "invalid_request");
            bad = Args(); bad["referenceScope"] = "file"; await Reject(bad, "invalid_request");
            bad = Args(); bad["cropPx"] = new { y = 0, width = 2, height = 2 }; await Reject(bad, "invalid_request");
            var badComparison = Args(); badComparison["pageId"] = page; badComparison["elementIds"] = new[] { "application-git-branch" };
            badComparison["bounds"] = new { x = 0, y = 0, width = 10, height = 10 };
            var ambiguous = await client.CallToolAsync("compare_reference", badComparison, cancellationToken: ct);
            Assert.True(ambiguous.IsError); Assert.Equal("invalid_request", ambiguous.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
            // Catalogue metadata now describes a different-sized source. The pinned old hash still uses its own pixels and dimensions.
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(original, new System.Windows.Int32Rect(0, 0, 16, 16))));
            using var replacementBytes = new MemoryStream(); encoder.Save(replacementBytes); replacementBytes.Position = 0;
            var replacement = await h.Window.Preparer.PrepareAsync(replacementBytes, "image/png", name + ".png", ct);
            Assert.True(replacement.IsOk);
            Assert.True((await h.Window.Library.ReplaceSourceAsync(imported.Value.Asset.Id, replacement.Value, ct)).IsOk);
            var pinned = await Call("render_reference_region", Args());
            Assert.Equal(32, pinned.StructuredContent!.Value.GetProperty("reference").GetProperty("sourceWidthPx").GetInt32());
            Assert.Equal(imported.Value.Asset.Sha256, pinned.StructuredContent!.Value.GetProperty("reference").GetProperty("sha256").GetString());
        }
        Assert.Equal(before, (await h.Send("doc.snapshot")).Result!.Value.GetRawText());
        // A document pin is independently authorised; comparison does not require the library entry to remain.
        var libraryEntry = await h.Window.Library.GetAsync("asset:branch", ct);
        var import = Identity(); import["baseRevision"] = 1; import["transactionId"] = Guid.NewGuid().ToString();
        import["script"] = "asset import prepared=\"asset:branch\""; import["libraryAssets"] = new[] { "asset:branch" };
        await Call("execute_script", import);
        Assert.True((await h.Window.Library.DeleteAsync("asset:branch", ct)).IsOk);
        var pinnedBefore = (await h.Send("doc.snapshot")).Result!.Value.GetRawText();
        var documentCrop = Identity(); documentCrop["referenceAssetId"] = "asset:branch"; documentCrop["referenceSha256"] = libraryEntry!.Sha256;
        documentCrop["referenceScope"] = "document"; documentCrop["cropPx"] = new { x = 0, y = 0, width = 16, height = 16 };
        await Call("render_reference_region", documentCrop);
        Assert.Equal(pinnedBefore, (await h.Send("doc.snapshot")).Result!.Value.GetRawText());
        var oversizedEncoder = new PngBitmapEncoder();
        oversizedEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, new byte[512 * 512 * 4], 512 * 4)));
        using var bigStream = new MemoryStream(); oversizedEncoder.Save(bigStream); bigStream.Position = 0;
        var bigPrepared = await h.Window.Preparer.PrepareAsync(bigStream, "image/png", "large.png", ct);
        var bigAsset = await h.Window.Library.ImportAsync(bigPrepared.Value, [], ct);
        var oversized = Identity(); oversized["referenceAssetId"] = bigAsset.Value.Asset.Id; oversized["referenceSha256"] = bigAsset.Value.Asset.Sha256;
        oversized["cropPx"] = new { x = 0, y = 0, width = 512, height = 512 }; oversized["scale"] = 16;
        var limited = await client.CallToolAsync("render_reference_region", oversized, cancellationToken: ct);
        Assert.True(limited.IsError); Assert.Equal("limit_exceeded", limited.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }
}
