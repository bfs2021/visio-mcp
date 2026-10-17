using OfficeIMO.Visio;

namespace VsdxProbe;

public sealed record ProbeManifest(
    double PageWidthPt, double PageHeightPt, int DrawableCount,
    string? RectangleAgentId, string? RectangleId, string? ConnectorFromShapeId, string? ConnectorToShapeId,
    string? RectangleText, double RotatedAngleDeg, string? ImageHash,
    string? DuplicateUserIdShapeA, string? DuplicateUserIdShapeB,
    IReadOnlyList<string> Unsupported);

public static class ProbeScene
{
    public const string AgentIdCell = "AgentId";
    public const double GroupW = 2.0, GroupH = 1.0;
    public static readonly Guid RectAgentId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid PictureAgentId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    public static readonly Guid EllipseAgentId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    const double Mm = 1.0; // OfficeIMO unit overloads take VisioMeasurementUnit.Millimeters

    public static ProbeManifest Write(string path)
    {
        var doc = VisioDocument.Create(path);
        var page = doc.AddPage("Probe", 297, 210, VisioMeasurementUnit.Millimeters);
        page.AutoResizeDrawing = false;
        page.AddLayer("Layer A", null);
        page.AddLayer("Layer B", null);

        var rect = page.AddRectangle(30, 120, 50, 25, "Probe", VisioMeasurementUnit.Millimeters);
        rect.SetUserCell(AgentIdCell, RectAgentId.ToString(), "STR", null, null);
        rect.FillColor = OfficeIMO.Drawing.OfficeColor.FromRgb(135, 206, 250);
        page.AddToLayer("Layer A", rect);

        var ellipse = page.AddEllipse(130, 120, 50, 25, "Ellipse", VisioMeasurementUnit.Millimeters);
        ellipse.SetUserCell(AgentIdCell, EllipseAgentId.ToString(), "STR", null, null);
        ellipse.Angle = 33.0 * Math.PI / 180.0;
        page.AddToLayer("Layer B", ellipse);

        var text = page.AddTextBox(30, 60, 80, 15, "Text box", VisioMeasurementUnit.Millimeters);
        text.SetUserCell(AgentIdCell, Guid.Parse("33333333-3333-4333-8333-333333333333").ToString(), "STR", null, null);

        page.AddConnector(rect, ellipse, ConnectorKind.Straight, VisioSide.Right, VisioSide.Left);

        var extras = doc.AddPage("Extras", 297, 210, VisioMeasurementUnit.Millimeters);
        extras.AutoResizeDrawing = false;
        extras.AddLayer("L1", null);
        extras.AddLayer("L2", null);
        // Group probe: 2.0 x 1.0 in group with two children, positions in group-local inches.
        var grp = new VisioShape("20", 3.0, 5.0, GroupW, GroupH, "") { NameU = "Group" };
        grp.Children.Add(new VisioShape("21", 0.5, 0.5, 0.6, 0.4, "a") { NameU = "ChildA" });
        grp.Children.Add(new VisioShape("22", 1.5, 0.5, 0.6, 0.4, "b") { NameU = "ChildB" });
        extras.Shapes.Add(grp);
        // Multi-layer probe: one shape in both layers.
        var multi = extras.AddRectangle(150, 100, 40, 20, "multi", VisioMeasurementUnit.Millimeters);
        multi.NameU = "MultiLayer";
        extras.AddToLayer("L1", multi);
        extras.AddToLayer("L2", multi);

        doc.Save();
        GluePatch.Apply(path);
        ImagePatch.Add(path, PictureAgentId, 7.5, 3.0, 1.2, 1.2);
        return Read(path);
    }

