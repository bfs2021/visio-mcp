using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Visio;

/// <summary>Builds canonical documents from plain JSON-shaped objects so fixtures stay readable.</summary>
static class Doc
{
    public const string DocId = "00000000-0000-4000-8000-0000000000d1";
    public const string PageId = "00000000-0000-4000-8000-0000000000a1";
    public const string LayerA = "00000000-0000-4000-8000-0000000000b1";
    public const string LayerB = "00000000-0000-4000-8000-0000000000b2";
    public static string Id(int n) => $"00000000-0000-4000-8000-{n:D12}";

    public static readonly object Fill = new { kind = "solid", colour = "#87cefa", opacity = 1 };
    public static readonly object Stroke = new { kind = "solid", colour = "#333333", opacity = 1 };
    public static readonly object None = new { kind = "none" };

    public static object Text(string value, object? over = null)
    {
        var o = JsonSerializer.SerializeToNode(new { value, fontFamily = "Segoe UI", fontSizePt = 12, bold = false, italic = false, underline = false, colour = "#000000", horizontalAlign = "center", verticalAlign = "middle", wrap = true, paddingPt = 4 })!.AsObject();
        if (over is not null) foreach (var kv in JsonSerializer.SerializeToNode(over)!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    public static object Shape(int n, double x, double y, double w, double h, object? over = null, string? preset = "rectangle", int z = 0, object? text = null)
    {
        var o = JsonSerializer.SerializeToNode(new
        {
            id = Id(n), kind = "shape", layerIds = Array.Empty<string>(), zIndex = z == 0 ? n : z, locked = false, hidden = false, bounds = new { x, y, width = w, height = h }, rotationDeg = 0, metadata = new { },
            geometry = new { preset }, style = new { fill = Fill, stroke = Stroke, strokeWidthPt = 1.5, dash = "solid", lineCap = "butt", lineJoin = "miter" },
        })!.AsObject();
        if (text is not null) o["text"] = JsonSerializer.SerializeToNode(text);
        if (over is not null) foreach (var kv in JsonSerializer.SerializeToNode(over)!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    public static object Connector(int n, object from, object to, string route = "straight", object? over = null, int z = 50)
    {
        var o = JsonSerializer.SerializeToNode(new
        {
            id = Id(n), kind = "connector", layerIds = Array.Empty<string>(), zIndex = z, locked = false, hidden = false, bounds = new { x = 0, y = 0, width = 1, height = 1 }, rotationDeg = 0, metadata = new { },
            from, to, route, waypoints = Array.Empty<object>(), style = new { colour = "#334155", widthPt = 1.5, dash = "solid", startArrow = "none", endArrow = "block" },
        })!.AsObject();
        if (over is not null) foreach (var kv in JsonSerializer.SerializeToNode(over)!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    public static object Glue(int elementN, string glue = "dynamic", string? port = null) => port is null ? new { elementId = Id(elementN), glue } : (object)new { elementId = Id(elementN), glue, port };
    public static object Free(double x, double y) => new { glue = "none", point = new { x, y } };

    public static JsonObject Page(IEnumerable<object> elements, object? over = null, IEnumerable<object>? layers = null, IEnumerable<object>? guides = null)
    {
        var o = JsonSerializer.SerializeToNode(new
        {
            id = PageId, name = "Page 1", widthPt = 841.8897637795276, heightPt = 595.2755905511812, background = new { kind = "solid", colour = "#ffffff", opacity = 1 },
            grid = new { visible = true, spacingPt = 14.173228346456693, snap = true }, guides = guides?.ToArray() ?? Array.Empty<object>(),
            layers = layers?.ToArray() ?? Array.Empty<object>(), elements = elements.ToArray(),
        })!.AsObject();
        if (over is not null) foreach (var kv in JsonSerializer.SerializeToNode(over)!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    public static object Layer(string id, string name, bool visible = true, bool locked = false, bool printable = true, string? colour = null) =>
        colour is null ? new { id, name, visible, locked, printable, snap = true, glue = true } : (object)new { id, name, visible, locked, printable, snap = true, glue = true, colourOverride = colour };

    public static DiagramDocument Make(JsonObject[] pages, IEnumerable<object>? assets = null, string title = "Test diagram")
    {
        var d = JsonSerializer.SerializeToNode(new { schemaVersion = 1, id = DocId, title, revision = 3, pages = pages.ToArray<object>(), assets = assets?.ToArray() ?? Array.Empty<object>(), metadata = new { author = "tests" } })!;
        return d.Deserialize<DiagramDocument>(ContractJson.Options)!;
    }

    public static ExportSnapshot Snapshot(DiagramDocument doc, Dictionary<string, JsonObject>? routes = null)
    {
        var projection = new { documentId = doc.Id, sessionId = Id(777), revision = doc.Revision, connectors = routes ?? new Dictionary<string, JsonObject>(), groupVisualBounds = new { } };
        var snap = new { documentId = doc.Id, sessionId = Id(777), revision = doc.Revision, document = JsonSerializer.SerializeToNode(doc, ContractJson.Options), projection };
        return JsonSerializer.SerializeToNode(snap)!.Deserialize<ExportSnapshot>(ContractJson.Options)!;
    }

    public static ExportSnapshot Snapshot(params JsonObject[] pages) => Snapshot(Make(pages));

    public static JsonObject Route(params (double X, double Y)[] pts)
    {
        var xs = pts.Select(p => p.X).ToArray(); var ys = pts.Select(p => p.Y).ToArray();
        return new JsonObject
        {
            ["routePoints"] = new JsonArray(pts.Select(p => (JsonNode)new JsonObject { ["x"] = p.X, ["y"] = p.Y }).ToArray()),
            ["visualBounds"] = new JsonObject { ["x"] = xs.Min(), ["y"] = ys.Min(), ["width"] = xs.Max() - xs.Min(), ["height"] = ys.Max() - ys.Min() },
        };
    }
}

sealed class Blobs : IAssetBlobSource
{
    public readonly Dictionary<string, byte[]> Bytes = new();
    public readonly Dictionary<string, byte[]> Rasters = new();
    public string Add(byte[] b) { var sha = BlobStore.HashOf(b); Bytes[sha] = b; return sha; }
    public Task<ReadOnlyMemory<byte>> ReadAsync(string sha, CancellationToken ct) => Bytes.TryGetValue(sha, out var b) ? Task.FromResult<ReadOnlyMemory<byte>>(b) : throw new FileNotFoundException(sha);
    public Task<ReadOnlyMemory<byte>?> RasteriseAsync(string sha, CancellationToken ct) => Task.FromResult(Rasters.TryGetValue(sha, out var b) ? (ReadOnlyMemory<byte>?)b : null);
}

/// <summary>Deterministic small images and zip helpers.</summary>
static class Fx
{
    public static byte[] Png(int w = 16, int h = 8, byte r = 30, byte g = 90, byte b = 200)
    {
        var raw = new byte[h * (1 + w * 3)];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var o = y * (1 + w * 3) + 1 + x * 3; var on = ((x / 4) + (y / 4)) % 2 == 0; raw[o] = on ? r : (byte)255; raw[o + 1] = on ? g : (byte)255; raw[o + 2] = on ? b : (byte)255; }
        using var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.NoCompression, true)) d.Write(raw);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h); ihdr[8] = 8; ihdr[9] = 2;
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length); png.Write(len);
            var td = new byte[4 + data.Length]; System.Text.Encoding.ASCII.GetBytes(type).CopyTo(td, 0); data.CopyTo(td, 4); png.Write(td);
            var crc = System.IO.Hashing.Crc32.Hash(td); png.Write(new[] { crc[3], crc[2], crc[1], crc[0] });
        }
        Chunk("IHDR", ihdr); Chunk("IDAT", z.ToArray()); Chunk("IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    public static byte[] Bmp(int w = 8, int h = 4)
    {
        var row = (w * 3 + 3) & ~3; var size = 54 + row * h; var b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M'; BitConverter.GetBytes(size).CopyTo(b, 2); BitConverter.GetBytes(54).CopyTo(b, 10); BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(w).CopyTo(b, 18); BitConverter.GetBytes(h).CopyTo(b, 22); b[26] = 1; b[28] = 24; BitConverter.GetBytes(row * h).CopyTo(b, 34);
        return b;
    }

    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) foreach (var (n, d) in entries) { var e = z.CreateEntry(n); using var s = e.Open(); s.Write(d); }
        return ms.ToArray();
    }

    /// <summary>A JPEG header with a frame of the given size: enough for the host's header checks, which never decode pixels.</summary>
    public static byte[] Jpeg(int w, int h) => [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xD9];

    /// <summary>The package with one part rewritten.</summary>
    public static byte[] Edit(byte[] package, string name, Action<XDocument> change)
    {
        using var input = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var ms = new MemoryStream();
        using (var output = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var e in input.Entries)
            {
                using var src = e.Open();
                var target = output.CreateEntry(e.FullName).Open();
                if (e.FullName == name) { var d = XDocument.Load(src); change(d); d.Save(target); } else src.CopyTo(target);
                target.Dispose();
            }
        }
        return ms.ToArray();
    }

    public static byte[] Replace(byte[] package, string name, byte[] data)
    {
        using var input = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var ms = new MemoryStream();
        using (var output = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var e in input.Entries) { using var src = e.Open(); using var target = output.CreateEntry(e.FullName).Open(); if (e.FullName == name) target.Write(data); else src.CopyTo(target); }
        return ms.ToArray();
    }

    public static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    public static XDocument Part(byte[] package, string name)
    {
        using var z = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var s = z.GetEntry(name)!.Open();
        return XDocument.Load(s);
    }

    public static IReadOnlyList<string> Names(byte[] package) { using var z = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read); return z.Entries.Select(e => e.FullName).ToList(); }
}
