using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

/// <summary>One approved picture in the per-user library, as the person and the agent see it.</summary>
public sealed record LibraryAsset(
    string Id, string Name, IReadOnlyList<string> Tags, string Provenance, string MimeType, string Sha256, int Width, int Height, int Version,
    DateTimeOffset UpdatedAt, IReadOnlyList<string> History)
{
    [JsonIgnore] public string Scope => "library";

    public Asset ToDescriptor() => new() { Id = Id, Name = Name, MimeType = MimeType, Sha256 = Sha256, Tags = Tags, Provenance = Provenance };
}

/// <summary>What importing did: the entry, whether it is new, and whether the stable id was already taken by different bytes.</summary>
public sealed record LibraryImport(LibraryAsset Asset, bool Created, bool IdWasTaken, IReadOnlyList<string> Notes);

/// <summary>
/// The per-user catalogue of approved pictures. Bytes live in the content-addressed <see cref="BlobStore"/> and are durable before the catalogue
/// names them. A document pins a descriptor and hash when it uses a library picture, so changing a source here never changes an open document.
/// </summary>
public sealed class AssetLibrary
{
    readonly string file;
    readonly BlobStore blobs;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly AtomicFileWriter writer = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AssetLibrary(string directory, BlobStore blobs)
    {
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "library.json");
        this.blobs = blobs;
    }

    /// <summary>Fault-injection hook for the catalogue write, run after the temp file is durable and before it replaces the catalogue.</summary>
    public Action? BeforeCatalogueReplace { get => writer.BeforeReplace; set => writer.BeforeReplace = value; }

    async Task<List<LibraryAsset>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        try
        {
            var all = JsonSerializer.Deserialize<List<LibraryAsset>>(await File.ReadAllTextAsync(file, ct), Json) ?? [];
            return all.Where(a => blobs.Contains(a.Sha256)).ToList(); // an entry whose bytes are gone is not offered
        }
        catch (JsonException) { return []; }
    }

    Task SaveAsync(List<LibraryAsset> all, CancellationToken ct) => writer.WriteAsync(file, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all, Json)), ct);

    public async Task<IReadOnlyList<LibraryAsset>> ListAsync(string? query, IReadOnlyList<string>? tags, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            return all.Where(a =>
                    (string.IsNullOrWhiteSpace(query) || a.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    && (tags is null || tags.Count == 0 || a.Tags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase))))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { gate.Release(); }
    }

    public async Task<LibraryAsset?> GetAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).FirstOrDefault(a => a.Id == id); }
        finally { gate.Release(); }
    }

    /// <summary>Adds a prepared (already validated and durable) upload. The same bytes under the same name are not added twice.</summary>
    public async Task<Result<LibraryImport>> ImportAsync(PreparedUpload prepared, IReadOnlyList<string>? tags, CancellationToken ct)
    {
        var src = prepared.Ref.Asset;
        if (!blobs.Contains(src.Sha256)) return Result<LibraryImport>.Fail("not_found", "the picture's bytes are not stored, so it cannot be added");
        await gate.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            var slug = AssetPreparer.SlugOf(src.Name);
            var stable = $"asset:{slug}";
            var sameBytes = all.FirstOrDefault(a => a.Sha256 == src.Sha256 && (a.Id == stable || a.Id.StartsWith(stable + "~", StringComparison.Ordinal)));
            if (sameBytes is not null) return Result<LibraryImport>.Ok(new LibraryImport(sameBytes, false, false, prepared.Notes));
            var taken = all.Any(a => a.Id == stable);
            var id = taken ? $"{stable}~{src.Sha256}" : stable; // a collision never overwrites: the newcomer gets the full-hash id
            var entry = new LibraryAsset(id, src.Name, Clean(tags), "library-import", src.MimeType, src.Sha256, prepared.Width, prepared.Height, 1, DateTimeOffset.UtcNow, [src.Sha256]);
            all.Add(entry);
            await SaveAsync(all, ct);
            return Result<LibraryImport>.Ok(new LibraryImport(entry, true, taken, prepared.Notes));
        }
        finally { gate.Release(); }
    }

    /// <summary>Points a library entry at new bytes. Documents that already pinned the old bytes keep them; the old version stays available by hash.</summary>
    public async Task<Result<LibraryAsset>> ReplaceSourceAsync(string id, PreparedUpload prepared, CancellationToken ct)
    {
        var src = prepared.Ref.Asset;
        if (!blobs.Contains(src.Sha256)) return Result<LibraryAsset>.Fail("not_found", "the picture's bytes are not stored");
        await gate.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0) return Result<LibraryAsset>.Fail("not_found", $"no library asset {id}");
            var old = all[at];
            if (old.Sha256 == src.Sha256) return Result<LibraryAsset>.Ok(old);
            var history = old.History.Contains(src.Sha256) ? old.History : [.. old.History, src.Sha256];
            var next = old with { Sha256 = src.Sha256, MimeType = src.MimeType, Width = prepared.Width, Height = prepared.Height, Version = old.Version + 1, UpdatedAt = DateTimeOffset.UtcNow, History = history };
            all[at] = next;
            await SaveAsync(all, ct);
            return Result<LibraryAsset>.Ok(next);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<LibraryAsset>> SetMetadataAsync(string id, string? name, IReadOnlyList<string>? tags, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            var at = all.FindIndex(a => a.Id == id);
            if (at < 0) return Result<LibraryAsset>.Fail("not_found", $"no library asset {id}");
            if (name is not null && (name.Length == 0 || name.Length > 128)) return Result<LibraryAsset>.Fail("invalid_request", "a name is 1 to 128 characters");
            var next = all[at] with { Name = name ?? all[at].Name, Tags = tags is null ? all[at].Tags : Clean(tags), UpdatedAt = DateTimeOffset.UtcNow };
            all[at] = next;
            await SaveAsync(all, ct);
            return Result<LibraryAsset>.Ok(next);
        }
        finally { gate.Release(); }
    }

    /// <summary>Removes the entry. The bytes stay in the store: documents and recovery may still pin them.</summary>
    public async Task<Result<Unit>> DeleteAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            if (all.RemoveAll(a => a.Id == id) == 0) return Result<Unit>.Fail("not_found", $"no library asset {id}");
            await SaveAsync(all, ct);
            return Result<Unit>.Ok(default);
        }
        finally { gate.Release(); }
    }

    static List<string> Clean(IReadOnlyList<string>? tags) =>
        (tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 64).Distinct().Take(32).ToList();
}

/// <summary>Turns a library id (and optionally a version hash) into a preparation a document can pin, in the same shape as a fresh upload.</summary>
public sealed class AssetResolver(AssetLibrary library, AssetPreparer preparer)
{
    /// <param name="scope">"library" (the only scope the host can resolve; a document's own assets are resolved inside the document).</param>
    public async Task<Result<PreparedUpload>> ResolveAsync(string id, string scope, string? hash, CancellationToken ct)
    {
        if (scope != "library") return Result<PreparedUpload>.Fail("invalid_request", "only library assets are resolved by the host; a document's own assets are already in the document");
        var entry = await library.GetAsync(id, ct);
        if (entry is null) return Result<PreparedUpload>.Fail("not_found", $"no library asset {id}");
        var sha = hash ?? entry.Sha256;
        if (sha != entry.Sha256 && !entry.History.Contains(sha)) return Result<PreparedUpload>.Fail("not_found", $"{id} has no version with hash {sha}");
        return preparer.PrepareExisting(entry.ToDescriptor() with { Sha256 = sha }, entry.Width, entry.Height);
    }
}
