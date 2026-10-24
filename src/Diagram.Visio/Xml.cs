using System.Globalization;
using System.Xml.Linq;
using Diagram.Core.Contracts;

namespace Diagram.Visio;

/// <summary>Shared constants and small helpers for the Visio XML dialect (verified against files Visio itself writes).</summary>
static class Vx
{
    public static readonly XNamespace V = "http://schemas.microsoft.com/office/visio/2012/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    public static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const double PtPerInch = 72.0;
    public const string AgentPrefix = "Agent";

    public static string N(double v)
    {
        if (v == 0) v = 0; // no negative zero
        return v.ToString("R", Inv);
    }

    public static double D(string? s, double fallback = 0) => s is not null && double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : fallback;

    public static XElement Cell(string n, double v, string? f = null, string? u = null)
    {
        var e = new XElement(V + "Cell", new XAttribute("N", n), new XAttribute("V", N(v)));
        if (f is not null) e.SetAttributeValue("F", f);
        if (u is not null) e.SetAttributeValue("U", u);
        return e;
    }

    public static XElement CellS(string n, string v) => new(V + "Cell", new XAttribute("N", n), new XAttribute("V", v));

    /// <summary>A user cell row holding a string, written as Visio expects (U="STR"): without it a UUID is read as a number and truncated.</summary>
    public static XElement UserRow(string name, string value) =>
        new(V + "Row", new XAttribute("N", name), new XElement(V + "Cell", new XAttribute("N", "Value"), new XAttribute("V", value), new XAttribute("U", "STR")));

    /// <summary>Canonical colours are lower-case #rrggbb; Visio writes them the same way.</summary>
    public static string Colour(string c) => c.ToLowerInvariant();

    public static (string? Id, string? Alias, string? Name, IReadOnlyList<string> LayerIds, int Z, int? Level, bool Locked, bool Hidden, Bounds B, double Rot, IReadOnlyDictionary<string, string> Meta) Common(Element e) => e switch
    {
        ShapeElement s => (s.Id, s.Alias, s.Name, s.LayerIds, s.ZIndex, s.Level, s.Locked, s.Hidden, s.Bounds, s.RotationDeg, s.Metadata),
        TextElement t => (t.Id, t.Alias, t.Name, t.LayerIds, t.ZIndex, t.Level, t.Locked, t.Hidden, t.Bounds, t.RotationDeg, t.Metadata),
        ImageElement i => (i.Id, i.Alias, i.Name, i.LayerIds, i.ZIndex, i.Level, i.Locked, i.Hidden, i.Bounds, i.RotationDeg, i.Metadata),
        ConnectorElement c => (c.Id, c.Alias, c.Name, c.LayerIds, c.ZIndex, c.Level, c.Locked, c.Hidden, c.Bounds, c.RotationDeg, c.Metadata),
        GroupElement g => (g.Id, g.Alias, g.Name, g.LayerIds, g.ZIndex, g.Level, g.Locked, g.Hidden, g.Bounds, g.RotationDeg, g.Metadata),
        _ => throw new InvalidOperationException("unknown element kind"),
    };

    public static string Kind(Element e) => e switch { ShapeElement => "shape", TextElement => "text", ImageElement => "image", ConnectorElement => "connector", _ => "group" };

    /// <summary>Visio ShapeSheet line patterns: 0 none, 1 solid, 2 dashed, 3 dotted, 4 dash-dot.</summary>
    public static int LinePattern(string dash) => dash switch { "dashed" => 2, "dotted" => 3, "dash-dot" => 4, _ => 1 };
    public static string DashOf(int pattern) => pattern switch { 2 => "dashed", 3 => "dotted", 4 => "dash-dot", _ => "solid" };

    /// <summary>Visio arrow indexes, found by drawing each one in Visio: 0 none, 13 filled triangle, 1 open, 22 open diamond, 20 open circle.</summary>
    public static int ArrowCode(string arrow) => arrow switch { "block" => 13, "open" => 1, "diamond" => 22, "oval" => 20, _ => 0 };
    /// <summary>Visio arrow size indexes: 1 small, 2 medium (the default), 4 large.</summary>
    public static int ArrowSizeCode(string? size) => size switch { "small" => 1, "large" => 4, _ => 2 };
    public static string ArrowSizeOf(int code) => code <= 1 ? "small" : code >= 3 ? "large" : "medium";
    public static string? ArrowOf(int code) => code switch
    {
        0 => "none",
        2 or 4 or 5 or 6 or 13 or 14 or 15 or 16 or 17 or 18 => "block",
        1 or 3 or 7 or 12 or 19 => "open",
        22 => "diamond",
        20 or 10 => "oval",
        _ => null,
    };
}
