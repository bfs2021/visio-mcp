using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Diagram.Host.Core;

namespace Diagram.Visio;

/// <summary>A point as fractions of a shape's width and height, with the origin at the top left and y running down (the editor's orientation).</summary>
public readonly record struct Frac(double X, double Y);

public abstract record GeoRow;
public sealed record MoveRow(Frac At) : GeoRow;
public sealed record LineRow(Frac To) : GeoRow;
/// <summary>A full ellipse inscribed in a box given as fractions.</summary>
public sealed record EllipseRow(Frac Centre, double Rx, double Ry) : GeoRow;

/// <summary>One Visio geometry section: an outline, or an open stroke with no fill.</summary>
public sealed record GeoSection(IReadOnlyList<GeoRow> Rows, bool NoFill = false);

/// <summary>
/// Shape outlines in the editor's terms. The exporter writes them as Visio geometry (formulas in the shape's own width and height, so a
/// resize in Visio stretches them), and the importer reads Visio geometry back into a preset or a restricted path.
/// A custom path is an SVG path in a 100 x 100 box that is stretched to the shape's bounds.
/// </summary>
public static class GeometryMap
{
    public const int PathBox = 100;
    const int Samples = 24;

    static readonly Frac[] Unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    public static IReadOnlyList<GeoSection> ForPreset(string preset) => preset switch
    {
        "ellipse" => [new([new EllipseRow(new(0.5, 0.5), 0.5, 0.5)])],
        "diamond" => [Poly([new(0.5, 0), new(1, 0.5), new(0.5, 1), new(0, 0.5)])],
        "triangle" => [Poly([new(0.5, 0), new(1, 1), new(0, 1)])],
        "hexagon" => [Poly([new(0.25, 0), new(0.75, 0), new(1, 0.5), new(0.75, 1), new(0.25, 1), new(0, 0.5)])],
        "parallelogram" => [Poly([new(0.2, 0), new(1, 0), new(0.8, 1), new(0, 1)])],
        "callout" => [Poly([new(0, 0), new(1, 0), new(1, 0.8), new(0.45, 0.8), new(0.2, 1), new(0.25, 0.8), new(0, 0.8)])],
        "cylinder" => Cylinder(),
        "cloud" => [Poly(Cloud())],
        _ => [Poly(Unit)], // rectangle and rounded-rectangle (rounding is a separate cell)
    };

    /// <summary>Presets that are drawn as an approximation of the canvas shape, so a person can be told.</summary>
    public static bool IsApproximated(string preset) => preset is "cylinder" or "cloud";

    static GeoSection Poly(IReadOnlyList<Frac> pts)
    {
        var rows = new List<GeoRow> { new MoveRow(pts[0]) };
        rows.AddRange(pts.Skip(1).Select(p => (GeoRow)new LineRow(p)));
        rows.Add(new LineRow(pts[0]));
        return new GeoSection(rows);
    }

    static IReadOnlyList<GeoSection> Cylinder()
    {
        const double ry = 0.12;
        var outline = new List<GeoRow> { new MoveRow(new(0, ry)) };
        for (var i = 1; i <= Samples; i++) { var t = Math.PI * i / Samples; outline.Add(new LineRow(new(0.5 - 0.5 * Math.Cos(t), ry - ry * Math.Sin(t)))); } // back rim, left to right
        outline.Add(new LineRow(new(1, 1 - ry)));
        for (var i = 1; i <= Samples; i++) { var t = Math.PI * i / Samples; outline.Add(new LineRow(new(0.5 + 0.5 * Math.Cos(t), 1 - ry + ry * Math.Sin(t)))); } // bottom, right to left
        outline.Add(new LineRow(new(0, ry)));
        var front = new List<GeoRow> { new MoveRow(new(0, ry)) };
        for (var i = 1; i <= Samples; i++) { var t = Math.PI * i / Samples; front.Add(new LineRow(new(0.5 - 0.5 * Math.Cos(t), ry + ry * Math.Sin(t)))); } // front rim
        return [new GeoSection(outline), new GeoSection(front, NoFill: true)];
    }

