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

    public async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new IOException("the path has no directory");
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await fs.WriteAsync(bytes, ct);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }
            BeforeReplace?.Invoke();
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

    public async Task<string> PutDurableAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var sha = HashOf(bytes.Span);
        var path = PathFor(sha);
        if (!File.Exists(path)) await writer.WriteAsync(path, bytes, ct);
        return sha;
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(string sha, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(PathFor(sha), ct);
        if (HashOf(bytes) != sha) throw new IOException($"blob {sha} is corrupt");
        return bytes;
    }
}