    public static ProbeManifest Read(string path)
    {
        var doc = VisioDocument.Load(path);
        var page = doc.Pages[0];
        var pic = ImagePatch.Inspect(path);
        var rect = page.Shapes.FirstOrDefault(s => s.Text == "Probe");
        var ell = page.Shapes.FirstOrDefault(s => s.Text == "Ellipse");
        var con = page.Connectors.FirstOrDefault();
        var unsupported = new List<string> { "PNG picture shape: OfficeIMO.Visio 3.4.4 exposes no picture/foreign-data API (image export only)" };
        return new ProbeManifest(
            PageInches(path, "PageWidth") * 72.0, PageInches(path, "PageHeight") * 72.0,
            CountShapes(path),
            rect?.GetUserCellValue(AgentIdCell), rect?.Id.ToString(),
            con?.From?.Id.ToString(), con?.To?.Id.ToString(),
            rect?.Text, (ell?.Angle ?? 0) * 180.0 / Math.PI, pic.Hash, null, null, unsupported);
    }

    /// <summary>Counts how many connection-point rows each shape has, keyed by shape name (read from the XML).</summary>
    public static Dictionary<string, int> ConnectionRows(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.Xml.Linq.XDocument page;
        using (var st = zip.GetEntry("visio/pages/page1.xml")!.Open()) page = System.Xml.Linq.XDocument.Load(st);
        System.Xml.Linq.XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        return page.Descendants(v + "Shape").ToDictionary(
            sh => (string?)sh.Attribute("NameU") ?? "#" + (string?)sh.Attribute("ID"),
            sh => sh.Descendants(v + "Row").Count(r => (string?)r.Attribute("T") == "Connection"));
    }

