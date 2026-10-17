using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

/// <summary>A validated upload: the schema-owned reference plus what sanitising changed, so the user can be told.</summary>
public sealed record PreparedUpload(PreparedAssetRef Ref, IReadOnlyList<string> Notes, int Width, int Height);

/// <summary>Reads raster dimensions from the header alone. Pixel data is never decoded here.</summary>
public static class RasterHeader
{
    public static (int Width, int Height)? Read(string mime, ReadOnlySpan<byte> b) => mime switch
    {
        "image/png" => Png(b),
        "image/jpeg" => Jpeg(b),
        "image/bmp" => Bmp(b),
        _ => null,
    };

    static (int, int)? Png(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.Length < 24 || !b[..8].SequenceEqual(sig) || !b.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        return (BinaryPrimitives.ReadInt32BigEndian(b[16..]), BinaryPrimitives.ReadInt32BigEndian(b[20..]));
    }

    static (int, int)? Jpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            var len = (b[i + 2] << 8) | b[i + 3];
            var isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            if (marker == 0xDA) return null; // start of scan without a frame header
            i += 2 + len;
        }
        return null;
    }

    static (int, int)? Bmp(ReadOnlySpan<byte> b)
    {
        if (b.Length < 26 || b[0] != 'B' || b[1] != 'M') return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(b[14..]);
        if (headerSize < 40) return null; // OS/2 core headers are not supported
        if (b.Length < 26) return null;
        var w = BinaryPrimitives.ReadInt32LittleEndian(b[18..]);
        var h = BinaryPrimitives.ReadInt32LittleEndian(b[22..]);
        return (w, h == int.MinValue ? 0 : Math.Abs(h)); // negative height means top-down rows
    }
}

/// <summary>
/// Validates untrusted image bytes (type, size, header dimensions, SVG allowlist), stores the approved bytes durably by hash, and
/// issues an opaque, expiring preparation capability. Nothing here opens a path named by the input.
/// </summary>
public sealed class AssetPreparer
{
    static readonly HashSet<string> Mimes = ["image/png", "image/jpeg", "image/bmp", "image/svg+xml"];
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    readonly BlobStore blobs;
    readonly Func<DateTimeOffset> now;
    readonly ConcurrentDictionary<string, (string Sha, DateTimeOffset Expires)> preparations = new();

