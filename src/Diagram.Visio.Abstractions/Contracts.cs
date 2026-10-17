using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.Visio;

/// <summary>An image whose approved bytes were found in a package: stored by hash, ready to be made durable before the document is published.</summary>
public sealed record ImportedBlob(string Sha256, byte[] Bytes, string MimeType);

/// <summary>What happened to element identity on import. Valid identity is kept; the rest is repaired deterministically and reported.</summary>
public sealed record IdentityReport(int Preserved, int Generated, int Repaired, IReadOnlyList<string> Notes);

public sealed record ImportOptions(long MaxPackageBytes = 64L * 1024 * 1024, long MaxExpandedBytes = 256L * 1024 * 1024, int MaxEntries = 2_000, int MaxPages = 200, int MaxShapes = 20_000);

public sealed record ImportResult(
    DiagramDocument Document, IReadOnlyList<ImportedBlob> Blobs, IReadOnlyList<Diagnostic> Diagnostics, IdentityReport Identity);

public sealed record ExportOptions(string? Title = null, DateTimeOffset? Created = null);

/// <summary>The exported package, what could not be carried over, and where each element went (element id to "page index / shape id").</summary>
public sealed record ExportResult(byte[] Package, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<string, string> NativeIds)
{
    /// <summary>True if saving to this format loses something the editor holds: the person must be told before a file is written.</summary>
    public bool IsLossy => Diagnostics.Any(d => d.Severity == "warning" || d.Severity == "error");
}

/// <summary>Where the exporter gets approved image bytes. It never opens a path named in the document.</summary>
public interface IAssetBlobSource
{
    Task<ReadOnlyMemory<byte>> ReadAsync(string sha256, CancellationToken ct);
    /// <summary>A PNG stand-in for an SVG asset (Visio pictures are raster), or null if none can be made.</summary>
    Task<ReadOnlyMemory<byte>?> RasteriseAsync(string sha256, CancellationToken ct) => Task.FromResult<ReadOnlyMemory<byte>?>(null);
}

public interface IVsdxImporter
{
    Task<Result<ImportResult>> ImportAsync(Stream package, ImportOptions options, CancellationToken ct);
}

public interface IVsdxExporter
{
    Task<Result<ExportResult>> ExportAsync(ExportSnapshot snapshot, IAssetBlobSource blobs, ExportOptions options, CancellationToken ct);
}
