using System.Security.Cryptography;

namespace Diagram.Host.Core;

/// <summary>
/// Writes a file so a failure at any point leaves the previous file byte-for-byte intact: temp file in the same directory,
/// flushed to disk, then replaced in one step. The temp file is removed on failure.
/// </summary>
public sealed class AtomicFileWriter
{
    /// <summary>Fault-injection hook, run after the temp file is durable and before it replaces the target.</summary>
    public Action? BeforeReplace { get; set; }

    public async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct, Func<CancellationToken, Task>? beforeReplace = null)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new IOException("the path has no directory");
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            BeforeReplace?.Invoke();
            if (beforeReplace is not null) await beforeReplace(ct).ConfigureAwait(false);
            if (File.Exists(full)) File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(temp, full);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort: the original is untouched either way */ }
            throw;
        }
    }
}

/// <summary>Immutable content-addressed bytes on disk, named by SHA-256. Writes are durable before the hash is handed out.</summary>
public sealed class BlobStore
{
    readonly string directory;
    readonly AtomicFileWriter writer = new();
    readonly SemaphoreSlim gate = new(1, 1); // a picture handed out again and a collection deciding it is unused never interleave

    public BlobStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }

    public static string HashOf(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    string PathFor(string sha)
    {
        if (sha.Length != 64 || !sha.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) throw new ArgumentException("not a SHA-256 hex string", nameof(sha));
        return Path.Combine(directory, sha);
    }

    public bool Contains(string sha) => File.Exists(PathFor(sha));

    /// <summary>The stored size of a picture, or null if it is not here.</summary>
    public long? SizeOf(string sha) { var f = new FileInfo(PathFor(sha)); return f.Exists ? f.Length : null; }

    /// <summary>Every stored blob, for garbage collection. Temp files from an interrupted write are not blobs and are not listed.</summary>
    public IEnumerable<(string Sha, DateTime LastWriteUtc)> Enumerate() =>
        Directory.EnumerateFiles(directory).Select(f => (Name: Path.GetFileName(f), f))
            .Where(x => x.Name.Length == 64 && x.Name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            .Select(x => (x.Name, File.GetLastWriteTimeUtc(x.f)));

    public bool Delete(string sha)
    {
        var path = PathFor(sha);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Stores the bytes and returns their hash. A picture already here is touched instead, so it counts as new again: its next user may not
    /// have pinned it yet, and garbage collection leaves recently touched pictures alone.
    /// </summary>
    public async Task<string> PutDurableAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var sha = HashOf(bytes.Span);
        var path = PathFor(sha);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) await writer.WriteAsync(path, bytes, ct).ConfigureAwait(false);
            else File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        finally { gate.Release(); }
        return sha;
    }

    /// <summary>
    /// Deletes a picture only if nothing has stored or touched it since <paramref name="idleSince"/>, checked under the same lock as
    /// <see cref="PutDurableAsync"/>. Returns the bytes freed, or null if it was kept or already gone.
    /// </summary>
    public async Task<long?> DeleteIfIdleAsync(string sha, DateTime idleSince, CancellationToken ct)
    {
        var file = new FileInfo(PathFor(sha));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            file.Refresh();
            if (!file.Exists || file.LastWriteTimeUtc >= idleSince) return null;
            var length = file.Length;
            file.Delete();
            return length;
        }
        finally { gate.Release(); }
    }

    /// <summary>Synchronous read for callers that must answer on a UI thread (serving an asset to the page). Never block on <see cref="ReadAsync"/> there.</summary>
    public byte[] Read(string sha)
    {
        var bytes = File.ReadAllBytes(PathFor(sha));
        if (HashOf(bytes) != sha) throw new IOException($"blob {sha} is corrupt");
        return bytes;
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(string sha, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(PathFor(sha), ct).ConfigureAwait(false);
        if (HashOf(bytes) != sha) throw new IOException($"blob {sha} is corrupt");
        return bytes;
    }
}

/// <summary>
/// Marks a picture folder as in use by this process for as long as the lease is held. One window per user and logon session is enforced
/// elsewhere, but the same user signed in twice runs two processes over the same folder, and neither can see the other's documents. A process
/// that holds the only live lease knows nobody else needs the pictures; a lease left by a process that ended is removed when found.
/// </summary>
public sealed class BlobStoreLease : IDisposable
{
    const string Folder = ".holders";
    readonly string holders;
    readonly FileStream held;

    BlobStoreLease(string holders, FileStream held) { this.holders = holders; this.held = held; }

    public static BlobStoreLease Take(string blobDirectory)
    {
        var holders = Path.Combine(Path.GetFullPath(blobDirectory), Folder);
        Directory.CreateDirectory(holders);
        var file = Path.Combine(holders, $"{Environment.ProcessId}-{Guid.NewGuid():N}.lease");
        // Open with no sharing and delete-on-close: another process cannot remove it while this one runs, and it goes when the process does.
        return new BlobStoreLease(holders, new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
    }

    /// <summary>True if no other running process holds a lease on the folder. Leases of processes that ended are cleared on the way.</summary>
    public bool IsSoleHolder()
    {
        foreach (var other in Directory.EnumerateFiles(holders, "*.lease"))
        {
            if (string.Equals(other, held.Name, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(other); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } // still open: that process is alive
        }
        return true;
    }

    public void Dispose() => held.Dispose();
}
