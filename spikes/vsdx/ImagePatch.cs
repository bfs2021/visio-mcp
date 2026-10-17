using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace VsdxProbe;

/// <summary>
/// Adds a PNG picture as a Visio Foreign/Bitmap shape by editing the package directly,
/// because OfficeIMO.Visio 3.4.4 has no picture API.
/// </summary>
public static class ImagePatch
{
    static readonly XNamespace V = "http://schemas.microsoft.com/office/visio/2012/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string AgentIdCell = "AgentId";
    public const string SourceUri = "file:///probe/checker.png";
    public const string MimeType = "image/png";
    /// <summary>Original SVG source kept as a string user cell: the picture is a raster fallback of this.</summary>
    public const string SvgSource = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\"><rect width=\"8\" height=\"8\" fill=\"#1e5ac8\"/></svg>";

    /// <summary>Deterministic 16x16 PNG (blue/white checker) so the hash is stable.</summary>
    public static byte[] MakePng()
    {
        const int n = 16;
        var raw = new byte[n * (1 + n * 3)];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                bool on = ((x / 4) + (y / 4)) % 2 == 0;
                int o = y * (1 + n * 3) + 1 + x * 3;
                raw[o] = (byte)(on ? 30 : 255);
                raw[o + 1] = (byte)(on ? 90 : 255);
                raw[o + 2] = (byte)(on ? 200 : 255);
            }
        }
        using var z = new MemoryStream();
        using (var d = new ZLibStream(z, CompressionLevel.NoCompression, true)) d.Write(raw);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, n);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), n);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(td, 0);
        data.CopyTo(td, 4);
        s.Write(td);
        var crc = System.IO.Hashing.Crc32.Hash(td);
        s.Write(new[] { crc[3], crc[2], crc[1], crc[0] });
    }

    public static string Sha256(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static void Add(string path, Guid agentId, double pinXIn, double pinYIn, double wIn, double hIn)
    {
        var png = MakePng();
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        XDocument page;
        using (var s = zip.GetEntry("visio/pages/page1.xml")!.Open()) page = XDocument.Load(s);
        var shapes = page.Descendants(V + "Shapes").First();
        int id = shapes.Elements(V + "Shape").Max(s => int.Parse((string)s.Attribute("ID")!)) + 1;

        var shape = new XElement(V + "Shape",
            new XAttribute("ID", id), new XAttribute("Name", "Picture"), new XAttribute("NameU", "Picture"), new XAttribute("Type", "Foreign"),
            C("PinX", pinXIn), C("PinY", pinYIn), C("Width", wIn), C("Height", hIn),
            C("LocPinX", wIn / 2, "Width*0.5"), C("LocPinY", hIn / 2, "Height*0.5"), C("Angle", 0),
            C("ImgOffsetX", 0), C("ImgOffsetY", 0), C("ImgWidth", wIn, "Width*1"), C("ImgHeight", hIn, "Height*1"),
            Geometry(),
            new XElement(V + "Section", new XAttribute("N", "User"),
                new XElement(V + "Row", new XAttribute("N", AgentIdCell),
                    new XElement(V + "Cell", new XAttribute("N", "Value"), new XAttribute("V", agentId.ToString()), new XAttribute("U", "STR"))),
                UserRow("ImageSource", SourceUri),
                UserRow("ImageMime", MimeType),
                UserRow("ImageOriginalSha256", Sha256(png)),
                new XElement(V + "Row", new XAttribute("N", "SvgSource"),
                    new XElement(V + "Cell", new XAttribute("N", "Value"), new XAttribute("V", SvgSource), new XAttribute("U", "STR")))),
            new XElement(V + "ForeignData", new XAttribute("ForeignType", "Bitmap"), new XAttribute("CompressionType", "PNG"),
                new XElement(V + "Rel", new XAttribute(R + "id", "rId1"))));
        shapes.Add(shape);
        zip.GetEntry("visio/pages/page1.xml")!.Delete();
        using (var o = zip.CreateEntry("visio/pages/page1.xml").Open()) page.Save(o);

        using (var m = zip.CreateEntry("visio/media/image1.png").Open()) m.Write(png);

        var rels = new XDocument(new XElement(Rels + "Relationships",
            new XElement(Rels + "Relationship", new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                new XAttribute("Target", "../media/image1.png"))));
        using (var o = zip.CreateEntry("visio/pages/_rels/page1.xml.rels").Open()) rels.Save(o);
    }

    /// <summary>Unit-square relative outline, as Visio writes for its own pictures.</summary>
    static XElement Geometry()
    {
        XElement Row(string t, int ix, int x, int y) => new(V + "Row", new XAttribute("T", t), new XAttribute("IX", ix), C("X", x), C("Y", y));
        return new XElement(V + "Section", new XAttribute("N", "Geometry"), new XAttribute("IX", 0),
            C("NoFill", 0), C("NoLine", 0), C("NoShow", 0), C("NoSnap", 0), C("NoQuickDrag", 0),
            Row("RelMoveTo", 1, 0, 0), Row("RelLineTo", 2, 1, 0), Row("RelLineTo", 3, 1, 1), Row("RelLineTo", 4, 0, 1), Row("RelLineTo", 5, 0, 0));
    }

    static XElement UserRow(string name, string value) =>
        new(V + "Row", new XAttribute("N", name),
            new XElement(V + "Cell", new XAttribute("N", "Value"), new XAttribute("V", value), new XAttribute("U", "STR")));

    static XElement C(string n, double v, string? f = null)
    {
        var e = new XElement(V + "Cell", new XAttribute("N", n), new XAttribute("V", v.ToString("R", CultureInfo.InvariantCulture)));
        if (f != null) e.SetAttributeValue("F", f);
        return e;
    }

    /// <summary>Reads picture count, hash and AgentId from the package, independent of OfficeIMO.</summary>
    public static (int Count, string? Hash, string? AgentId) Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument page;
        using (var s = zip.GetEntry("visio/pages/page1.xml")!.Open()) page = XDocument.Load(s);
        var fs = page.Descendants(V + "Shape")
            .Where(s => (string?)s.Attribute("Type") == "Foreign" && s.Element(V + "ForeignData")?.Element(V + "Rel") != null).ToList();
        if (fs.Count == 0) return (0, null, null);
        var rid = (string)fs[0].Element(V + "ForeignData")!.Element(V + "Rel")!.Attribute(R + "id")!;
        XDocument relDoc;
        using (var s = zip.GetEntry("visio/pages/_rels/page1.xml.rels")!.Open()) relDoc = XDocument.Load(s);
        var target = relDoc.Descendants(Rels + "Relationship").First(r => (string)r.Attribute("Id")! == rid).Attribute("Target")!.Value;
        var full = target.StartsWith("/") ? target.TrimStart('/') : "visio/" + target.Replace("../", "");
        using var ms = new MemoryStream();
        using (var s = zip.GetEntry(full)!.Open()) s.CopyTo(ms);
        var agent = fs[0].Descendants(V + "Row").FirstOrDefault(r => (string?)r.Attribute("N") == AgentIdCell)?.Element(V + "Cell")?.Attribute("V")?.Value;
        return (fs.Count, Sha256(ms.ToArray()), agent);
    }
}
