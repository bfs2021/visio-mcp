using System.Text.Json;
namespace Diagram.Host.Core;

public sealed record RecentFile(string Id, string Path, string Kind);
public sealed record RecentFilesState(IReadOnlyList<RecentFile> Entries, string? LastOpenFolder, string? Warning = null);

/// <summary>Bounded per-user history. Entry IDs are host-owned capabilities; removing history never deletes drawings.</summary>
public sealed class RecentFilesStore(string file, AtomicFileWriter writer)
{
    const int MaxBytes = 131072;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly SemaphoreSlim gate = new(1, 1);
    sealed record Persisted(int Version, List<RecentFile> Entries, string? LastOpenFolder);
    public string File => file;
    static string Normalize(string path)
    {
        if (path.Length > 4096 || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Recent paths must be bounded absolute paths");
        return Path.GetFullPath(path);
    }
    async Task<RecentFilesState> ReadCore(CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > MaxBytes) throw new JsonException();
            using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            { if (bytes.Length + count > MaxBytes) throw new JsonException(); await bytes.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); }
            var p = JsonSerializer.Deserialize<Persisted>(bytes.ToArray(), Json);
            if (p is null || p.Version != 1 || p.Entries is null || p.Entries.Count > 10) throw new JsonException();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in p.Entries)
                if (entry is null || !Guid.TryParseExact(entry.Id, "N", out _) || entry.Kind is not ("json" or "vsdx") || entry.Path is null || !ids.Add(entry.Id) || !paths.Add(Normalize(entry.Path))) throw new JsonException();
            if (p.LastOpenFolder is not null) Normalize(p.LastOpenFolder);
            return new(p.Entries, p.LastOpenFolder);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new([], null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new([], null, "Recent-file history could not be read. It will be replaced after the next successful history update."); }
    }
    public async Task<RecentFilesState> ReadAsync(CancellationToken ct = default)
    { await gate.WaitAsync(ct).ConfigureAwait(false); try { return await ReadCore(ct).ConfigureAwait(false); } finally { gate.Release(); } }
    async Task Change(Func<RecentFilesState, RecentFilesState> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = change(await ReadCore(ct).ConfigureAwait(false));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await writer.WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(new Persisted(1, next.Entries.ToList(), next.LastOpenFolder), Json), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public Task RecordAsync(string path, string kind, bool opened, CancellationToken ct = default)
    {
        path = Normalize(path); if (kind is not ("json" or "vsdx")) throw new ArgumentException("Unsupported recent file kind");
        return Change(s => { var old = s.Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)); var entry = new RecentFile(old?.Id ?? Guid.NewGuid().ToString("N"), path, kind); return new(new[] { entry }.Concat(s.Entries.Where(e => e.Id != old?.Id)).Take(10).ToArray(), opened ? Path.GetDirectoryName(path) : s.LastOpenFolder); }, ct);
    }
    public Task RemoveAsync(string id, CancellationToken ct = default) => Change(s => s with { Entries = s.Entries.Where(e => e.Id != id).ToArray() }, ct);
    public Task ClearAsync(CancellationToken ct = default) => Change(s => s with { Entries = [] }, ct);
    public async Task<RecentFile?> ResolveAsync(string id, string kind, CancellationToken ct = default) => (await ReadAsync(ct).ConfigureAwait(false)).Entries.FirstOrDefault(e => e.Id == id && e.Kind == kind);
    /// <summary>Run native availability probes off the UI thread; failure does not remove history.</summary>
    public static Task<string> AvailabilityAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        try { return (System.IO.File.GetAttributes(path) & FileAttributes.Directory) != 0 ? "unavailable" : "available"; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return "missing"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }, ct);
}