    public AssetPreparer(BlobStore blobs, Func<DateTimeOffset>? now = null)
    {
        this.blobs = blobs;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<Result<PreparedUpload>> PrepareAsync(Stream stream, string mime, string? name, CancellationToken ct)
    {
        var bytes = await ReadBounded(stream, mime, ct);
        if (!bytes.IsOk) return Result<PreparedUpload>.Fail(bytes.Error!);
        var approved = Approve(bytes.Value, mime);
        if (!approved.IsOk) return Result<PreparedUpload>.Fail(approved.Error!);

        var (final, notes) = approved.Value;
        var sha = await blobs.PutDurableAsync(final, ct);
        var preparationId = "prep-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expires = now() + Lifetime;
        preparations[preparationId] = (sha, expires);
        var display = DisplayName(name);
        var asset = new Asset
        {
            Id = $"asset:{Slug(display)}~{sha}", Name = display, MimeType = mime, Sha256 = sha, Tags = [], Provenance = "user-import",
        };
        var (w, h) = Dimensions(mime, final);
        return Result<PreparedUpload>.Ok(new PreparedUpload(new PreparedAssetRef { PreparationId = preparationId, Asset = asset, ExpiresAt = expires.ToString("O") }, notes, w, h));
    }

    /// <summary>Validates and, for SVG, sanitises bytes that are already in memory (used when opening a file). Does not store them.</summary>
    public Result<(byte[] Bytes, IReadOnlyList<string> Notes)> Approve(byte[] bytes, string mime) => ApproveBytes(bytes, mime);

    /// <summary>The same checks without needing a preparer: used by importers that hold the bytes and keep them elsewhere.</summary>
    public static Result<(byte[] Bytes, IReadOnlyList<string> Notes)> ApproveBytes(byte[] bytes, string mime)
    {
        if (!Mimes.Contains(mime)) return Result<(byte[], IReadOnlyList<string>)>.Fail("invalid_request", $"{mime} is not a supported image type");
        if (mime == "image/svg+xml")
        {
            var s = SvgSanitiser.Sanitise(bytes);
            return s.IsOk ? Result<(byte[], IReadOnlyList<string>)>.Ok((s.Value.Bytes, s.Value.Notes)) : Result<(byte[], IReadOnlyList<string>)>.Fail(s.Error!);
        }
        if (bytes.Length > InputLimits.RasterBytes) return Result<(byte[], IReadOnlyList<string>)>.Fail("limit_exceeded", $"image is larger than {InputLimits.RasterBytes} bytes");
        var dims = RasterHeader.Read(mime, bytes);
        if (dims is null) return Result<(byte[], IReadOnlyList<string>)>.Fail("invalid_request", $"the content is not a valid {mime} image");
        var (w, h) = dims.Value;
        if (w <= 0 || h <= 0) return Result<(byte[], IReadOnlyList<string>)>.Fail("invalid_request", "the image has no pixels");
        if (w > InputLimits.RasterSide || h > InputLimits.RasterSide || (long)w * h > InputLimits.RasterPixels)
            return Result<(byte[], IReadOnlyList<string>)>.Fail("limit_exceeded", $"image is {w} x {h}; the limits are {InputLimits.RasterSide} px per side and {InputLimits.RasterPixels} pixels");
        return Result<(byte[], IReadOnlyList<string>)>.Ok((bytes, Array.Empty<string>()));
    }

    /// <summary>The blob hash for a live preparation id, or null if it is unknown or expired. Callers never see a path.</summary>
    public string? Resolve(string preparationId) =>
        preparations.TryGetValue(preparationId, out var p) && p.Expires > now() ? p.Sha : null;

    /// <summary>Natural size in CSS pixels: header dimensions for rasters, width/height or viewBox for SVG, else 100 x 100.</summary>
    static (int Width, int Height) Dimensions(string mime, byte[] bytes)
    {
        if (mime != "image/svg+xml") return RasterHeader.Read(mime, bytes) is { } d ? (d.Width, d.Height) : (100, 100);
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        static double? Num(string text, string attr)
        {
            var m = Regex.Match(text, "\\b" + attr + "=\"([0-9.]+)(px)?\"");
            return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        var w = Num(head, "width");
        var h = Num(head, "height");
        if (w is null || h is null)
        {
            var vb = Regex.Match(head, "viewBox=\"\\s*[-0-9.]+[ ,]+[-0-9.]+[ ,]+([0-9.]+)[ ,]+([0-9.]+)");
            if (vb.Success)
            {
                w ??= double.Parse(vb.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                h ??= double.Parse(vb.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return ((int)Math.Clamp(w ?? 100, 1, 16384), (int)Math.Clamp(h ?? 100, 1, 16384));
    }

    static async Task<Result<byte[]>> ReadBounded(Stream stream, string mime, CancellationToken ct)
    {
        var limit = mime == "image/svg+xml" ? InputLimits.SvgBytes : InputLimits.RasterBytes;
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > limit) return Result<byte[]>.Fail("limit_exceeded", $"the upload is larger than {limit} bytes");
        }
        return Result<byte[]>.Ok(ms.ToArray());
    }

    static string DisplayName(string? name)
    {
        var file = string.IsNullOrWhiteSpace(name) ? "image" : name.Replace('\\', '/').Split('/').Last();
        return file.Length > 128 ? file[..128] : file;
    }

    static string Slug(string display)
    {
        var stem = Path.GetFileNameWithoutExtension(display);
        var slug = Regex.Replace(stem.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "image" : slug;
    }
}
