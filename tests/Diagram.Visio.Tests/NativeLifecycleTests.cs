using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Visio;
using Xunit;

sealed class FakeEditor : IEditorChannel, IExportSource
{
    public DiagramDocument Document;
    public string SessionId = Guid.NewGuid().ToString();
    public int Revision;
    public int ReplaceCalls, MarkSavedCalls;
    public Action? AfterSnapshot;
    public Action<DiagramDocument>? BeforeReplace;
    public Func<ExportSnapshot, ExportSnapshot>? Tamper;

    public FakeEditor(DiagramDocument document) { Document = document; Revision = document.Revision; }
    public Scope Scope => new() { DocumentId = Document.Id, SessionId = SessionId };
    public void Edit() => Revision++;
    public void NewSession() => SessionId = Guid.NewGuid().ToString();
    public ExpectedState Expect => new() { DocumentId = Document.Id, SessionId = SessionId, BaseRevision = Revision };

    Result<Unit> Check(Scope s) => s.SessionId != SessionId ? Result<Unit>.Fail("session_mismatch", "stale session") : s.DocumentId != Document.Id ? Result<Unit>.Fail("document_mismatch", "other document") : Result<Unit>.Ok(default);

    public Task<Result<Snapshot>> SnapshotAsync(Scope scope, CancellationToken ct) => throw new NotSupportedException();

    public Task<Result<ExportSnapshot>> ExportSnapshotAsync(Scope scope, CancellationToken ct)
    {
        var c = Check(scope);
        if (!c.IsOk) return Task.FromResult(Result<ExportSnapshot>.Fail(c.Error!));
        var snap = Doc.Snapshot(Document with { Revision = Revision });
        snap = snap with { SessionId = SessionId, Projection = snap.Projection with { SessionId = SessionId } };
        if (Tamper is not null) snap = Tamper(snap);
        AfterSnapshot?.Invoke(); // the user keeps editing after the barrier
        return Task.FromResult(Result<ExportSnapshot>.Ok(snap));
    }

    public Task<Result<Snapshot>> ReplaceAsync(DiagramDocument document, ExpectedState expected, CancellationToken ct)
    {
        BeforeReplace?.Invoke(document);
        var c = Check(new Scope { DocumentId = expected.DocumentId, SessionId = expected.SessionId });
        if (!c.IsOk) return Task.FromResult(Result<Snapshot>.Fail(c.Error!));
        if (expected.BaseRevision != Revision) return Task.FromResult(Result<Snapshot>.Fail("revision_conflict", "edited meanwhile"));
        ReplaceCalls++;
        Document = document; Revision = document.Revision; SessionId = Guid.NewGuid().ToString();
        return Task.FromResult(Result<Snapshot>.Ok(new Snapshot { DocumentId = document.Id, SessionId = SessionId, Revision = Revision, Document = document }));
    }

    public Task<Result<Unit>> MarkSavedAsync(Scope scope, int revision, string path, CancellationToken ct) { MarkSavedCalls++; return Task.FromResult(Result<Unit>.Ok(default)); }
}