    static IReadOnlyList<Frac> Cloud()
    {
        var pts = new List<Frac>();
        for (var i = 0; i < 64; i++)
        {
            var t = 2 * Math.PI * i / 64;
            var r = 0.9 + 0.1 * Math.Abs(Math.Sin(4 * t)); // eight scallops
            pts.Add(new Frac(0.5 + 0.5 * r * Math.Cos(t), 0.5 + 0.5 * r * Math.Sin(t)));
        }
        return pts;
    }

    // ------------------------------------------------------------------ restricted SVG path

    /// <summary>
    /// Reads a path of M, L, H, V, Z and C/Q curves (absolute or relative) in the 100 x 100 box. Curves are flattened to short lines.
    /// Anything else (arcs, smooth curves) is refused rather than guessed.
    /// </summary>
    public static Result<IReadOnlyList<GeoSection>> ForSvgPath(string path)
    {
        if (path.Length is 0 or > 65536) return Result<IReadOnlyList<GeoSection>>.Fail("invalid_request", "path length must be 1–65536 characters");
        var rows = new List<GeoRow>();
        double x = 0, y = 0, sx = 0, sy = 0;
        var i = 0;
        char cmd = ' ';
        var drawn = false;
        bool Num(out double v)
        {
            while (i < path.Length && (char.IsWhiteSpace(path[i]) || path[i] == ',')) i++;
            var start = i;
            if (i < path.Length && (path[i] == '-' || path[i] == '+')) i++;
            while (i < path.Length && char.IsAsciiDigit(path[i])) i++;
            if (i < path.Length && path[i] == '.') { i++; while (i < path.Length && char.IsAsciiDigit(path[i])) i++; }
            if (i < path.Length && (path[i] == 'e' || path[i] == 'E')) { i++; if (i < path.Length && (path[i] == '-' || path[i] == '+')) i++; while (i < path.Length && char.IsDigit(path[i])) i++; }
            return double.TryParse(path.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && i > start && double.IsFinite(v) && Math.Abs(v) <= 1e6;
        }
        Frac F(double px, double py) => new(px / PathBox, py / PathBox);
        void Curve(Func<double, (double, double)> at) { for (var k = 1; k <= 16; k++) { var (px, py) = at(k / 16.0); rows.Add(new LineRow(F(px, py))); } }

        while (true)
        {
            while (i < path.Length && (char.IsWhiteSpace(path[i]) || path[i] == ',')) i++;
            if (i >= path.Length) break;
            if (char.IsLetter(path[i])) { cmd = path[i++]; if (rows.Count == 0 && cmd is not ('M' or 'm')) return Bad(cmd); if (cmd is 'Z' or 'z') { if (rows.Count >= 32768) return Bad(cmd); if (rows.Count > 0) rows.Add(new LineRow(F(sx, sy))); x = sx; y = sy; cmd = ' '; continue; } }
            else if (cmd == ' ') return Result<IReadOnlyList<GeoSection>>.Fail("invalid_request", "an SVG path must start with a command");
            var rel = char.IsLower(cmd);
            if (char.ToUpperInvariant(cmd) != 'M') drawn = true;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                case 'L':
                {
                    if (!Num(out var a) || !Num(out var b)) return Bad(cmd);
                    if (rel) { a += x; b += y; }
                    if (char.ToUpperInvariant(cmd) == 'M' && rows.Count == 0) { rows.Add(new MoveRow(F(a, b))); sx = a; sy = b; }
                    else if (char.ToUpperInvariant(cmd) == 'M') { rows.Add(new MoveRow(F(a, b))); sx = a; sy = b; }
                    else rows.Add(new LineRow(F(a, b)));
                    x = a; y = b;
                    if (char.ToUpperInvariant(cmd) == 'M') cmd = rel ? 'l' : 'L'; // extra pairs after a move are lines
                    break;
                }
                case 'H': { if (!Num(out var a)) return Bad(cmd); x = rel ? x + a : a; rows.Add(new LineRow(F(x, y))); break; }
                case 'V': { if (!Num(out var a)) return Bad(cmd); y = rel ? y + a : a; rows.Add(new LineRow(F(x, y))); break; }
                case 'C':
                {
                    if (!Num(out var x1) || !Num(out var y1) || !Num(out var x2) || !Num(out var y2) || !Num(out var x3) || !Num(out var y3)) return Bad(cmd);
                    if (rel) { x1 += x; y1 += y; x2 += x; y2 += y; x3 += x; y3 += y; }
                    double x0 = x, y0 = y;
                    Curve(t => { var u = 1 - t; return (u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3, u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3); });
                    x = x3; y = y3;
                    break;
                }
                case 'Q':
                {
                    if (!Num(out var x1) || !Num(out var y1) || !Num(out var x2) || !Num(out var y2)) return Bad(cmd);
                    if (rel) { x1 += x; y1 += y; x2 += x; y2 += y; }
                    double x0 = x, y0 = y;
                    Curve(t => { var u = 1 - t; return (u * u * x0 + 2 * u * t * x1 + t * t * x2, u * u * y0 + 2 * u * t * y1 + t * t * y2); });
                    x = x2; y = y2;
                    break;
                }
                default:
                    return Result<IReadOnlyList<GeoSection>>.Fail("invalid_request", $"the path command '{cmd}' is not supported (use M, L, H, V, C, Q and Z)");
            }
            if (rows.Count > 32768 || !double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e6 || Math.Abs(y) > 1e6)
                return Result<IReadOnlyList<GeoSection>>.Fail("invalid_request", "path exceeds the coordinate or segment limit");
        }
        if (rows.Count < 2 || !drawn || rows[0] is not MoveRow) return Result<IReadOnlyList<GeoSection>>.Fail("invalid_request", "the path draws nothing");
        return Result<IReadOnlyList<GeoSection>>.Ok([new GeoSection(rows)]);

        Result<IReadOnlyList<GeoSection>> Bad(char c) => Result<IReadOnlyList<GeoSection>>.Fail("invalid_request", $"the path command '{c}' is missing a number");
    }

