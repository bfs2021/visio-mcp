using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.Visio;

/// <summary>What was and was not carried across, in a form a person (or an agent) can read.</summary>
public sealed record CompatibilityReport(IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Something the other side cannot hold would be lost or changed: a person must agree before it happens.</summary>
    public bool IsLossy => Diagnostics.Any(d => d.Severity is "warning" or "error");

    public IReadOnlyList<Diagnostic> Warnings => Diagnostics.Where(d => d.Severity is "warning" or "error").ToList();

    public string Summary
    {
        get
        {
            var w = Warnings.Count; var i = Diagnostics.Count - w;
            return w == 0 && i == 0 ? "Everything was carried across." : $"{w} thing{(w == 1 ? "" : "s")} could not be carried across exactly, and {i} note{(i == 1 ? "" : "s")}.";
        }
    }

    public JsonArray ToJson() => new(Diagnostics.Select(d => (JsonNode)System.Text.Json.JsonSerializer.SerializeToNode(d, ContractJson.Options)!).ToArray());
}

public sealed record NativeOpened(Snapshot Snapshot, CompatibilityReport Report, IdentityReport Identity, string Path);
public sealed record NativeExported(string Path, int Revision, int Bytes, CompatibilityReport Report, IReadOnlyDictionary<string, string> NativeIds);

/// <summary>Image bytes for export: the durable store, plus the editor's drawing surface for pictures a Visio file cannot hold as they are.</summary>
public sealed class StoreBlobSource(BlobStore store, IRasteriser? rasteriser) : IAssetBlobSource
{
    public Task<ReadOnlyMemory<byte>> ReadAsync(string sha256, CancellationToken ct) => store.ReadAsync(sha256, ct);

    public async Task<ReadOnlyMemory<byte>?> RasteriseAsync(string sha256, CancellationToken ct)
    {
        if (rasteriser is null) return null;
        var r = await rasteriser.RasteriseAsync(sha256, ct);
        return r.IsOk ? r.Value : null;
    }
}

/// <summary>
/// Native (.vsdx) open and export, built on the same rules as the JSON lifecycle: the file is prepared outside the editor's queue, every
/// image is durable before the document is published, the document is replaced only if the editor is still where the open started, and an
/// export is a barrier snapshot at one revision. Exporting never marks the document saved (the editor keeps what a Visio file cannot hold).
/// </summary>
public sealed class NativeFileCoordinator(IEditorChannel editor, IExportSource source, BlobStore blobs, AtomicFileWriter writer, IVsdxImporter importer, IVsdxExporter exporter, IRasteriser? rasteriser = null)
{
    public async Task<Result<NativeOpened>> OpenAsync(string path, ExpectedState expected, CancellationToken ct)
    {
        var safe = PathPolicy.Validate(path);
        if (!safe.IsOk) return Result<NativeOpened>.Fail(safe.Error!);
        if (!string.Equals(Path.GetExtension(safe.Value), ".vsdx", StringComparison.OrdinalIgnoreCase)) return Result<NativeOpened>.Fail("invalid_request", "this is not a Visio drawing (.vsdx)");
        var options = new ImportOptions();
        byte[] bytes;
        try
        {
            var info = new FileInfo(safe.Value);
            if (!info.Exists) return Result<NativeOpened>.Fail("not_found", "the file does not exist");
            if (info.Length > options.MaxPackageBytes) return Result<NativeOpened>.Fail("limit_exceeded", $"file is {info.Length} bytes; the limit is {options.MaxPackageBytes}");
            bytes = await File.ReadAllBytesAsync(safe.Value, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Result<NativeOpened>.Fail("io_error", "the file could not be read", retryable: true); }

        // everything below runs outside the editor's queue and touches nothing the user can see
        var imported = await importer.ImportAsync(new MemoryStream(bytes), options, ct);
        if (!imported.IsOk) return Result<NativeOpened>.Fail(imported.Error!);
        var result = imported.Value;

        try { foreach (var blob in result.Blobs) await blobs.PutDurableAsync(blob.Bytes, ct); } // durable before the editor can see a reference to it
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Result<NativeOpened>.Fail("io_error", "the pictures in the file could not be stored, so the document was not opened: " + e.Message, retryable: true); }

        var candidate = result.Document with { Source = new DocumentSource { Format = "vsdx", Path = safe.Value } };
        var replaced = await editor.ReplaceAsync(candidate, expected, ct);
        if (!replaced.IsOk) return Result<NativeOpened>.Fail(replaced.Error!);
        return Result<NativeOpened>.Ok(new NativeOpened(replaced.Value, new CompatibilityReport(result.Diagnostics), result.Identity, safe.Value));
    }