    /// <summary>Layer names per shape name on the given page (1-based). LayerMember is a ";"-separated list of 0-based positions.</summary>
    public static Dictionary<string, string[]> Layers(string path, int pageNo = 1)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.Xml.Linq.XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        System.Xml.Linq.XDocument page, pages;
        var entry = zip.GetEntry($"visio/pages/page{pageNo}.xml") ?? throw new InvalidOperationException($"file has no page {pageNo} (predates the extras page)");
        using (var st = entry.Open()) page = System.Xml.Linq.XDocument.Load(st);
        using (var st = zip.GetEntry("visio/pages/pages.xml")!.Open()) pages = System.Xml.Linq.XDocument.Load(st);
        var pageEl = pages.Descendants(v + "Page").ElementAt(pageNo - 1);
        var layerSection = pageEl.Descendants(v + "Section").First(x => (string?)x.Attribute("N") == "Layer");
        var names = layerSection.Elements(v + "Row")
            .Select(r => (string)r.Elements(v + "Cell").First(c => (string?)c.Attribute("N") == "Name").Attribute("V")!).ToList();
        var res = new Dictionary<string, string[]>();
        foreach (var sh in page.Descendants(v + "Shape"))
        {
            var lm = sh.Elements(v + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == "LayerMember")?.Attribute("V")?.Value;
            if (string.IsNullOrEmpty(lm)) continue;
            res[(string?)sh.Attribute("NameU") ?? "#" + (string?)sh.Attribute("ID")] =
                lm.Split(';').Select(i => names[int.Parse(i)]).ToArray();
        }
        return res;
    }

    /// <summary>Group size and each child's pin/size as fractions of the group, from the Extras page.</summary>
    public static (double GroupWidthIn, double GroupHeightIn, Dictionary<string, double[]> Ratios, bool HasRelativeFormulas) GroupGeometry(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.Xml.Linq.XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        var entry = zip.GetEntry("visio/pages/page2.xml") ?? throw new InvalidOperationException("file has no page 2 (predates the extras page)");
        System.Xml.Linq.XDocument page;
        using (var st = entry.Open()) page = System.Xml.Linq.XDocument.Load(st);
        var g = page.Descendants(v + "Shape").First(x => (string?)x.Attribute("Type") == "Group");
        double Val(System.Xml.Linq.XElement sh, string n) => double.Parse((string)sh.Elements(v + "Cell").First(c => (string?)c.Attribute("N") == n).Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture);
        double gw = Val(g, "Width"), gh = Val(g, "Height");
        var ratios = new Dictionary<string, double[]>();
        var rel = true;
        foreach (var ch in g.Element(v + "Shapes")!.Elements(v + "Shape"))
        {
            ratios[(string)ch.Attribute("NameU")!] = new[] { Val(ch, "PinX") / gw, Val(ch, "PinY") / gh, Val(ch, "Width") / gw, Val(ch, "Height") / gh };
            rel &= ((string?)ch.Elements(v + "Cell").First(c => (string?)c.Attribute("N") == "Width").Attribute("F") ?? "").Contains("!Width");
        }
        return (gw, gh, ratios, rel);
    }

    public enum Provenance { Match, Mismatch, Missing }

    /// <summary>Compares the picture's recorded original SHA-256 with the media part actually in the package.</summary>
    public static (Provenance Status, string? Source, string? Mime) CheckProvenance(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.Xml.Linq.XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        System.Xml.Linq.XDocument page;
        using (var st = zip.GetEntry("visio/pages/page1.xml")!.Open()) page = System.Xml.Linq.XDocument.Load(st);
        var pic = page.Descendants(v + "Shape").First(x => (string?)x.Attribute("Type") == "Foreign");
        string? Row(string n) => pic.Descendants(v + "Row").FirstOrDefault(r => (string?)r.Attribute("N") == n)?.Elements(v + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == "Value")?.Attribute("V")?.Value;
        var recorded = Row("ImageOriginalSha256");
        if (recorded is null) return (Provenance.Missing, Row("ImageSource"), Row("ImageMime"));
        var actual = ImagePatch.Inspect(path).Hash;
        return (recorded == actual ? Provenance.Match : Provenance.Mismatch, Row("ImageSource"), Row("ImageMime"));
    }

    /// <summary>AgentIds appearing on more than one shape, then a resolved mapping that gives later duplicates new IDs.</summary>
    public static (IReadOnlyList<string> Duplicates, IReadOnlyList<string> Resolved) ResolveDuplicateAgentIds(string path)
    {
        var doc = VisioDocument.Load(path);
        var ids = doc.Pages[0].Shapes.Select(s => s.GetUserCellValue(AgentIdCell)).Where(i => !string.IsNullOrEmpty(i)).Select(i => i!).ToList();
        var dups = ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var seen = new HashSet<string>();
        var resolved = ids.Select(i => seen.Add(i) ? i : Guid.NewGuid().ToString()).ToList();
        return (dups, resolved);
    }

    public static string WriteDuplicateIdScene(string path)
    {
        var doc = VisioDocument.Create(path);
        var page = doc.AddPage("Dup", 297, 210, VisioMeasurementUnit.Millimeters);
        var shared = Guid.Parse("55555555-5555-4555-8555-555555555555").ToString();
        foreach (var x in new[] { 20.0, 100.0 })
            page.AddRectangle(x, 100, 40, 20, "dup", VisioMeasurementUnit.Millimeters).SetUserCell(AgentIdCell, shared, "STR", null, null);
        doc.Save();
        return shared;
    }

    public static string? ReadSvgSource(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.Xml.Linq.XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        System.Xml.Linq.XDocument page;
        using (var st = zip.GetEntry("visio/pages/page1.xml")!.Open()) page = System.Xml.Linq.XDocument.Load(st);
        return page.Descendants(v + "Row").FirstOrDefault(r => (string?)r.Attribute("N") == "SvgSource")?.Elements(v + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == "Value")?.Attribute("V")?.Value;
    }

    /// <summary>Counts drawable shapes (including connectors and pictures) straight from the page XML.</summary>
    static int CountShapes(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.Xml.Linq.XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        System.Xml.Linq.XDocument page;
        using (var st = zip.GetEntry("visio/pages/page1.xml")!.Open()) page = System.Xml.Linq.XDocument.Load(st);
        return page.Descendants(v + "Shape").Count();
    }

    /// <summary>Page size from the XML: V is always inches in Visio; U is only the display unit.</summary>
    static double PageInches(string path, string cell)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.Xml.Linq.XNamespace v = "http://schemas.microsoft.com/office/visio/2012/main";
        System.Xml.Linq.XDocument pages;
        using (var st = zip.GetEntry("visio/pages/pages.xml")!.Open()) pages = System.Xml.Linq.XDocument.Load(st);
        var c = pages.Descendants(v + "Cell").First(x => (string?)x.Attribute("N") == cell);
        return double.Parse((string)c.Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture);
    }
}
