using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class LifecycleTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly BlobStore Blobs;
        public readonly AtomicFileWriter Writer = new();
        public readonly SessionCoordinator Coordinator;
        public readonly FakeEditor Editor;

        public Env(FakeEditor editor, BlobStore? blobs = null, long? maxJsonBytes = null)
        {
            Editor = editor;
            Blobs = blobs ?? new BlobStore(Dir.File("blobs"));
            Coordinator = new SessionCoordinator(editor, Blobs, new AssetPreparer(Blobs), Writer, maxJsonBytes ?? InputLimits.NativeJsonBytes);
        }
        public void Dispose() => Dir.Dispose();
    }

    static readonly byte[] Png = Bytes.PngHeader(16, 16);

    static async Task<Env> WithImage(int revision = 0)
    {
        var editor = new FakeEditor(Docs.Make([("asset:logo", "image/png", Png)], revision: revision));
        var env = new Env(editor);
        await env.Blobs.PutDurableAsync(Png, default);
        return env;
    }

    static ExpectedState Expect(FakeEditor e) => new() { DocumentId = e.Document.Id, SessionId = e.SessionId, BaseRevision = e.Revision };


    // ---- save ----------------------------------------------------------------------------------

    [Fact]
    public async Task ASaveAtTheSizeLimitSucceedsAndOneByteOverSaysWhatIsTooBigAndWritesNothing()
    {
        using var env = await WithImage();
        var probe = env.Dir.File("probe.diagram.json");
        Assert.True((await env.Coordinator.SaveJsonAsync(probe, env.Editor.Scope, default)).IsOk);
        var size = new FileInfo(probe).Length;

        using var atLimit = new Env(env.Editor, env.Blobs, maxJsonBytes: size);
        var ok = await atLimit.Coordinator.SaveJsonAsync(atLimit.Dir.File("at.diagram.json"), env.Editor.Scope, default);
        Assert.True(ok.IsOk, ok.Error?.Message);

        using var over = new Env(env.Editor, env.Blobs, maxJsonBytes: size - 1);
        var path = over.Dir.File("over.diagram.json");
        var refused = await over.Coordinator.SaveJsonAsync(path, env.Editor.Scope, default);
        Assert.False(refused.IsOk);
        Assert.Equal("limit_exceeded", refused.Error!.Code);
        Assert.False(File.Exists(path));
        var d = refused.Error.Details!;
        Assert.Equal(size, d["sizeBytes"]!.GetValue<long>());
        Assert.Equal(size - 1, d["limitBytes"]!.GetValue<long>());
        Assert.Equal(1, d["overByBytes"]!.GetValue<long>());
        Assert.Equal(1, d["pictureCount"]!.GetValue<int>());
        Assert.Equal(Png.Length, d["largestPictures"]![0]!["bytes"]!.GetValue<long>());
        Assert.Contains("asset:logo", refused.Error.Message); // names the picture to remove
        Assert.Contains("over the", refused.Error.Message);
    }

    [Fact]
    public async Task SaveRevisionRDoesNotCleanRPlus1()
    {
        using var env = await WithImage(revision: 1);
        env.Editor.AfterSnapshot = env.Editor.Edit; // the user edits after the host's snapshot barrier
        var saved = await env.Coordinator.SaveJsonAsync(env.Dir.File("a.diagram.json"), env.Editor.Scope, default);
        Assert.True(saved.IsOk, saved.Error?.Message);
        Assert.Equal(1, saved.Value.Revision);
        Assert.Equal(1, env.Editor.SavedRevision);
        Assert.Equal(2, env.Editor.Revision);
        Assert.True(env.Editor.Dirty); // revision 2 was never written
    }

    [Fact]
    public async Task SaveCannotCleanAnotherSession()
    {
        using var env = await WithImage();
        env.Editor.AfterSnapshot = env.Editor.NewSession; // a different document session starts mid-save
        var saved = await env.Coordinator.SaveJsonAsync(env.Dir.File("a.diagram.json"), env.Editor.Scope, default);
        Assert.False(saved.IsOk);
        Assert.Equal("session_mismatch", saved.Error!.Code);
        Assert.Null(env.Editor.SavedRevision);
        Assert.True(env.Editor.Dirty);
    }

    [Fact]
    public async Task SaveWithAStaleScopeIsRejectedBeforeAnyWrite()
    {
        using var env = await WithImage();
        var stale = env.Editor.Scope;
        env.Editor.NewSession();
        var path = env.Dir.File("a.diagram.json");
        var saved = await env.Coordinator.SaveJsonAsync(path, stale, default);
        Assert.Equal("session_mismatch", saved.Error!.Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteFailurePreservesOriginal()
    {
        using var env = await WithImage();
        var path = env.Dir.File("keep.diagram.json");
        var original = Encoding.UTF8.GetBytes("""{"precious":"previous contents"}""");
        await File.WriteAllBytesAsync(path, original);
        env.Writer.BeforeReplace = () => throw new IOException("simulated failure after the temp file was written");

        var saved = await env.Coordinator.SaveJsonAsync(path, env.Editor.Scope, default);
        Assert.False(saved.IsOk);
        Assert.Equal("io_error", saved.Error!.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(env.Dir.Path, "*", SearchOption.TopDirectoryOnly)); // no stray temp file
        Assert.Null(env.Editor.SavedRevision);
    }

    [Fact]
    public async Task AtomicWriterReplacesExistingAndCreatesNew()
    {
        using var dir = new TempDir();
        var writer = new AtomicFileWriter();
        var path = dir.File("x.bin");
        await writer.WriteAsync(path, new byte[] { 1, 2, 3 }, default);
        await writer.WriteAsync(path, new byte[] { 4, 5 }, default);
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    // ---- open ----------------------------------------------------------------------------------

    [Fact]
    public async Task JsonLoadPreservesImagesAndIds()
    {
        using var source = await WithImage(revision: 4);
        var path = source.Dir.File("a.diagram.json");
        Assert.True((await source.Coordinator.SaveJsonAsync(path, source.Editor.Scope, default)).IsOk);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Single(json["blobs"]!.AsObject()); // asset bytes are stored once, by hash

        // A fresh editor and a fresh blob directory, as after restarting the app.
        var target = new FakeEditor(Docs.Make([], withImage: false));
        using var env = new Env(target);
        var oldSession = target.SessionId;
        var opened = await env.Coordinator.OpenJsonAsync(path, Expect(target), default);
        Assert.True(opened.IsOk, opened.Error?.Message);

        var el = target.Document.Pages[0].Elements.OfType<ImageElement>().Single();
        Assert.Equal(Docs.ImageId, el.Id);
        Assert.Equal("asset:logo", el.AssetId);
        Assert.Equal(Bytes.Sha(Png), target.Document.Assets.Single().Sha256);
        Assert.Equal(Png, (await env.Blobs.ReadAsync(Bytes.Sha(Png), default)).ToArray());
        Assert.NotEqual(oldSession, target.SessionId); // reload gets a fresh session
        Assert.Equal("native-json", target.Document.Source!.Format);
    }

    [Fact]
    public async Task LateOpenCannotReplaceNewSessionOrNewEdits()
    {
        using var env = await WithImage();
        var path = env.Dir.File("a.diagram.json");
        Assert.True((await env.Coordinator.SaveJsonAsync(path, env.Editor.Scope, default)).IsOk);

        var expected = Expect(env.Editor);
        var before = env.Editor.Document;

        env.Editor.BeforeReplace = _ => env.Editor.Edit(); // user edits while the file is being read
        var lateEdit = await env.Coordinator.OpenJsonAsync(path, expected, default);
        Assert.Equal("revision_conflict", lateEdit.Error!.Code);
        Assert.Same(before, env.Editor.Document);

        env.Editor.BeforeReplace = null;
        var expected2 = Expect(env.Editor);
        env.Editor.BeforeReplace = _ => env.Editor.NewSession(); // another open completed first
        var lateSession = await env.Coordinator.OpenJsonAsync(path, expected2, default);
        Assert.Equal("session_mismatch", lateSession.Error!.Code);
        Assert.Equal(0, env.Editor.ReplaceCalls);
    }

    // ---- hostile input -------------------------------------------------------------------------

    static async Task<(Env env, string path, JsonObject file)> SavedFile(params (string id, string mime, byte[] blob)[] assets)
    {
        var editor = new FakeEditor(Docs.Make(assets));
        var env = new Env(editor);
        foreach (var a in assets) await env.Blobs.PutDurableAsync(a.blob, default);
        var path = env.Dir.File("a.diagram.json");
        var saved = await env.Coordinator.SaveJsonAsync(path, editor.Scope, default);
        Assert.True(saved.IsOk, saved.Error?.Message);
        return (env, path, JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject());
    }

    static async Task<Result<OpenedFile>> OpenFresh(string path)
    {
        var target = new FakeEditor(Docs.Make([], withImage: false));
        using var env = new Env(target);
        var r = await env.Coordinator.OpenJsonAsync(path, Expect(target), default);
        if (!r.IsOk) Assert.Equal(0, target.ReplaceCalls); // a rejected file never reaches the editor
        return r;
    }

    [Fact]
    public async Task OversizeOrDeepJsonRejectsBeforePublish()
    {
        using var dir = new TempDir();
        var target = new FakeEditor(Docs.Make([], withImage: false));
        using var env = new Env(target);

        // Depth 129.
        var deep = dir.File("deep.json");
        await File.WriteAllTextAsync(deep, new string('[', 129) + new string(']', 129));
        var d = await env.Coordinator.OpenJsonAsync(deep, Expect(target), default);
        Assert.Equal("limit_exceeded", d.Error!.Code);

        // An ordinary string over 1 MiB.
        var longString = dir.File("long.json");
        await File.WriteAllTextAsync(longString, "{\"format\":\"native-json\",\"formatVersion\":1,\"document\":{\"title\":\"" + new string('x', 1024 * 1024 + 1) + "\"},\"blobs\":{}}");
        var s = await env.Coordinator.OpenJsonAsync(longString, Expect(target), default);
        Assert.Equal("limit_exceeded", s.Error!.Code);

        // A file over 64 MiB is refused from its length, without reading it.
        var huge = dir.File("huge.json");
        await using (var fs = File.Create(huge)) fs.SetLength(InputLimits.NativeJsonBytes + 1);
        var h = await env.Coordinator.OpenJsonAsync(huge, Expect(target), default);
        Assert.Equal("limit_exceeded", h.Error!.Code);

        Assert.Equal(0, target.ReplaceCalls);
    }

    [Fact]
    public void JsonBudgetAllowsLargeBlobStringsButNotLargeOrdinaryOnes()
    {
        var okBlob = Encoding.UTF8.GetBytes("{\"document\":{},\"blobs\":{\"a\":\"" + new string('A', 2 * 1024 * 1024) + "\"}}");
        Assert.True(JsonBudget.Check(okBlob).IsOk);
        var badOrdinary = Encoding.UTF8.GetBytes("{\"document\":{\"title\":\"" + new string('A', 2 * 1024 * 1024) + "\"},\"blobs\":{}}");
        Assert.Equal("limit_exceeded", JsonBudget.Check(badOrdinary).Error!.Code);
    }

    [Fact]
    public async Task JsonEmbeddedSvgIsSanitisedBeforePublish()
    {
        var evil = Bytes.Svg("""<script>alert(1)</script><rect width="5" height="5" onclick="alert(2)" fill="#123456"/>""");
        var (src, path, file) = await SavedFile(("asset:vec", "image/svg+xml", evil));
        using var _ = src;
        var target = new FakeEditor(Docs.Make([], withImage: false));
        using var env = new Env(target);
        var opened = await env.Coordinator.OpenJsonAsync(path, Expect(target), default);
        Assert.True(opened.IsOk, opened.Error?.Message);

        var asset = target.Document.Assets.Single();
        Assert.NotEqual(Bytes.Sha(evil), asset.Sha256); // the descriptor now names the sanitised bytes
        var stored = Encoding.UTF8.GetString((await env.Blobs.ReadAsync(asset.Sha256, default)).Span);
        Assert.DoesNotContain("script", stored);
        Assert.DoesNotContain("onclick", stored);
        Assert.Contains("#123456", stored);
        Assert.Equal(Bytes.Sha(Encoding.UTF8.GetBytes(stored)), asset.Sha256);
        Assert.Contains(opened.Value.Notes, n => n.Contains("script"));
        Assert.Equal(1, target.ReplaceCalls);
    }

    [Fact]
    public async Task JsonEmbeddedRasterDecodeBudget()
    {
        var huge = Bytes.PngHeader(20_000, 20_000);
        var (src, path, _) = await SavedFile(("asset:big", "image/png", huge));
        using var _s = src;
        var r = await OpenFresh(path);
        Assert.Equal("limit_exceeded", r.Error!.Code);
    }

    [Fact]
    public async Task ImportedBlobsDurableBeforePublish()
    {
        var (src, path, _) = await SavedFile(("asset:logo", "image/png", Png));
        using var _s = src;
        var target = new FakeEditor(Docs.Make([], withImage: false));
        using var env = new Env(target);
        var durableAtPublish = false;
        target.BeforeReplace = doc =>
            durableAtPublish = doc.Assets.All(a => File.Exists(Path.Combine(env.Dir.File("blobs"), a.Sha256)));
        var r = await env.Coordinator.OpenJsonAsync(path, Expect(target), default);
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.True(durableAtPublish);
    }

    [Fact]
    public async Task ReferenceAndHashConsistency()
    {
        var (src, path, file) = await SavedFile(("asset:logo", "image/png", Png));
        using var _s = src;

        // A blob whose key does not match its bytes.
        var tampered = file.DeepClone().AsObject();
        var blobs = tampered["blobs"]!.AsObject();
        var key = blobs.First().Key;
        blobs[key] = Convert.ToBase64String(Bytes.PngHeader(32, 32));
        var p1 = src.Dir.File("tampered.json");
        await File.WriteAllTextAsync(p1, tampered.ToJsonString());
        Assert.Equal("invalid_request", (await OpenFresh(p1)).Error!.Code);

        // An asset with no blob in the file.
        var missing = file.DeepClone().AsObject();
        missing["blobs"] = new JsonObject();
        var p2 = src.Dir.File("missing.json");
        await File.WriteAllTextAsync(p2, missing.ToJsonString());
        Assert.Equal("invalid_request", (await OpenFresh(p2)).Error!.Code);

        // An unknown member anywhere in the document.
        var extra = file.DeepClone().AsObject();
        extra["document"]!["surprise"] = 1;
        var p3 = src.Dir.File("extra.json");
        await File.WriteAllTextAsync(p3, extra.ToJsonString());
        Assert.Equal("invalid_request", (await OpenFresh(p3)).Error!.Code);

        // Wrong format marker.
        var wrong = file.DeepClone().AsObject();
        wrong["format"] = "something-else";
        var p4 = src.Dir.File("wrong.json");
        await File.WriteAllTextAsync(p4, wrong.ToJsonString());
        Assert.Equal("invalid_request", (await OpenFresh(p4)).Error!.Code);
    }

    [Fact]
    public async Task UnsafePathsAreRefusedForOpenAndSave()
    {
        using var env = await WithImage();
        foreach (var bad in new[] { @"\\server\share\a.json", @"\\.\pipe\x", @"C:\temp\CON.json", @"C:\temp\a.json:stream", "relative.json" })
        {
            var open = await env.Coordinator.OpenJsonAsync(bad, Expect(env.Editor), default);
            Assert.Equal("path_not_permitted", open.Error!.Code);
            var save = await env.Coordinator.SaveJsonAsync(bad, env.Editor.Scope, default);
            Assert.Equal("path_not_permitted", save.Error!.Code);
        }
    }
}
