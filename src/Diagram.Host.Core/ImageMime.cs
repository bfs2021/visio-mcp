namespace Diagram.Host.Core;

/// <summary>Content type for host-served image bytes, from the bytes themselves. Blobs were validated on the way in.</summary>
public static class ImageMime
{
    public static string Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return "image/bmp";
        var head = System.Text.Encoding.UTF8.GetString(b[..Math.Min(b.Length, 256)]);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "application/octet-stream";
    }
}
