using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Xunit;

/// <summary>A document with one of everything the exporter maps, used for round trips and for opening in real Visio.</summary>
static class SampleScene
{
    public const int Rect = 1, Ellipse = 2, TextBox = 3, Dyn1 = 4, Dyn2 = 5, GroupN = 6, GA = 7, GB = 8, Picture = 9, StaticConn = 10, DynConn = 11, FreeConn = 12, OrthoConn = 13, Locked = 14;

    public static (ExportSnapshot Snapshot, Blobs Blobs, byte[] Png) Build()
    {
        var blobs = new Blobs();
        var png = Fx.Png(32, 16);
        var sha = blobs.Add(png);
        var layers = new object[] { Doc.Layer(Doc.LayerA, "Layer A"), Doc.Layer(Doc.LayerB, "Layer B", visible: true) };
        var rect = Doc.Shape(Rect, 60, 80, 120, 60, new { alias = "rect", layerIds = new[] { Doc.LayerA }, ports = new[] { new { id = "out", x = 1.0, y = 0.5 }, new { id = "in", x = 0.0, y = 0.5 } } }, text: Doc.Text("Probe"));
        var ellipse = Doc.Shape(Ellipse, 300, 80, 120, 60, new { alias = "ellipse", rotationDeg = 33, layerIds = new[] { Doc.LayerB, Doc.LayerA }, ports = new[] { new { id = "in", x = 0.0, y = 0.5 } } }, preset: "ellipse");
        var tb = JsonSerializer.SerializeToNode(new { id = Doc.Id(TextBox), kind = "text", layerIds = Array.Empty<string>(), zIndex = 3, locked = false, hidden = false, bounds = new { x = 60, y = 20, width = 200, height = 30 }, rotationDeg = 0, metadata = new { }, text = Doc.Text("Text box", new { horizontalAlign = "left" }) })!;
        var d1 = Doc.Shape(Dyn1, 60, 250, 100, 50, new { alias = "d1" }, preset: "rounded-rectangle");
        var d2 = Doc.Shape(Dyn2, 300, 330, 100, 50, new { alias = "d2" });
        var ga = Doc.Shape(GA, 520, 80, 60, 40, new { alias = "ga" });
        var gb = Doc.Shape(GB, 600, 150, 60, 40, new { alias = "gb" }, preset: "ellipse");
        var group = JsonSerializer.SerializeToNode(new { id = Doc.Id(GroupN), kind = "group", alias = "pair", layerIds = Array.Empty<string>(), zIndex = 8, locked = false, hidden = false, bounds = new { x = 520, y = 80, width = 140, height = 110 }, rotationDeg = 0, metadata = new { }, childIds = new[] { Doc.Id(GA), Doc.Id(GB) } })!;
        var pic = JsonSerializer.SerializeToNode(new { id = Doc.Id(Picture), kind = "image", alias = "logo", layerIds = Array.Empty<string>(), zIndex = 9, locked = false, hidden = false, bounds = new { x = 520, y = 300, width = 160, height = 80 }, rotationDeg = 0, metadata = new { }, assetId = "asset:chk", fit = "contain", opacity = 1, preserveAspectRatio = true })!;
        var staticConn = Doc.Connector(StaticConn, Doc.Glue(Rect, "static", "out"), Doc.Glue(Ellipse, "static", "in"), "straight");
        var dynConn = Doc.Connector(DynConn, Doc.Glue(Dyn1), Doc.Glue(Dyn2), "straight");
        var freeConn = Doc.Connector(FreeConn, Doc.Free(60, 450), Doc.Free(260, 500), "straight", new { waypoints = Array.Empty<object>() });
        var ortho = Doc.Connector(OrthoConn, Doc.Glue(GA, "static", "south"), Doc.Glue(Picture, "dynamic"), "orthogonal", new { waypoints = new[] { new { x = 550.0, y = 250.0 } } });
        var locked = Doc.Shape(Locked, 700, 450, 80, 40, new { locked = true, alias = "locked" });
        var elements = new object[] { rect, ellipse, tb, d1, d2, ga, gb, group, pic, staticConn, dynConn, freeConn, ortho, locked };
        var asset = new { id = "asset:chk", name = "checker.png", mimeType = "image/png", sha256 = sha, tags = new[] { "logo" }, provenance = "test" };
        var doc = Doc.Make([Doc.Page(elements, layers: layers)], [asset]);
        // routes the editor would have resolved
        var routes = new Dictionary<string, JsonObject>
        {
            [Doc.Id(OrthoConn)] = Doc.Route((550, 124), (550, 250), (600, 250), (600, 340)),
        };
        return (Doc.Snapshot(doc, routes), blobs, png);
    }

    public static string WriteTo(string path)
    {
        var (snap, blobs, _) = Build();
        File.WriteAllBytes(path, RT.Export(snap, blobs).Package);
        return path;
    }
}

public class SampleTests
{
    [Fact]
    public void WritesASampleForInspectionWhenAskedTo()
    {
        var path = Environment.GetEnvironmentVariable("DIAGRAM_SAMPLE_OUT");
        if (path is null) return;
        SampleScene.WriteTo(path);
        Assert.True(File.Exists(path));
    }
}
