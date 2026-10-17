using System.IO.Compression;
using System.Xml.Linq;

namespace VsdxProbe;

/// <summary>
/// Post-processes a saved .vsdx so connector glue is real Visio glue: Connect FromPart/ToPart,
/// PAR(PNT(...)) formulas on BeginX/Y and EndX/Y, and a 1-D transform with ObjType 2.
/// Needed because OfficeIMO.Visio 3.4.4 writes Connect elements Visio discards on save.
/// </summary>
public static class GluePatch
{
    static readonly XNamespace V = "http://schemas.microsoft.com/office/visio/2012/main";

    public static void Apply(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        FixPageUnits(zip);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("visio/pages/page") && e.FullName.EndsWith(".xml")).ToList())
        {
            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);
            var c1 = PatchConnectors(doc);
            var c2 = PatchGroups(doc);
            if (!c1 && !c2) continue;
            var name = entry.FullName;
            entry.Delete();
            var ne = zip.CreateEntry(name);
            using var o = ne.Open();
            doc.Save(o);
        }
    }

    /// <summary>
    /// OfficeIMO writes mm magnitudes into Visio's inch-valued cells (PageWidth V=297 U=MM), and Visio then
    /// resets the page to A4 portrait. Visio stores inches in V and uses U only as the display unit, so
    /// convert size and margin cells to inches and keep U=MM for a metric UI.
    /// </summary>
    static void FixPageUnits(ZipArchive zip)
    {
        const string name = "visio/pages/pages.xml";
        var entry = zip.GetEntry(name)!;
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);
        var changed = false;
        foreach (var c in doc.Descendants(V + "Cell").Where(c => (string?)c.Attribute("U") == "MM"
            && (string?)c.Attribute("N") is "PageWidth" or "PageHeight" or "PageLeftMargin" or "PageRightMargin" or "PageTopMargin" or "PageBottomMargin"))
        {
            var mm = double.Parse((string)c.Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture);
            c.SetAttributeValue("V", (mm / 25.4).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            changed = true;
        }
        if (!changed) return;
        entry.Delete();
        using var o = zip.CreateEntry(name).Open();
        doc.Save(o);
    }

    static bool PatchConnectors(XDocument doc)
    {
        var connects = doc.Descendants(V + "Connect").ToList();
        if (connects.Count == 0) return false;
        var shapes = doc.Descendants(V + "Shape").ToDictionary(s => (string)s.Attribute("ID")!);
        foreach (var group in connects.GroupBy(c => (string)c.Attribute("FromSheet")!))
        {
            var conn = shapes[group.Key];
            var cells = new Dictionary<string, XElement>();
            foreach (var c in group)
            {
                var from = (string)c.Attribute("FromCell")!;           // BeginX / EndX
                var isBegin = from == "BeginX";
                var toSheet = (string)c.Attribute("ToSheet")!;
                var toCell = (string)c.Attribute("ToCell")!;          // Connections.X<n>
                var n = int.Parse(toCell[(toCell.LastIndexOf('X') + 1)..]);
                c.SetAttributeValue("FromPart", isBegin ? 9 : 12);
                c.SetAttributeValue("ToPart", 100 + (n - 1));
                var xy = $"PAR(PNT(Sheet.{toSheet}!Connections.X{n},Sheet.{toSheet}!Connections.Y{n}))";
                SetF(conn, isBegin ? "BeginX" : "EndX", xy);
                SetF(conn, isBegin ? "BeginY" : "EndY", xy);
            }
            double Get(string n) => double.Parse((string)conn.Elements(V + "Cell").First(e => (string)e.Attribute("N")! == n).Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture);
            double bx = Get("BeginX"), by = Get("BeginY"), ex = Get("EndX"), ey = Get("EndY");
            // 1-D transform exactly as Visio writes it for a dynamic connector.
            var first = conn.Elements(V + "Cell").First();
            foreach (var (n, v, f) in new (string, double, string?)[] {
                ("PinX", (bx + ex) / 2, "GUARD((BeginX+EndX)/2)"), ("PinY", (by + ey) / 2, "GUARD((BeginY+EndY)/2)"),
                ("Width", ex - bx, "GUARD(EndX-BeginX)"), ("Height", ey - by, "GUARD(EndY-BeginY)"),
                ("LocPinX", (ex - bx) / 2, "GUARD(Width*0.5)"), ("LocPinY", (ey - by) / 2, "GUARD(Height*0.5)"),
                ("Angle", 0, "GUARD(0)"), ("FlipX", 0, "GUARD(FALSE)"), ("FlipY", 0, "GUARD(FALSE)"),
                ("ObjType", 2, null), ("ShapeRouteStyle", 2, null) })
                first.AddBeforeSelf(Cell(n, v, f));
            // Local-coordinate geometry driven by formulas so Visio's reroute cannot leave stale vertices.
            var geo = conn.Elements(V + "Section").First(s => (string)s.Attribute("N")! == "Geometry");
            geo.Elements(V + "Row").ToList().ForEach(r => r.Remove());
            geo.Add(Row("MoveTo", 1, 0, null, 0, null), Row("LineTo", 2, ex - bx, "Width", ey - by, "Height"));
        }
        return true;
    }

    /// <summary>
    /// Child shapes of a group get formulas relative to the group (Sheet.N!Width*k), as Visio writes them,
    /// so resizing the group in Visio scales the children. OfficeIMO writes literal values.
    /// </summary>
    static bool PatchGroups(XDocument doc)
    {
        var changed = false;
        foreach (var g in doc.Descendants(V + "Shape").Where(x => (string?)x.Attribute("Type") == "Group").ToList())
        {
            var gid = (string)g.Attribute("ID")!;
            double gw = Num(g, "Width"), gh = Num(g, "Height");
            foreach (var ch in g.Element(V + "Shapes")?.Elements(V + "Shape") ?? Enumerable.Empty<XElement>())
            {
                double px = Num(ch, "PinX"), py = Num(ch, "PinY"), w = Num(ch, "Width"), h = Num(ch, "Height");
                SetF(ch, "PinX", $"Sheet.{gid}!Width*{R(px / gw)}");
                SetF(ch, "PinY", $"Sheet.{gid}!Height*{R(py / gh)}");
                SetF(ch, "Width", $"Sheet.{gid}!Width*{R(w / gw)}");
                SetF(ch, "Height", $"Sheet.{gid}!Height*{R(h / gh)}");
                SetF(ch, "LocPinX", "Width*0.5");
                SetF(ch, "LocPinY", "Height*0.5");
                foreach (var row in ch.Descendants(V + "Row").Where(r => (string?)r.Attribute("T") is "LineTo" or "MoveTo"))
                {
                    var x = row.Elements(V + "Cell").First(c => (string?)c.Attribute("N") == "X");
                    var y = row.Elements(V + "Cell").First(c => (string?)c.Attribute("N") == "Y");
                    x.SetAttributeValue("F", Math.Abs(double.Parse((string)x.Attribute("V")!, CI)) < 1e-9 ? "Width*0" : "Width*1");
                    y.SetAttributeValue("F", Math.Abs(double.Parse((string)y.Attribute("V")!, CI)) < 1e-9 ? "Height*0" : "Height*1");
                }
                changed = true;
            }
        }
        return changed;
    }

    static string R(double d) => d.ToString("R", CI);
    static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

    static double Num(XElement shape, string cell) =>
        double.Parse((string)shape.Elements(V + "Cell").First(e => (string?)e.Attribute("N") == cell).Attribute("V")!, CI);

    static XElement Cell(string n, double v, string? f)
    {
        var e = new XElement(V + "Cell", new XAttribute("N", n), new XAttribute("V", v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        if (f != null) e.SetAttributeValue("F", f);
        return e;
    }

    static XElement Row(string t, int ix, double x, string? xf, double y, string? yf) =>
        new(V + "Row", new XAttribute("T", t), new XAttribute("IX", ix), Cell("X", x, xf), Cell("Y", y, yf));

    static void SetF(XElement shape, string cell, string f)
    {
        var e = shape.Elements(V + "Cell").First(c => (string)c.Attribute("N")! == cell);
        e.SetAttributeValue("F", f);
    }
}