    /// <param name="revalidate">Run immediately before the file is written: a last look at whether the destination is still the one that was approved.</param>
    public async Task<Result<NativeExported>> ExportAsync(string path, Scope scope, bool acceptLoss, CancellationToken ct, Func<Result<Unit>>? revalidate = null)
    {
        var safe = PathPolicy.Validate(path);
        if (!safe.IsOk) return Result<NativeExported>.Fail(safe.Error!);
        if (!string.Equals(Path.GetExtension(safe.Value), ".vsdx", StringComparison.OrdinalIgnoreCase)) return Result<NativeExported>.Fail("invalid_request", "a Visio export must be saved as .vsdx");

        var snap = await source.ExportSnapshotAsync(scope, ct); // a queue barrier: committed state at one revision, whatever happens next
        if (!snap.IsOk) return Result<NativeExported>.Fail(snap.Error!);
        var exported = await exporter.ExportAsync(snap.Value, new StoreBlobSource(blobs, rasteriser), new ExportOptions(), ct);
        if (!exported.IsOk) return Result<NativeExported>.Fail(exported.Error!);
        var report = new CompatibilityReport(exported.Value.Diagnostics);

        if (report.IsLossy && !acceptLoss)
            return Result<NativeExported>.Fail("compatibility_loss", report.Summary + " Nothing was written.", details: new JsonObject { ["diagnostics"] = report.ToJson(), ["revision"] = snap.Value.Revision });

        var check = await Verify(snap.Value, exported.Value, ct);
        if (!check.IsOk) return Result<NativeExported>.Fail(check.Error!);

        if (revalidate?.Invoke() is { IsOk: false } stale) return Result<NativeExported>.Fail(stale.Error!);
        try { await writer.WriteAsync(safe.Value, exported.Value.Package, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<NativeExported>.Fail("io_error", $"the file could not be written; any previous file is unchanged: {e.Message}", retryable: true);
        }
        return Result<NativeExported>.Ok(new NativeExported(safe.Value, snap.Value.Revision, exported.Value.Package.Length, report, exported.Value.NativeIds));
    }

    /// <summary>Reads the package back before it is saved: the same elements, ids, pages and references must come out as went in.</summary>
    async Task<Result<Unit>> Verify(ExportSnapshot snapshot, ExportResult exported, CancellationToken ct)
    {
        var back = await importer.ImportAsync(new MemoryStream(exported.Package), new ImportOptions(), ct);
        if (!back.IsOk) return Result<Unit>.Fail("internal_error", "the exported file could not be read back, so it was not saved: " + back.Error!.Message);
        var skipped = exported.Diagnostics.Where(d => d.Code == "svg_picture_not_exported").Select(d => d.ElementId).ToHashSet();
        string[] Ids(DiagramDocument d) => d.Pages.SelectMany(p => p.Elements).Select(e => System.Text.Json.JsonSerializer.SerializeToNode(e, ContractJson.Options)!["id"]!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var expected = Ids(snapshot.Document).Where(i => !skipped.Contains(i)).ToArray();
        var actual = Ids(back.Value.Document);
        if (back.Value.Document.Pages.Count != snapshot.Document.Pages.Count || !expected.SequenceEqual(actual))
            return Result<Unit>.Fail("internal_error", "the exported file does not contain the same pages and objects as the document, so it was not saved", details: new JsonObject { ["expected"] = expected.Length, ["found"] = actual.Length });
        var ids = actual.ToHashSet();
        foreach (var e in back.Value.Document.Pages.SelectMany(p => p.Elements))
        {
            var j = System.Text.Json.JsonSerializer.SerializeToNode(e, ContractJson.Options)!;
            foreach (var side in new[] { "from", "to" })
                if (j[side]?["elementId"]?.GetValue<string>() is { } target && !ids.Contains(target)) return Result<Unit>.Fail("internal_error", "the exported file has a connector pointing at a missing object, so it was not saved");
        }
        return Result<Unit>.Ok(default);
    }
}