public class NativeLifecycleTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "diagram-native-" + Guid.NewGuid().ToString("N"));
    readonly BlobStore store;
    readonly AtomicFileWriter writer = new();
    readonly NativeVsdxImporter importer = new();
    readonly NativeVsdxExporter exporter = new();

    public NativeLifecycleTests() { Directory.CreateDirectory(dir); store = new BlobStore(Path.Combine(dir, "blobs")); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }
    string P(string n) => Path.Combine(dir, n);

    NativeFileCoordinator Coordinator(FakeEditor e, IRasteriser? r = null, BlobStore? s = null, ImportOptions? limits = null) => new(e, e, s ?? store, writer, importer, exporter, r, limits);

    static DiagramDocument Plain() => Doc.Make([Doc.Page([Doc.Shape(1, 50, 50, 80, 40, new { alias = "a" }), Doc.Shape(2, 200, 50, 80, 40, new { alias = "b" }), Doc.Connector(10, Doc.Glue(1), Doc.Glue(2))])]);

    static byte[] SamplePackage() => RT.Export(Doc.Snapshot(Plain())).Package;

    static JsonNode ImageNode(int n, string assetId) => System.Text.Json.JsonSerializer.SerializeToNode(new { id = Doc.Id(n), kind = "image", layerIds = Array.Empty<string>(), zIndex = n, locked = false, hidden = false, bounds = new { x = 100, y = 100, width = 80, height = 80 }, rotationDeg = 0, metadata = new { }, assetId, fit = "contain", opacity = 1, preserveAspectRatio = true })!;

    sealed class FakeRaster : IRasteriser { public Task<Result<byte[]>> RasteriseAsync(string sha, CancellationToken ct) => Task.FromResult(Result<byte[]>.Ok(Fx.Png(10, 10))); }

    // ---- import without opening (import_visio) ------------------------------------------------------

    [Fact]
    public async Task ImportingAFileReturnsTheDocumentAndTouchesNoEditor()
    {
        var editor = new FakeEditor(Plain());
        var path = P("ref.vsdx");
        File.WriteAllBytes(path, SamplePackage());
        var r = await Coordinator(editor).ImportFileAsync(path, default);
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.Equal(2, r.Value.Document.Pages.Sum(pg => pg.Elements.Count(e => System.Text.Json.JsonSerializer.SerializeToNode(e, ContractJson.Options)!["kind"]!.GetValue<string>() == "shape")));
        Assert.Equal(0, editor.ReplaceCalls);
        Assert.Equal(Plain().Id, editor.Document.Id);
    }

    [Fact]
    public async Task AnImportedPictureIsDurableBeforeTheResultIsReturned()
    {
        var png = Fx.Png(12, 12);
        var sha = await store.PutDurableAsync(png, default);
        var doc = Doc.Make([Doc.Page([ImageNode(1, "asset:logo")])], [new { id = "asset:logo", name = "logo.png", mimeType = "image/png", sha256 = sha, tags = Array.Empty<string>() }]);
        var path = P("pic.vsdx");
        File.WriteAllBytes(path, RT.Export(Doc.Snapshot(doc), new Blobs { Bytes = { [sha] = png } }).Package);
        var fresh = new BlobStore(Path.Combine(dir, "blobs-fresh"));
        var r = await Coordinator(new FakeEditor(Plain()), s: fresh).ImportFileAsync(path, default);
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.All(r.Value.Document.Assets, a => Assert.True(fresh.Contains(a.Sha256)));
    }

    [Fact]
    public async Task ARevalidationFailureStopsTheReadAndTheOtherRefusalsAreStable()
    {
        var c = Coordinator(new FakeEditor(Plain()));
        var path = P("ref.vsdx");
        File.WriteAllBytes(path, SamplePackage());
        var stale = await c.ImportFileAsync(path, default, () => Result<Unit>.Fail("path_not_permitted", "no longer approved"));
        Assert.Equal("path_not_permitted", stale.Error!.Code);
        Assert.Equal("not_found", (await c.ImportFileAsync(P("missing.vsdx"), default)).Error!.Code);
        File.WriteAllText(P("bad.vsdx"), "this is not a zip");
        Assert.False((await c.ImportFileAsync(P("bad.vsdx"), default)).IsOk); // hostile or damaged input is an error, never a crash
        File.WriteAllBytes(P("a.png"), [1, 2, 3]);
        Assert.Equal("invalid_request", (await c.ImportFileAsync(P("a.png"), default)).Error!.Code);
    }

    // ---- export ----------------------------------------------------------------------------------

    [Fact]
    public async Task APackageAtTheSizeLimitIsWrittenAndOneByteOverIsRefusedWithTheReasonAndNothingIsWritten()
    {
        var png = Fx.Png(40, 40);
        var sha = await store.PutDurableAsync(png, default);
        var doc = Doc.Make([Doc.Page([ImageNode(1, "asset:logo")])], [new { id = "asset:logo", name = "logo.png", mimeType = "image/png", sha256 = sha, tags = Array.Empty<string>() }]);
        var probeEditor = new FakeEditor(doc);
        var probe = await Coordinator(probeEditor).ExportAsync(P("probe.vsdx"), probeEditor.Scope, false, default);
        Assert.True(probe.IsOk, probe.Error?.Message);
        var size = probe.Value.Bytes;

        var atLimit = new FakeEditor(doc);
        var ok = await Coordinator(atLimit, limits: new ImportOptions(MaxPackageBytes: size)).ExportAsync(P("at.vsdx"), atLimit.Scope, false, default);
        Assert.True(ok.IsOk, ok.Error?.Message);
        Assert.True(File.Exists(P("at.vsdx")));

        var overEditor = new FakeEditor(doc);
        var refused = await Coordinator(overEditor, limits: new ImportOptions(MaxPackageBytes: size - 1)).ExportAsync(P("over.vsdx"), overEditor.Scope, false, default);
        Assert.False(refused.IsOk);
        Assert.Equal("limit_exceeded", refused.Error!.Code); // not internal_error
        Assert.False(File.Exists(P("over.vsdx")));
        var d = refused.Error.Details!;
        Assert.Equal(size, d["sizeBytes"]!.GetValue<long>());
        Assert.Equal(size - 1, d["limitBytes"]!.GetValue<long>());
        Assert.Equal("logo.png", d["largestPictures"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(png.Length, d["largestPictures"]![0]!["bytes"]!.GetValue<long>());
        Assert.Contains("logo.png", refused.Error.Message);
    }

    [Fact]
    public async Task ExportWritesTheDocumentAtTheBarrierRevisionAndDoesNotMarkItSaved()
    {
        var editor = new FakeEditor(Plain() with { Revision = 5 });
        editor.AfterSnapshot = editor.Edit; // revision 6 happens after the barrier
        var path = P("a.vsdx");
        var r = await Coordinator(editor).ExportAsync(path, editor.Scope, false, default);
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.Equal(5, r.Value.Revision);
        Assert.Equal(6, editor.Revision); // still ahead of the file
        Assert.Equal(0, editor.MarkSavedCalls); // a Visio export is not a save: the editor keeps what the file cannot hold
        Assert.Equal(Doc.Id(1), RT.El(RT.Import(File.ReadAllBytes(path)).Document, Doc.Id(1))["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ALossyExportNeedsAgreementAndWritesNothingUntilItIsGiven()
    {
        var svgSha = await store.PutDurableAsync(Fx.Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>"), default);
        var doc = Doc.Make([Doc.Page([ImageNode(1, "asset:logo"), Doc.Shape(2, 10, 10, 50, 30)])], [new { id = "asset:logo", name = "logo.svg", mimeType = "image/svg+xml", sha256 = svgSha, tags = Array.Empty<string>() }]);
        var editor = new FakeEditor(doc);
        var path = P("lossy.vsdx");
        var refused = await Coordinator(editor).ExportAsync(path, editor.Scope, false, default);
        Assert.Equal("compatibility_loss", refused.Error!.Code);
        Assert.False(File.Exists(path));
        Assert.Contains(refused.Error.Details!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "svg_picture_not_exported"); // what would be lost is named
        var agreed = await Coordinator(editor).ExportAsync(path, editor.Scope, true, default);
        Assert.True(agreed.IsOk, agreed.Error?.Message);
        Assert.True(File.Exists(path));
        Assert.True(agreed.Value.Report.IsLossy);
    }

    [Fact]
    public async Task ASvgPictureIsRasterisedThroughTheEditorSoNothingIsLost()
    {
        var svgSha = await store.PutDurableAsync(Fx.Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>"), default);
        var doc = Doc.Make([Doc.Page([ImageNode(1, "asset:logo")])], [new { id = "asset:logo", name = "logo.svg", mimeType = "image/svg+xml", sha256 = svgSha, tags = Array.Empty<string>() }]);
        var editor = new FakeEditor(doc);
        var r = await Coordinator(editor, new FakeRaster()).ExportAsync(P("svg.vsdx"), editor.Scope, false, default);
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.False(r.Value.Report.IsLossy);
    }

    [Fact]
    public async Task AMissingBlobStopsTheExportAndLeavesAnExistingFileAlone()
    {
        var doc = Doc.Make([Doc.Page([ImageNode(1, "asset:gone")])], [new { id = "asset:gone", name = "gone.png", mimeType = "image/png", sha256 = new string('a', 64), tags = Array.Empty<string>() }]);
        var editor = new FakeEditor(doc);
        var path = P("keep.vsdx");
        await File.WriteAllBytesAsync(path, Fx.Utf8("previous file"));
        var r = await Coordinator(editor).ExportAsync(path, editor.Scope, true, default);
        Assert.Equal("not_found", r.Error!.Code);
        Assert.Equal("previous file", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AStaleOrMismatchedSidecarFailsTheExport()
    {
        var editor = new FakeEditor(Plain());
        editor.Tamper = s => s with { Projection = s.Projection with { Revision = s.Revision + 3 } };
        var path = P("stale.vsdx");
        var r = await Coordinator(editor).ExportAsync(path, editor.Scope, true, default);
        Assert.Equal("invalid_request", r.Error!.Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AWriteFailurePreservesTheOriginal()
    {
        var editor = new FakeEditor(Plain());
        var path = P("orig.vsdx");
        await File.WriteAllBytesAsync(path, Fx.Utf8("original bytes"));
        writer.BeforeReplace = () => throw new IOException("disk full");
        var r = await Coordinator(editor).ExportAsync(path, editor.Scope, true, default);
        writer.BeforeReplace = null;
        Assert.Equal("io_error", r.Error!.Code);
        Assert.Equal("original bytes", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(dir, ".*.tmp"));
    }

    [Fact]
    public async Task ExportRefusesAStaleScopeAndABadPath()
    {
        var editor = new FakeEditor(Plain());
        var stale = editor.Scope; editor.NewSession();
        Assert.Equal("session_mismatch", (await Coordinator(editor).ExportAsync(P("s.vsdx"), stale, true, default)).Error!.Code);
        Assert.Equal("path_not_permitted", (await Coordinator(editor).ExportAsync(@"\\server\share\x.vsdx", editor.Scope, true, default)).Error!.Code);
        Assert.Equal("invalid_request", (await Coordinator(editor).ExportAsync(P("x.txt"), editor.Scope, true, default)).Error!.Code);
    }

    // ---- open ------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenReplacesTheDocumentAndReportsWhatItDidNotBringAcross()
    {
        var path = P("in.vsdx"); await File.WriteAllBytesAsync(path, SamplePackage());
        var editor = new FakeEditor(Doc.Make([Doc.Page([])]) with { Revision = 7 });
        var r = await Coordinator(editor).OpenAsync(path, editor.Expect, default);
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.Equal(1, editor.ReplaceCalls);
        Assert.Equal("vsdx", editor.Document.Source!.Format);
        Assert.Equal(path, editor.Document.Source.Path);
        Assert.Equal(3, RT.All(editor.Document).Count());
        Assert.False(r.Value.Report.IsLossy);
    }

    [Fact]
    public async Task AnInvalidWholeCandidateIsRejectedAndTheExistingDocumentIsUntouched()
    {
        var existing = Plain() with { Revision = 4 };
        var editor = new FakeEditor(existing);
        var types = Fx.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        foreach (var (name, bytes) in new[] { ("junk.vsdx", Fx.Utf8("not a package")), ("evil.vsdx", Fx.Zip(("[Content_Types].xml", types), ("../evil.xml", Fx.Utf8("<a/>")))) })
        {
            var path = P(name); await File.WriteAllBytesAsync(path, bytes);
            Assert.False((await Coordinator(editor).OpenAsync(path, editor.Expect, default)).IsOk);
        }
        Assert.Equal(0, editor.ReplaceCalls);
        Assert.Same(existing, editor.Document);
        Assert.Equal(4, editor.Revision);
        Assert.Equal("not_found", (await Coordinator(editor).OpenAsync(P("missing.vsdx"), editor.Expect, default)).Error!.Code);
        Assert.Equal("invalid_request", (await Coordinator(editor).OpenAsync(P("junk.txt"), editor.Expect, default)).Error!.Code);
    }

    [Fact]
    public async Task ALateNativeOpenCannotReplaceNewEditsOrANewSession()
    {
        var path = P("late.vsdx"); await File.WriteAllBytesAsync(path, SamplePackage());
        var edited = new FakeEditor(Plain());
        edited.BeforeReplace = _ => edited.Edit(); // the person kept editing while the file was being read
        Assert.Equal("revision_conflict", (await Coordinator(edited).OpenAsync(path, edited.Expect, default)).Error!.Code);
        Assert.Equal(0, edited.ReplaceCalls);

        var swapped = new FakeEditor(Plain());
        swapped.BeforeReplace = _ => swapped.NewSession();
        Assert.Equal("session_mismatch", (await Coordinator(swapped).OpenAsync(path, swapped.Expect, default)).Error!.Code);
        Assert.Equal(0, swapped.ReplaceCalls);
    }

    [Fact]
    public async Task ImportedNativeBlobsAreDurableBeforeTheDocumentIsPublished()
    {
        var blobs = new Blobs(); var png = Fx.Png(16, 8); var sha = blobs.Add(png);
        var doc = Doc.Make([Doc.Page([ImageNode(1, "asset:a")])], [new { id = "asset:a", name = "a.png", mimeType = "image/png", sha256 = sha, tags = Array.Empty<string>() }]);
        var path = P("img.vsdx"); await File.WriteAllBytesAsync(path, RT.Export(Doc.Snapshot(doc), blobs).Package);
        var editor = new FakeEditor(Doc.Make([Doc.Page([])]));
        var durableWhenPublished = false;
        editor.BeforeReplace = d => durableWhenPublished = d.Assets.Count > 0 && d.Assets.All(a => store.Contains(a.Sha256));
        var r = await Coordinator(editor).OpenAsync(path, editor.Expect, default);
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.True(durableWhenPublished);
        Assert.Equal(png, store.Read(sha));
    }

    [Fact]
    public async Task IfTheBlobsCannotBeStoredTheDocumentIsNotPublished()
    {
        var blobs = new Blobs(); var sha = blobs.Add(Fx.Png(16, 8));
        var doc = Doc.Make([Doc.Page([ImageNode(1, "asset:a")])], [new { id = "asset:a", name = "a.png", mimeType = "image/png", sha256 = sha, tags = Array.Empty<string>() }]);
        var path = P("img2.vsdx"); await File.WriteAllBytesAsync(path, RT.Export(Doc.Snapshot(doc), blobs).Package);
        var folder = Path.Combine(dir, "blobs2");
        var broken = new BlobStore(folder);
        Directory.Delete(folder);
        await File.WriteAllTextAsync(folder, "a file where the blob folder should be");
        var editor = new FakeEditor(Doc.Make([Doc.Page([])]));
        var r = await Coordinator(editor, null, broken).OpenAsync(path, editor.Expect, default);
        Assert.Equal("io_error", r.Error!.Code);
        Assert.Equal(0, editor.ReplaceCalls);
    }

    [Fact]
    public async Task OpenThenExportRoundTripsThroughTheCoordinators()
    {
        var first = P("one.vsdx"); var second = P("two.vsdx");
        var editor = new FakeEditor(Plain());
        Assert.True((await Coordinator(editor).ExportAsync(first, editor.Scope, true, default)).IsOk);
        var reopened = new FakeEditor(Doc.Make([Doc.Page([])]));
        Assert.True((await Coordinator(reopened).OpenAsync(first, reopened.Expect, default)).IsOk);
        Assert.True((await Coordinator(reopened).ExportAsync(second, reopened.Scope, true, default)).IsOk);
        Assert.Equal(RT.All(RT.Import(File.ReadAllBytes(first)).Document).Select(e => e.ToJsonString()).OrderBy(x => x), RT.All(RT.Import(File.ReadAllBytes(second)).Document).Select(e => e.ToJsonString()).OrderBy(x => x));
    }
}
