namespace Diagram.Visio;

/// <summary>Places along a drawn route, as the editor measures them: a fraction of the route's length, and a distance to the right of the direction of travel (page y grows downwards).</summary>
internal static class PathMath
{
    public static (double X, double Y)? PointAt(IReadOnlyList<(double X, double Y)> route, double position, double offsetPt)
    {
        if (route.Count == 0) return null;
        var segs = new double[Math.Max(route.Count - 1, 0)];
        double total = 0;
        for (var i = 0; i < segs.Length; i++) { segs[i] = Math.Sqrt(Math.Pow(route[i + 1].X - route[i].X, 2) + Math.Pow(route[i + 1].Y - route[i].Y, 2)); total += segs[i]; }
        if (route.Count == 1 || total == 0) return route[0];
        var remaining = Math.Clamp(position, 0, 1) * total;
        var s = 0;
        while (s < segs.Length - 1 && remaining > segs[s]) { remaining -= segs[s]; s++; }
        var a = route[s]; var b = route[s + 1]; var len = segs[s] == 0 ? 1 : segs[s];
        double tx = (b.X - a.X) / len, ty = (b.Y - a.Y) / len;
        var along = Math.Min(remaining, len) / len;
        return (a.X + (b.X - a.X) * along - ty * offsetPt, a.Y + (b.Y - a.Y) * along + tx * offsetPt);
    }
}

/// <summary>R16: where a hopping connector arcs over the connectors it crosses. The same rules as the editor's (canvas/hopShape.ts), in page points.</summary>
internal static class HopMath
{
    /// <summary>Half the width of a hop, in points; the editor draws the same.</summary>
    public const double RadiusPt = 4;

    public readonly record struct Hop(double X, double Y, double Along);

    static (double T, double U)? CrossAt((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
        var den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-6) return null;
        var t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / den;
        var u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / den;
        return t > 0 && t < 1 && u > 0 && u < 1 ? (t, u) : null;
    }

    /// <summary>For each segment of the route, the crossings that are clear of its corners and of each other, in order along the segment.</summary>
    public static List<Hop>[] Crossings(IReadOnlyList<(double X, double Y)> route, IEnumerable<IReadOnlyList<(double X, double Y)>> others, double radius = RadiusPt)
    {
        var list = others.ToList();
        var result = new List<Hop>[Math.Max(route.Count - 1, 0)];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = [];
            var a = route[i]; var b = route[i + 1]; var len = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            if (len < radius * 4) continue;
            var found = new List<Hop>();
            foreach (var other in list)
                for (var j = 1; j < other.Count; j++)
                {
                    if (CrossAt(a, b, other[j - 1], other[j]) is not { } hit) continue;
                    var along = hit.T * len; var otherLen = Math.Sqrt(Math.Pow(other[j].X - other[j - 1].X, 2) + Math.Pow(other[j].Y - other[j - 1].Y, 2));
                    if (along < radius * 1.5 || len - along < radius * 1.5) continue;
                    if (hit.U * otherLen < radius || (1 - hit.U) * otherLen < radius) continue;
                    found.Add(new Hop(a.X + (b.X - a.X) * hit.T, a.Y + (b.Y - a.Y) * hit.T, hit.T));
                }
            foreach (var f in found.OrderBy(h => h.Along))
                if (result[i].Count == 0 || (f.Along - result[i][^1].Along) * len >= radius * 2.5) result[i].Add(f);
        }
        return result;
    }
}
