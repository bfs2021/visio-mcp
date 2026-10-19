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
