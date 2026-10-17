using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Visio;
using Xunit;

/// <summary>
/// Opens exported files in the real Visio application through its automation interface and checks what Visio itself reports: that it loads
/// the file as-is, that shapes are editable objects, that glue follows moves, that groups scale, and that a file saved by Visio reads back
/// with the same identity and geometry. This is automated evidence from the installed desktop Visio; the by-hand checklist (I41) remains separate.
/// If Visio is not installed these tests do nothing and the results file says "not run".
/// </summary>
public class VisioAcceptanceTests
{
    static readonly object Gate = new();

    static dynamic? Start()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var type = Type.GetTypeFromProgID("Visio.Application");
        if (type is null) return null;
        try
        {
            dynamic v = Activator.CreateInstance(type)!;
            v.Visible = false;
            v.AlertResponse = 7; // a repair or compatibility dialog would be answered "No": the file must not need one
            return v;
        }
        catch (COMException) { return null; }
    }

    static string Temp(string name) => Path.Combine(Path.GetTempPath(), $"diagram-visio-{Guid.NewGuid():N}-{name}");

    static void Report(string key, string value)
    {
        var path = Environment.GetEnvironmentVariable("DIAGRAM_VISIO_EVIDENCE");
        if (path is null) return;
        lock (Gate)
        {
            var obj = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            obj[key] = value;
            File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static double Cell(dynamic shape, string name) => (double)shape.CellsU(name).ResultIU;

    [Fact]
    public void VisioOpensTheExportWithoutRepairAndEveryObjectIsEditable()
    {
        lock (Gate)
        {
            dynamic? visio = Start();
            if (visio is null) { Report("visioAvailable", "no (tests did nothing)"); return; }
            var file = Temp("sample.vsdx");
            try
            {
                SampleScene.WriteTo(file);
                dynamic doc = visio.Documents.Open(file);
                dynamic page = doc.Pages.Item(1);
                Report("visioVersion", $"{visio.Version} build {visio.Build}");

                Assert.Equal(1, (int)doc.Pages.Count);
                Assert.Equal(297.0, Math.Round(Cell(page.PageSheet, "PageWidth") * 25.4, 2));
                Assert.Equal(210.0, Math.Round(Cell(page.PageSheet, "PageHeight") * 25.4, 2));
                var byName = new Dictionary<string, dynamic>();
                foreach (dynamic s in page.Shapes) byName[(string)s.Name] = s;
                foreach (var n in new[] { "rect", "ellipse", "text", "d1", "d2", "pair", "logo", "locked" }) Assert.True(byName.ContainsKey(n), $"Visio has no shape named {n}");
                Assert.Equal("Probe", ((string)byName["rect"].Text).Trim());
                Assert.Equal("Text box", ((string)byName["text"].Text).Trim());
                Assert.Equal(2, (int)byName["pair"].Type); // a Visio group
                Assert.Equal(4, (int)byName["logo"].Type); // a Visio picture (visTypeForeignObject)
                Assert.Equal(1, (int)byName["rect"].Type == 3 ? 1 : 0);
                // the full identity is visible in the shape's own data, not truncated
                Assert.Equal(Doc.Id(SampleScene.Rect), (string)byName["rect"].CellsU("User.AgentId").ResultStr(0));
                Assert.Equal(-33 * Math.PI / 180, Cell(byName["ellipse"], "Angle"), 6);
                Assert.Equal(1, (int)byName["locked"].CellsU("LockMoveX").ResultIU);

                // layers: two, both visible; the ellipse belongs to both
                Assert.Equal(2, (int)page.Layers.Count);
                Assert.Equal(2, (int)byName["ellipse"].LayerCount);
                Assert.Equal(1, (int)byName["rect"].LayerCount);

                // connectors are connectors: the three glued ones know both their ends
                var connectors = new List<dynamic>();
                foreach (dynamic s in page.Shapes) if ((int)s.OneD != 0) connectors.Add(s);
                Assert.Equal(4, connectors.Count);
                Assert.Equal(3, connectors.Count(c => (int)c.Connects.Count == 2));
                Assert.Equal(1, connectors.Count(c => (int)c.Connects.Count == 0)); // the free one
                Report("opensWithoutRepair", $"yes: {(int)page.Shapes.Count} shapes, {connectors.Count} connectors, 1 group, 1 picture, 2 layers");
                doc.Close();
            }
            finally { try { visio.Quit(); } catch { } try { File.Delete(file); } catch { } }
        }
    }

    [Fact]
    public void GlueFollowsMovesAndGroupsScaleInVisio()
    {
        lock (Gate)
        {
            dynamic? visio = Start();
            if (visio is null) return;
            var file = Temp("sample.vsdx");
            try
            {
                SampleScene.WriteTo(file);
                dynamic doc = visio.Documents.Open(file);
                dynamic page = doc.Pages.Item(1);
                dynamic Named(string n) => page.Shapes.ItemU(n);

                // static glue: move the source up one inch, the connector's begin point moves with it
                dynamic staticConn = page.Shapes.ItemU("connector");
                var beginBefore = Cell(staticConn, "BeginY");
                dynamic rect = Named("rect");
                rect.CellsU("PinY").ResultIU = Cell(rect, "PinY") + 1;
                Assert.Equal(beginBefore + 1, Cell(staticConn, "BeginY"), 6);

                // dynamic glue: the connector's ends are recomputed onto the moved shape's edge
                dynamic dynConn = page.Shapes.ItemU("Sheet.12");
                var endBefore = (Cell(dynConn, "BeginX"), Cell(dynConn, "BeginY"));
                dynamic d1 = Named("d1");
                d1.CellsU("PinX").ResultIU = Cell(d1, "PinX") + 1.5;
                Assert.NotEqual(endBefore, (Cell(dynConn, "BeginX"), Cell(dynConn, "BeginY")));
                // and it lands on the shape's outline, not at its centre (this one has rounded corners, so allow for the cut corner)
                var dx = Cell(dynConn, "BeginX") - Cell(d1, "PinX"); var dy = Cell(dynConn, "BeginY") - Cell(d1, "PinY");
                Assert.True(Math.Sqrt(dx * dx + dy * dy) >= 0.85 * Math.Min(Cell(d1, "Width"), Cell(d1, "Height")) / 2);

                // a group: doubling its width doubles its members' width and moves their pins proportionally
                dynamic group = Named("pair"); dynamic ga = group.Shapes.ItemU("ga");
                var w = Cell(ga, "Width"); var pin = Cell(ga, "PinX");
                group.CellsU("Width").ResultIU = Cell(group, "Width") * 2;
                Assert.Equal(w * 2, Cell(ga, "Width"), 6);
                Assert.Equal(pin * 2, Cell(ga, "PinX"), 6);

                // text and fill edits through Visio's own object model
                rect.Text = "Edited in Visio";
                rect.CellsU("FillForegnd").FormulaU = "RGB(255,0,0)";
                Assert.Equal("Edited in Visio", ((string)rect.Text).Trim());
                Report("glueAndGroupBehaviour", "static glue follows a move; dynamic glue reroutes to the shape edge; a group resize scales its members; text and fill edit normally");
                doc.Close(); // unsaved changes are discarded
            }
            finally { try { visio.Quit(); } catch { } try { File.Delete(file); } catch { } }
        }
    }

    [Fact]
    public void AFileSavedByVisioReadsBackWithTheSameIdentityAndGeometry()
    {
        lock (Gate)
        {
            dynamic? visio = Start();
            if (visio is null) return;
            var file = Temp("sample.vsdx"); var saved = Temp("resaved.vsdx");
            try
            {
                var (snap, blobs, png) = SampleScene.Build();
                File.WriteAllBytes(file, RT.Export(snap, blobs).Package);
                dynamic doc = visio.Documents.Open(file);
                doc.SaveAs(saved);
                doc.Close();

                var back = RT.Import(File.ReadAllBytes(saved));
                var original = snap.Document;
                Assert.Equal(original.Id, back.Document.Id);
                Assert.Equal(original.Pages[0].Id, back.Document.Pages[0].Id);
                Assert.Equal(0, back.Identity.Generated + back.Identity.Repaired);
                var before = RT.All(original).ToDictionary(e => e["id"]!.GetValue<string>());
                var after = RT.All(back.Document).ToDictionary(e => e["id"]!.GetValue<string>());
                Assert.Equal(before.Keys.OrderBy(x => x), after.Keys.OrderBy(x => x)); // every UUID survived a Visio save
                foreach (var (id, was) in before)
                {
                    var now = after[id];
                    Assert.Equal(was["kind"]!.GetValue<string>(), now["kind"]!.GetValue<string>());
                    Assert.Equal(was["alias"]?.GetValue<string>(), now["alias"]?.GetValue<string>());
                    if (was["kind"]!.GetValue<string>() is "connector") continue; // connector boxes are fallbacks, checked through their ends below
                    foreach (var k in new[] { "x", "y", "width", "height" })
                        Assert.True(Math.Abs(was["bounds"]![k]!.GetValue<double>() - now["bounds"]![k]!.GetValue<double>()) <= 0.01, $"{id} {k}: {was["bounds"]![k]} vs {now["bounds"]![k]}");
                    var rot = (now["rotationDeg"]!.GetValue<double>() - was["rotationDeg"]!.GetValue<double>() + 540) % 360 - 180;
                    Assert.True(Math.Abs(rot) <= 0.01, $"{id} rotation");
                    Assert.Equal(was["locked"]!.GetValue<bool>(), now["locked"]!.GetValue<bool>());
                    Assert.Equal(((JsonArray)was["layerIds"]!).Select(n => n!.GetValue<string>()).OrderBy(x => x), ((JsonArray)now["layerIds"]!).Select(n => n!.GetValue<string>()).OrderBy(x => x));
                }
                // glue survived the save: static ends keep their port, dynamic ends stay dynamic, the free one stays free
                Assert.Equal("static", after[Doc.Id(SampleScene.StaticConn)]["from"]!["glue"]!.GetValue<string>());
                Assert.Equal("out", after[Doc.Id(SampleScene.StaticConn)]["from"]!["port"]!.GetValue<string>());
                Assert.Equal("dynamic", after[Doc.Id(SampleScene.DynConn)]["from"]!["glue"]!.GetValue<string>());
                Assert.Equal("none", after[Doc.Id(SampleScene.FreeConn)]["from"]!["glue"]!.GetValue<string>());
                // the picture's bytes are untouched by Visio's save
                var blob = Assert.Single(back.Blobs);
                Assert.Equal(BlobStore.HashOf(png), blob.Sha256);
                Assert.Equal(new[] { "Layer A", "Layer B" }, back.Document.Pages[0].Layers.Select(l => l.Name));
                Report("saveByVisioRoundTrip", $"{after.Count} elements, identical UUIDs, bounds within 0.01 pt, rotation within 0.01 degrees, glue and picture bytes preserved");
            }
            finally { try { visio.Quit(); } catch { } foreach (var f in new[] { file, saved }) try { File.Delete(f); } catch { } }
        }
    }

    [Fact]
    public void LayerHideLockAndNoPrintAreRealVisioLayerSettings()
    {
        lock (Gate)
        {
            dynamic? visio = Start();
            if (visio is null) return;
            var file = Temp("layers.vsdx");
            try
            {
                var layers = new object[] { Doc.Layer(Doc.LayerA, "Hidden", visible: false), Doc.Layer(Doc.LayerB, "Locked and unprinted", locked: true, printable: false) };
                var shapes = new object[] { Doc.Shape(1, 100, 100, 60, 40, new { alias = "h", layerIds = new[] { Doc.LayerA } }), Doc.Shape(2, 200, 100, 60, 40, new { alias = "l", layerIds = new[] { Doc.LayerB } }) };
                File.WriteAllBytes(file, RT.Export(Doc.Snapshot(Doc.Page(shapes, layers: layers))).Package);
                dynamic doc = visio.Documents.Open(file);
                dynamic page = doc.Pages.Item(1);
                dynamic hidden = page.Layers.Item("Hidden"); dynamic locked = page.Layers.Item("Locked and unprinted");
                Assert.Equal(0, (int)hidden.CellsC(4).ResultIU);   // visLayerVisible
                Assert.Equal(1, (int)locked.CellsC(7).ResultIU);   // visLayerLock
                Assert.Equal(0, (int)locked.CellsC(3).ResultIU);   // visLayerPrint
                Report("layerSettings", "hide, lock and no-print are honoured by Visio as written");
                doc.Close();
            }
            finally { try { visio.Quit(); } catch { } try { File.Delete(file); } catch { } }
        }
    }
}