    /// <summary>A polyline path in the 100 x 100 box for the given geometry (used when Visio geometry is not one of the presets).</summary>
    public static string ToSvgPath(IReadOnlyList<GeoSection> sections)
    {
        var sb = new StringBuilder();
        string N(double v) => Math.Round(v * PathBox, 3).ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var s in sections.Where(s => !s.NoFill).DefaultIfEmpty(sections.FirstOrDefault() ?? new GeoSection([])))
        {
            foreach (var r in s.Rows)
            {
                switch (r)
                {
                    case MoveRow m: sb.Append("M ").Append(N(m.At.X)).Append(' ').Append(N(m.At.Y)).Append(' '); break;
                    case LineRow l: sb.Append("L ").Append(N(l.To.X)).Append(' ').Append(N(l.To.Y)).Append(' '); break;
                    case EllipseRow e:
                        for (var k = 0; k <= 32; k++)
                        {
                            var t = 2 * Math.PI * k / 32;
                            sb.Append(k == 0 ? "M " : "L ").Append(N(e.Centre.X + e.Rx * Math.Cos(t))).Append(' ').Append(N(e.Centre.Y + e.Ry * Math.Sin(t))).Append(' ');
                        }
                        break;
                }
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// A fingerprint of an outline that survives resizing and Visio rewriting formulas: the row kinds and the fractional coordinates only.
    /// If it still matches what the exporter wrote, the preset is intact; if not, the shape was redrawn in Visio.
    /// </summary>
    public static string Signature(IReadOnlyList<GeoSection> sections)
    {
        var sb = new StringBuilder();
        // Remove arithmetic round-off before quantising a half-way value (export scales to inches and back).
        string R(double v) => Math.Round(Math.Round(v, 8), 4).ToString("0.####", CultureInfo.InvariantCulture);
        foreach (var s in sections)
        {
            sb.Append(s.NoFill ? "[open" : "[fill");
            foreach (var r in s.Rows)
                sb.Append(r switch { MoveRow m => $"|M{R(m.At.X)},{R(m.At.Y)}", LineRow l => $"|L{R(l.To.X)},{R(l.To.Y)}", EllipseRow e => $"|E{R(e.Centre.X)},{R(e.Centre.Y)},{R(e.Rx)},{R(e.Ry)}", _ => "|?" });
            sb.Append(']');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }
}
