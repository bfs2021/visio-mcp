namespace Diagram.Host.Core;

/// <summary>The exact bytes observed, with display metadata. Missing is distinct from unreadable (which throws).</summary>
public sealed record DiskVersion(bool Exists, string? Sha256, long? Length, DateTime? LastWriteUtc)
{
    public static DiskVersion Missing { get; } = new(false, null, null, null);
    public static DiskVersion FromBytes(string path, byte[] bytes) => new(true, BlobStore.HashOf(bytes), bytes.LongLength, File.GetLastWriteTimeUtc(path));
    public static async Task<DiskVersion> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length > InputLimits.NativeJsonBytes) throw new IOException("The file exceeds the native diagram size limit.");
            using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes, ct);
            if (bytes.Length > InputLimits.NativeJsonBytes) throw new IOException("The file exceeds the native diagram size limit.");
            return FromBytes(path, bytes.ToArray());
        }
        catch (FileNotFoundException) { return Missing; }
        catch (DirectoryNotFoundException) { return Missing; }
    }
}
public sealed class DiskChangedException(DiskVersion observed) : IOException("The file changed before it could be replaced.")
{
    public DiskVersion Observed { get; } = observed;
}
