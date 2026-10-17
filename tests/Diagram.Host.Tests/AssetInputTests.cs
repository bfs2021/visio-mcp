using System.Text;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class AssetInputTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly BlobStore Blobs;
        public readonly AssetPreparer Preparer;
        public Env() { Blobs = new BlobStore(Dir.File("blobs")); Preparer = new AssetPreparer(Blobs); }
        public int StoredBlobs => Directory.Exists(Dir.File("blobs")) ? Directory.GetFiles(Dir.File("blobs")).Length : 0;
        public Task<Result<PreparedUpload>> Prepare(byte[] bytes, string mime, string? name = null) => Preparer.PrepareAsync(new MemoryStream(bytes), mime, name, default);
        public void Dispose() => Dir.Dispose();
    }

    // ---- raster --------------------------------------------------------------------------------

    [Theory]
    [InlineData(9_000, 9_000)]   // 81 M pixels, each side fine
    [InlineData(20_000, 10)]     // side over 16,384
    [InlineData(16_385, 1)]
    public async Task RasterHeaderOver64MpxRejectsBeforeDecode(int w, int h)
    {
        using var env = new Env();
        // Pad the body so a decoder that tried to read pixels would have something to chew on.
        var png = Bytes.PngHeader(w, h).Concat(new byte[2_000_000]).ToArray();
        var tooLarge = await env.Prepare(png, "image/png");
        Assert.False(tooLarge.IsOk);
        Assert.Equal("limit_exceeded", tooLarge.Error!.Code);
        Assert.Equal(0, env.StoredBlobs); // nothing was stored or described
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/bmp")]
    public async Task ValidRastersAreStoredByHash(string mime)
    {
        using var env = new Env();
        var bytes = mime switch { "image/png" => Bytes.PngHeader(640, 480), "image/jpeg" => Bytes.Jpeg(640, 480), _ => Bytes.Bmp(640, 480) };
        var r = await env.Prepare(bytes, mime, "My Logo.png");
        Assert.True(r.IsOk, r.Error?.Message);
        var asset = r.Value.Ref.Asset;
        Assert.Equal(Bytes.Sha(bytes), asset.Sha256);
        Assert.Equal(mime, asset.MimeType);
        Assert.StartsWith("asset:my-logo~", asset.Id);
        Assert.Equal(bytes, (await env.Blobs.ReadAsync(asset.Sha256, default)).ToArray());
    }

    [Fact]
    public async Task RasterSizeUnsupportedMimeAndBadHeadersAreRejected()
    {
        using var env = new Env();
        var tooBig = new byte[InputLimits.RasterBytes + 1];
        Bytes.PngHeader(8, 8).CopyTo(tooBig, 0);
        Assert.Equal("limit_exceeded", (await env.Prepare(tooBig, "image/png")).Error!.Code);
        Assert.Equal("invalid_request", (await env.Prepare(Bytes.PngHeader(8, 8), "image/gif")).Error!.Code);
        Assert.Equal("invalid_request", (await env.Prepare("not an image"u8.ToArray(), "image/png")).Error!.Code);
        Assert.Equal("invalid_request", (await env.Prepare(Bytes.Jpeg(8, 8), "image/png")).Error!.Code); // content disagrees with the declared type
        Assert.Equal("invalid_request", (await env.Prepare(Bytes.PngHeader(0, 8), "image/png")).Error!.Code);
        Assert.Equal(0, env.StoredBlobs);
    }

    // ---- SVG -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("""<image href="http://evil.example/x.png" width="5" height="5"/>""")]
    [InlineData("""<image xlink:href="https://evil.example/x.png" width="5" height="5"/>""")]
    [InlineData("""<image href="logo.png" width="5" height="5"/>""")]
    [InlineData("""<use xlink:href="http://evil.example/a.svg#x"/>""")]
    [InlineData("""<rect width="5" height="5" style="fill:url(http://evil.example/a.svg#g)"/>""")]
    [InlineData("""<rect width="5" height="5" fill="url(//evil.example/a.svg#g)"/>""")]
    [InlineData("""<style>@import url(http://evil.example/x.css);</style><rect width="5" height="5"/>""")]
    [InlineData("""<style>rect { fill: url(http://evil.example/x) }</style><rect width="5" height="5"/>""")]
    [InlineData("""<rect width="5" height="5" style="background:url(javascript:alert(1))"/>""")]
    public async Task SvgExternalResourceRejects(string body)
    {
        using var env = new Env();
        var r = await env.Prepare(Bytes.Svg(body), "image/svg+xml");
        Assert.False(r.IsOk);
        Assert.Equal("invalid_request", r.Error!.Code);
        Assert.Equal("external_resource", r.Error.Details?["reason"]?.GetValue<string>());
        Assert.Equal(0, env.StoredBlobs);
    }

    [Fact]
    public async Task SanitiseThenHash()
    {
        using var env = new Env();
        var original = Bytes.Svg("""
            <script>alert(1)</script>
            <a href="https://example.com/"><rect width="5" height="5" fill="#abcdef" onmouseover="x()"/></a>
            <foreignObject><div xmlns="http://www.w3.org/1999/xhtml">hi</div></foreignObject>
            <animate attributeName="href" to="http://evil.example/"/>
            <circle cx="5" cy="5" r="2" style="fill:url(#g)"/>
            <linearGradient id="g"><stop offset="0" stop-color="#ff0000"/></linearGradient>
            """);
        var prepared = await env.Prepare(original, "image/svg+xml", "vec");
        Assert.True(prepared.IsOk, prepared.Error?.Message);
        var stored = (await env.Blobs.ReadAsync(prepared.Value.Ref.Asset.Sha256, default)).ToArray();
        var text = Encoding.UTF8.GetString(stored);
        foreach (var banned in new[] { "script", "onmouseover", "foreignObject", "animate", "example.com", "<a " })
            Assert.DoesNotContain(banned, text);
        Assert.Contains("#abcdef", text);
        Assert.Contains("linearGradient", text);
        Assert.Contains("url(#g)", text); // internal references survive

        var expectedSanitisedHash = Bytes.Sha(stored);
        Assert.Equal(expectedSanitisedHash, prepared.Value.Ref.Asset.Sha256); // the hash is of the sanitised bytes, not the upload
        Assert.NotEqual(Bytes.Sha(original), prepared.Value.Ref.Asset.Sha256);
        Assert.Contains(prepared.Value.Notes, n => n.Contains("script"));

        // Sanitising is idempotent: preparing the sanitised output again yields the same hash.
        var again = await env.Prepare(stored, "image/svg+xml", "vec");
        Assert.Equal(expectedSanitisedHash, again.Value.Ref.Asset.Sha256);
    }

    [Fact]
    public async Task SvgLimitsAndMalformedInputAreRejected()
    {
        using var env = new Env();
        var tooBig = Bytes.Svg("<!--" + new string('x', (int)InputLimits.SvgBytes) + "-->");
        Assert.Equal("limit_exceeded", (await env.Prepare(tooBig, "image/svg+xml")).Error!.Code);
        var manyElements = Bytes.Svg(string.Concat(Enumerable.Repeat("<g/>", InputLimits.SvgElements + 1)));
        Assert.Equal("limit_exceeded", (await env.Prepare(manyElements, "image/svg+xml")).Error!.Code);
        Assert.Equal("invalid_request", (await env.Prepare("<svg"u8.ToArray(), "image/svg+xml")).Error!.Code);
        Assert.Equal("invalid_request", (await env.Prepare("<html/>"u8.ToArray(), "image/svg+xml")).Error!.Code);
        // DTDs (entity expansion, external entities) are refused outright.
        var xxe = """<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY a "boom"><!ENTITY b SYSTEM "file:///c:/windows/win.ini">]><svg xmlns="http://www.w3.org/2000/svg"><text>&b;</text></svg>""";
        Assert.Equal("invalid_request", (await env.Prepare(Encoding.UTF8.GetBytes(xxe), "image/svg+xml")).Error!.Code);
        Assert.Equal(0, env.StoredBlobs);
    }

    [Fact]
    public async Task SvgDataImagesAreAllowedOnlyForPngAndJpeg()
    {
        using var env = new Env();
        var png = Convert.ToBase64String(Bytes.PngHeader(4, 4));
        var okSvg = await env.Prepare(Bytes.Svg($"""<image href="data:image/png;base64,{png}" width="4" height="4"/>"""), "image/svg+xml");
        Assert.True(okSvg.IsOk, okSvg.Error?.Message);
        var nested = await env.Prepare(Bytes.Svg("""<image href="data:image/svg+xml;base64,PHN2Zy8+" width="4" height="4"/>"""), "image/svg+xml");
        Assert.Equal("invalid_request", nested.Error!.Code);
    }

    // ---- provenance and capabilities -----------------------------------------------------------

    [Fact]
    public async Task SourcePathNeverAutomaticallyReread()
    {
        using var env = new Env();
        var embedded = Bytes.PngHeader(10, 10);
        var onDisk = Path.Combine(env.Dir.Path, "logo.png");
        await File.WriteAllBytesAsync(onDisk, Bytes.PngHeader(99, 99)); // the file at the descriptor's sourcePath has since changed

        // A saved document names that path as information only; opening it must use the embedded bytes.
        var editor = new FakeEditor(Docs.Make([("asset:logo", "image/png", embedded)]));
        var coordinator = new SessionCoordinator(editor, env.Blobs, env.Preparer, new AtomicFileWriter());
        await env.Blobs.PutDurableAsync(embedded, default);
        var file = Path.Combine(env.Dir.Path, "a.diagram.json");
        Assert.True((await coordinator.SaveJsonAsync(file, editor.Scope, default)).IsOk);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        json["document"]!["assets"]![0]!["sourcePath"] = onDisk;
        await File.WriteAllTextAsync(file, json.ToJsonString());

        var target = new FakeEditor(Docs.Make([], withImage: false));
        var open = new SessionCoordinator(target, env.Blobs, env.Preparer, new AtomicFileWriter());
        var opened = await open.OpenJsonAsync(file, new ExpectedState { DocumentId = target.Document.Id, SessionId = target.SessionId, BaseRevision = 0 }, default);
        Assert.True(opened.IsOk, opened.Error?.Message);
        var asset = target.Document.Assets.Single();
        Assert.Equal(onDisk, asset.SourcePath); // kept as information
        Assert.Equal(Bytes.Sha(embedded), asset.Sha256); // but the bytes are the embedded ones, not what is at that path now
    }

    [Fact]
    public async Task PreparationIdIsAnOpaqueExpiringCapability()
    {
        using var env = new Env();
        var now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        var preparer = new AssetPreparer(env.Blobs, () => now);
        var r = await preparer.PrepareAsync(new MemoryStream(Bytes.PngHeader(8, 8)), "image/png", @"C:\secret\folder\logo.png", default);
        var id = r.Value.Ref.PreparationId;
        Assert.DoesNotContain("secret", id);
        Assert.DoesNotContain(@"\", id);
        Assert.DoesNotContain("secret", r.Value.Ref.Asset.Name, StringComparison.OrdinalIgnoreCase); // only the file name, never the folder
        Assert.Equal(r.Value.Ref.Asset.Sha256, preparer.Resolve(id)); // host-issued: resolves to the stored blob
        Assert.Null(preparer.Resolve("prep-forged"));
        now = now.AddHours(1);
        Assert.Null(preparer.Resolve(id)); // expired
    }

    [Theory]
    [InlineData(@"\\server\share\x.json", false)]
    [InlineData(@"\\?\C:\x.json", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData("//server/share/x.json", false)]
    [InlineData(@"C:\work\x.json:hidden", false)]
    [InlineData(@"C:\work\NUL", false)]
    [InlineData(@"C:\work\com1.txt", false)]
    [InlineData(@"C:\work\aux.diagram.json", false)]
    [InlineData("work\\x.json", false)]
    [InlineData("", false)]
    [InlineData(@"C:\work\x.diagram.json", true)]
    [InlineData(@"C:\work\..\work\x.json", true)]
    public void PathPolicyRefusesDeviceUncStreamAndReservedNames(string path, bool allowed)
    {
        var r = PathPolicy.Validate(path);
        Assert.Equal(allowed, r.IsOk);
        if (!allowed) Assert.Equal("path_not_permitted", r.Error!.Code);
    }
}
