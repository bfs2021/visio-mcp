using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

public sealed record ExpectedState
{
    public required string DocumentId { get; init; }
    public required string SessionId { get; init; }
    public required int BaseRevision { get; init; }
}

public sealed record SavedFile(Scope Scope, int Revision, string Path);
public sealed record OpenedFile(Snapshot Snapshot, IReadOnlyList<string> Notes);

/// <summary>
/// The host's view of the editor. The editor (the frontend engine) owns the document and every check on scope and revision;
/// the host only asks for barriers and publishes validated candidates. There is no host-side editable document.
/// </summary>
public interface IEditorChannel
{
    Task<Result<Snapshot>> SnapshotAsync(Scope scope, CancellationToken ct);
    Task<Result<Snapshot>> ReplaceAsync(DiagramDocument document, ExpectedState expected, CancellationToken ct);
    Task<Result<Unit>> MarkSavedAsync(Scope scope, int revision, string path, CancellationToken ct);
}

/// <summary>
/// JSON open and save. A file is a wrapper {format, formatVersion, document, blobs}: the canonical document plus each asset's bytes once, keyed by hash.
/// Open validates everything before the editor sees it; save never marks newer state or another session clean.
/// </summary>
public sealed class SessionCoordinator(IEditorChannel editor, BlobStore blobs, AssetPreparer preparer, AtomicFileWriter writer)
{
    public const string Format = "native-json";

    public async Task<Result<OpenedFile>> OpenJsonAsync(string path, ExpectedState expected, CancellationToken ct)
    {
        var safe = PathPolicy.Validate(path);
        if (!safe.IsOk) return Result<OpenedFile>.Fail(safe.Error!);

        byte[] bytes;
        try
        {
            var info = new FileInfo(safe.Value);
            if (!info.Exists) return Result<OpenedFile>.Fail("not_found", "the file does not exist");
            if (info.Length > InputLimits.NativeJsonBytes) return Result<OpenedFile>.Fail("limit_exceeded", $"file is {info.Length} bytes; the limit is {InputLimits.NativeJsonBytes}");
            bytes = await File.ReadAllBytesAsync(safe.Value, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<OpenedFile>.Fail("io_error", "the file could not be read", retryable: true);
        }

        var budget = JsonBudget.Check(bytes);
        if (!budget.IsOk) return Result<OpenedFile>.Fail(budget.Error!);

        JsonObject root;
        try { root = JsonNode.Parse(bytes)?.AsObject() ?? throw new JsonException(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return Result<OpenedFile>.Fail("invalid_request", "the file is not a JSON object"); }

        if (root["format"]?.GetValue<string>() != Format || root["formatVersion"]?.GetValue<int>() != 1)
            return Result<OpenedFile>.Fail("invalid_request", "this is not a native diagram JSON file (format native-json, version 1)");

        DiagramDocument document;
        try { document = root["document"].Deserialize<DiagramDocument>(ContractJson.Options) ?? throw new JsonException("no document"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return Result<OpenedFile>.Fail("invalid_request", "the document is invalid: " + e.Message); }

        var blobMap = root["blobs"] as JsonObject ?? new JsonObject();
        var notes = new List<string>();
        var approvedBySha = new Dictionary<string, (string NewSha, byte[] Bytes)>();
        var assets = new List<Asset>();
        foreach (var asset in document.Assets)
        {
            if (!approvedBySha.TryGetValue(asset.Sha256, out var approved))
            {
                if (blobMap[asset.Sha256]?.GetValue<string>() is not { } b64)
                    return Result<OpenedFile>.Fail("invalid_request", $"asset {asset.Id} has no embedded bytes for {asset.Sha256}");
                if (b64.Length / 4 * 3 > InputLimits.RasterBytes + 3) return Result<OpenedFile>.Fail("limit_exceeded", $"asset {asset.Id} is larger than {InputLimits.RasterBytes} bytes");
                byte[] raw;
                try { raw = Convert.FromBase64String(b64); }
                catch (FormatException) { return Result<OpenedFile>.Fail("invalid_request", $"asset {asset.Id} is not valid base64"); }
                if (BlobStore.HashOf(raw) != asset.Sha256) return Result<OpenedFile>.Fail("invalid_request", $"the bytes for asset {asset.Id} do not match their hash");

                var ok = preparer.Approve(raw, asset.MimeType); // header budgets and SVG allowlist run before anything is stored or published
                if (!ok.IsOk) return Result<OpenedFile>.Fail(ok.Error!);
                foreach (var n in ok.Value.Notes) notes.Add($"{asset.Id}: {n}");
                approved = (BlobStore.HashOf(ok.Value.Bytes), ok.Value.Bytes);
                approvedBySha[asset.Sha256] = approved;
            }
            assets.Add(asset with { Sha256 = approved.NewSha }); // sanitising changes the bytes, so the descriptor follows
        }

        foreach (var (_, a) in approvedBySha) await blobs.PutDurableAsync(a.Bytes, ct); // durable before the editor can see a reference to it

        var candidate = document with
        {
            Assets = assets,
            Source = new DocumentSource { Format = Format, Path = safe.Value },
        };
        var replaced = await editor.ReplaceAsync(candidate, expected, ct);
        return replaced.IsOk ? Result<OpenedFile>.Ok(new OpenedFile(replaced.Value, notes)) : Result<OpenedFile>.Fail(replaced.Error!);
    }

    public async Task<Result<SavedFile>> SaveJsonAsync(string path, Scope scope, CancellationToken ct)
    {
        var safe = PathPolicy.Validate(path);
        if (!safe.IsOk) return Result<SavedFile>.Fail(safe.Error!);

        // The snapshot is a queue barrier: it is the committed state at one exact revision, whatever happens next.
        var snap = await editor.SnapshotAsync(scope, ct);
        if (!snap.IsOk) return Result<SavedFile>.Fail(snap.Error!);
        var snapshot = snap.Value;

        var blobMap = new JsonObject();
        try
        {
            foreach (var asset in snapshot.Document.Assets)
            {
                if (blobMap.ContainsKey(asset.Sha256)) continue;
                var data = await blobs.ReadAsync(asset.Sha256, ct);
                blobMap[asset.Sha256] = Convert.ToBase64String(data.Span);
            }
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or UnauthorizedAccessException)
        {
            return Result<SavedFile>.Fail("io_error", $"an asset's bytes could not be read: {e.Message}");
        }

        var file = new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = 1,
            ["document"] = JsonSerializer.SerializeToNode(snapshot.Document, ContractJson.Options),
            ["blobs"] = blobMap,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file);
        if (bytes.Length > InputLimits.NativeJsonBytes) return Result<SavedFile>.Fail("limit_exceeded", "the document is too large to save as JSON");

        try { await writer.WriteAsync(safe.Value, bytes, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<SavedFile>.Fail("io_error", $"the file could not be written; the previous file is unchanged: {e.Message}", retryable: true);
        }

        // Only the revision actually written is marked saved, and only if the session is still the one we snapshotted.
        var marked = await editor.MarkSavedAsync(new Scope { DocumentId = snapshot.DocumentId, SessionId = snapshot.SessionId }, snapshot.Revision, safe.Value, ct);
        if (!marked.IsOk) return Result<SavedFile>.Fail(marked.Error!);
        return Result<SavedFile>.Ok(new SavedFile(new Scope { DocumentId = snapshot.DocumentId, SessionId = snapshot.SessionId }, snapshot.Revision, safe.Value));
    }
}
