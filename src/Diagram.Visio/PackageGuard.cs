using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.Visio;

/// <summary>
/// An untrusted .vsdx opened for reading. Part names were checked before this object existed, every read is counted against the
/// decompression budget as the bytes come out (declared sizes are not believed), and XML is parsed with DTDs and external
/// resolution off. Macro and embedded-object parts are listed but never read.
/// </summary>
public sealed class GuardedPackage : IDisposable
{
    readonly ZipArchive zip;
    readonly Dictionary<string, ZipArchiveEntry> entries;
    readonly ImportOptions options;
    long expanded;

    /// <summary>How many parts have been decompressed. Zero for a package that was refused.</summary>
    public int PartsRead { get; private set; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    /// <summary>SHA-256 of the exact file bytes: the seed for identity repair, so the same file always repairs the same way.</summary>
    public string Sha256 { get; }
    public IReadOnlyCollection<string> PartNames => entries.Keys;

    internal GuardedPackage(ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries, ImportOptions options, List<Diagnostic> diagnostics, string sha256)
    {
        Sha256 = sha256;
        this.zip = zip; this.entries = entries; this.options = options; Diagnostics = diagnostics;
    }

    public bool Has(string part) => entries.ContainsKey(Normalise(part));

    /// <summary>Reads one part, refusing it as soon as the bytes read exceed what is left of the budget.</summary>
    public Result<byte[]> Read(string part)
    {
        if (!entries.TryGetValue(Normalise(part), out var entry)) return Result<byte[]>.Fail("invalid_request", $"the package has no part {part}");
        if (IsInert(entry.FullName)) return Result<byte[]>.Fail("path_not_permitted", $"{part} is a macro or embedded object and is never read");
        var remaining = options.MaxExpandedBytes - expanded;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > remaining) return Result<byte[]>.Fail("limit_exceeded", $"the package expands to more than {options.MaxExpandedBytes} bytes", details: new System.Text.Json.Nodes.JsonObject { ["part"] = part });
            ms.Write(buffer, 0, n);
        }
        // a part that inflates by orders of magnitude is a bomb even if it fits the budget
        if (ms.Length > 4 * 1024 * 1024 && entry.CompressedLength > 0 && ms.Length / entry.CompressedLength > 1000)
            return Result<byte[]>.Fail("limit_exceeded", $"{part} is compressed more than 1000 to 1");
        expanded += ms.Length;
        PartsRead++;
        return Result<byte[]>.Ok(ms.ToArray());
    }

    /// <summary>Parses a part as XML with no DTD, no entity expansion and no external resolution.</summary>
    public Result<XDocument> ReadXml(string part)
    {
        var bytes = Read(part);
        if (!bytes.IsOk) return Result<XDocument>.Fail(bytes.Error!);
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes.Value), SafeXml);
            return Result<XDocument>.Ok(XDocument.Load(reader, LoadOptions.None));
        }
        catch (XmlException e)
        {
            return Result<XDocument>.Fail("invalid_request", $"{part} is not safe, valid XML: {e.Message}");
        }
    }

    public static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = 64L * 1024 * 1024, IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    internal static string Normalise(string part) => part.TrimStart('/');

    internal static bool IsInert(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith("vbaproject.bin") || n.Contains("/vba/") || n.StartsWith("visio/embeddings/") || n.StartsWith("visio/macros/") || n.EndsWith(".bin") && n.Contains("ole");
    }

    public void Dispose() => zip.Dispose();
}

public static class PackageGuard
{
    /// <summary>
    /// Opens a package after checking its shape: size, entry count, entry names (no traversal, no absolute or drive paths, no duplicates,
    /// no control characters) and declared totals. Nothing is decompressed here.
    /// </summary>
    public static Result<GuardedPackage> Open(Stream stream, ImportOptions options)
    {
        var buffered = ReadBounded(stream, options.MaxPackageBytes);
        if (!buffered.IsOk) return Result<GuardedPackage>.Fail(buffered.Error!);
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(buffered.Value), ZipArchiveMode.Read, leaveOpen: false); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return Result<GuardedPackage>.Fail("invalid_request", "the file is not a Visio package (not a valid zip archive)"); }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<Diagnostic>();
        long declared = 0;
        if (zip.Entries.Count > options.MaxEntries) { zip.Dispose(); return Result<GuardedPackage>.Fail("limit_exceeded", $"the package has more than {options.MaxEntries} parts"); }
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue; // directory marker
            var bad = BadName(e.FullName);
            if (bad is not null) { zip.Dispose(); return Result<GuardedPackage>.Fail("invalid_request", $"unsafe part name: {bad}", details: new System.Text.Json.Nodes.JsonObject { ["part"] = e.FullName }); }
            if (!entries.TryAdd(e.FullName, e)) { zip.Dispose(); return Result<GuardedPackage>.Fail("invalid_request", $"the package lists {e.FullName} twice"); }
            declared += e.Length;
            if (declared > options.MaxExpandedBytes) { zip.Dispose(); return Result<GuardedPackage>.Fail("limit_exceeded", $"the package declares more than {options.MaxExpandedBytes} bytes of content"); }
            if (GuardedPackage.IsInert(e.FullName))
                diagnostics.Add(new Diagnostic { Severity = "info", Code = "inert_part_ignored", Message = $"{e.FullName} (macro or embedded object) was not opened and is not kept.", Detail = e.FullName });
        }
        if (!entries.ContainsKey("[Content_Types].xml")) { zip.Dispose(); return Result<GuardedPackage>.Fail("invalid_request", "the package has no [Content_Types].xml"); }
        return Result<GuardedPackage>.Ok(new GuardedPackage(zip, entries, options, diagnostics, BlobStore.HashOf(buffered.Value)));
    }

    static Result<byte[]> ReadBounded(Stream stream, long limit)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > limit) return Result<byte[]>.Fail("limit_exceeded", $"the file is larger than {limit} bytes");
            ms.Write(buffer, 0, n);
        }
        return Result<byte[]>.Ok(ms.ToArray());
    }

    /// <summary>Why a part name cannot be trusted, or null.</summary>
    internal static string? BadName(string name)
    {
        if (name.Length == 0 || name.Length > 512) return "empty or very long name";
        if (name.Any(c => c < 0x20 || c == '\\' || c == ':')) return "control character, backslash or drive colon";
        if (name.StartsWith('/')) return "absolute path";
        foreach (var seg in name.Split('/'))
            if (seg is ".." or "." ) return "relative segment";
        return null;
    }

    /// <summary>
    /// Resolves a relationship target against the part that owns it. External targets are never followed (null). A target that climbs out of the
    /// package root is an error, not something to normalise away.
    /// </summary>
    public static Result<string?> ResolveTarget(string ownerPart, string target, bool external)
    {
        if (external) return Result<string?>.Ok(null);
        if (target.Contains(':') || target.StartsWith("\\", StringComparison.Ordinal) || target.Contains("//")) return Result<string?>.Fail("invalid_request", $"relationship target {target} is not a package path");
        var parts = new List<string>();
        if (!target.StartsWith('/')) parts.AddRange(ownerPart.TrimStart('/').Split('/').SkipLast(1));
        foreach (var seg in target.TrimStart('/').Split('/'))
        {
            if (seg == "." || seg.Length == 0) continue;
            if (seg == "..") { if (parts.Count == 0) return Result<string?>.Fail("invalid_request", $"relationship target {target} leaves the package"); parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return Result<string?>.Ok(string.Join('/', parts));
    }
}
