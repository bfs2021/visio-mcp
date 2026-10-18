using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>
/// Spec section 23, the fourteen-step demonstration, run through a real stdio MCP client, the real pipe and the real WPF/WebView2 editor.
/// The "human" acts with real mouse events and the inspector's colour input inside the page. Every step saves the document snapshot and the
/// transaction it produced to docs/acceptance/evidence/DEMO; tools/demo/verify-demo.mjs then checks, from those files alone, that unrelated
/// objects stayed byte-identical. Steps 11 to 13 need Visio and Word and are marked NOT RUN when they are not installed.
/// </summary>
public class DemoTests
{
    static string Evidence => Path.Combine(Harness.RepoRoot, "docs", "acceptance", "evidence", "DEMO");
    static CancellationToken Ct(int s = 90) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;
    static JsonElement Body(CallToolResult r) => r.StructuredContent!.Value;
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    sealed record Row(int Step, string Title, string Actor, string Status, string Detail);
    readonly List<Row> rows = [];
    readonly JsonArray transcript = [];

    static byte[] Png(byte r, byte g, byte b)
    {
        var px = new byte[48 * 24 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 0xFF; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(48, 24, 96, 96, PixelFormats.Bgra32, null, px, 48 * 4)));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    async Task Step(int n, string title, string actor, Func<Task<string>> run)
    {
        try { rows.Add(new Row(n, title, actor, "PASS", await run())); }
        catch (Exception e) { rows.Add(new Row(n, title, actor, "FAIL", e.GetBaseException().Message)); }
    }

    [Fact]
    public async Task TheFourteenStepArchitectureDiagramDemonstration()
    {
        if (Directory.Exists(Evidence)) Directory.Delete(Evidence, true);
        Directory.CreateDirectory(Evidence);
        var work = Path.Combine(Path.GetTempPath(), "demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pipe = $"demo-{Guid.NewGuid():N}";
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: Path.Combine(work, "exports"));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "demo", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: Ct());

        // ---------------------------------------------------------------- helpers
        async Task<JsonElement> Snapshot() { var r = await h.Send("doc.snapshot"); Assert.Null(r.Error); return r.Result!.Value.Clone(); }
        async Task<(string Doc, string Session, int Revision, string Page)> Summary()
        {
            var r = await client.CallToolAsync("get_document_summary", cancellationToken: Ct());
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            var b = Body(r);
            return (b.GetProperty("documentId").GetString()!, b.GetProperty("sessionId").GetString()!, b.GetProperty("revision").GetInt32(), b.GetProperty("pages")[0].GetProperty("id").GetString()!);
        }
        string IdOf(JsonElement snap, string alias) => Elements(snap).Values.First(e => e.TryGetProperty("alias", out var a) && a.GetString() == alias).GetProperty("id").GetString()!;
        void Save(string file, JsonElement v) => File.WriteAllText(Path.Combine(Evidence, file), JsonSerializer.Serialize(v, Pretty));
        void Record(int step, string actor, string what, string? snapshotFile, string? transactionFile = null, int? revision = null)
        {
            var o = new JsonObject { ["step"] = step, ["actor"] = actor, ["what"] = what, ["snapshot"] = snapshotFile, ["transaction"] = transactionFile, ["revision"] = revision };
            transcript.Add(o);
        }
        async Task<JsonElement> Tool(string name, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: Ct());
            Assert.True(r.IsError != true, name + ": " + r.StructuredContent?.ToString());
            return Body(r).Clone();
        }
        Dictionary<string, object?> Scope(string doc, string session, Dictionary<string, object?> more) { more["documentId"] = doc; more["sessionId"] = session; return more; }

        async Task Mouse(string type, double x, double y) => await h.OnUi(async () =>
        {
            await h.Host.FireDevToolsAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new { type, x, y, button = type == "mouseMoved" ? "none" : "left", buttons = type == "mouseReleased" ? 0 : 1, clickCount = type == "mouseMoved" ? 0 : 1 }));
            return 0;
        });
        // the editor's test API is compiled out of the shipped build, so a person's target is found the way a person finds it: by its label on the canvas
        async Task<(double X, double Y)> Centre(string label)
        {
            var j = JsonNode.Parse(await h.Eval($$"""
                (() => { const t = [...document.querySelectorAll('[data-testid=canvas] svg text')].find(n => n.textContent === '{{label}}');
                  if (!t) return null; const b = t.getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; })()
                """))!;
            return (j["x"]!.GetValue<double>(), j["y"]!.GetValue<double>());
        }

        // ================================================================ step 1: a blank A4 landscape page
        var s0 = await Summary();
        JsonElement snap = default;
        await Step(1, "Human opens a blank A4 landscape page", "human", async () =>
        {
            snap = await Snapshot();
            var page = snap.GetProperty("document").GetProperty("pages")[0];
            var w = page.GetProperty("widthPt").GetDouble(); var hgt = page.GetProperty("heightPt").GetDouble();
            Assert.InRange(w, 841.8, 842.0); Assert.InRange(hgt, 595.2, 595.4); // 297 x 210 mm
            Assert.Empty(Elements(snap));
            Save("step01-snapshot.json", snap); Record(1, "human", "blank A4 landscape page", "step01-snapshot.json", revision: s0.Revision);
            return $"{w:F1} x {hgt:F1} pt, no elements, revision {s0.Revision}";
        });

        // ---- the approved library holds two logos (the agent picks by id; step 8)
        var oldLogo = await h.Window.Preparer.PrepareAsync(new MemoryStream(Png(0x20, 0x60, 0xC0)), "image/png", "esg-old.png", Ct());
        var newLogo = await h.Window.Preparer.PrepareAsync(new MemoryStream(Png(0x10, 0x90, 0x40)), "image/png", "esg-global-primary.png", Ct());
        Assert.True(oldLogo.IsOk && newLogo.IsOk);
        Assert.True((await h.Window.Library.ImportAsync(oldLogo.Value, ["logo", "esg"], Ct())).IsOk);
        Assert.True((await h.Window.Library.ImportAsync(newLogo.Value, ["logo", "esg", "primary"], Ct())).IsOk);
        Assert.True((await h.Window.Library.SetMetadataAsync("asset:esg-old", "ESG Legacy Logo", null, Ct())).IsOk);
        Assert.True((await h.Window.Library.SetMetadataAsync("asset:esg-global-primary", "ESG Global — Primary Logo", null, Ct())).IsOk);

        // ================================================================ steps 2 and 3: the agent draws; the editor shows it
        string tx2 = Guid.NewGuid().ToString();
        await Step(2, "LLM creates four labelled blocks, connectors, annotations and a logo through MCP", "agent", async () =>
        {
            var script = File.ReadAllText(Path.Combine(Harness.RepoRoot, "tools", "demo", "scenario.drawscript"));
            var made = await Tool("execute_script", Scope(s0.Doc, s0.Session, new() { ["transactionId"] = tx2, ["baseRevision"] = s0.Revision, ["script"] = script, ["libraryAssets"] = new[] { "asset:esg-old" } }));
            Save("step02-transaction.json", made.GetProperty("transaction"));
            snap = await Snapshot();
            Save("step02-snapshot.json", snap);
            Record(2, "agent", "execute_script: four blocks, three connectors, two annotations, one logo", "step02-snapshot.json", "step02-transaction.json", snap.GetProperty("revision").GetInt32());
            Assert.Equal(10, Elements(snap).Count); // 4 blocks + 3 connectors + 2 annotations + 1 logo
            return $"{Elements(snap).Count} objects in one atomic transaction, revision {snap.GetProperty("revision").GetInt32()}";
        });

        await Step(3, "The editor visibly shows the result", "app", async () =>
        {
            var labels = new[] { "CRM", "ERP", "Billing", "Data warehouse", "Customer records, nightly" };
            string found = "";
            for (var i = 0; i < 50 && found.Length == 0; i++)
            {
                var texts = JsonNode.Parse(await h.Eval("[...document.querySelectorAll('[data-testid=canvas] svg text')].map(t => t.textContent).join('|')"))!.GetValue<string>();
                if (labels.All(l => texts.Contains(l))) found = texts; else await Task.Delay(100);
            }
            Assert.NotEqual("", found);
            var png = await h.OnUi(() => h.Host.CapturePreviewAsync());
            File.WriteAllBytes(Path.Combine(Evidence, "step03-editor.png"), png);
            Record(3, "app", "the editor canvas shows the labels of the new objects", null);
            return "every label is drawn on the canvas; editor screenshot saved";
        });

        // ================================================================ steps 4 and 5: preview, one alignment defect, a precise correction
        var seen = await Summary();
        var before5 = snap;
        await Step(4, "LLM requests a PNG preview and notices one alignment problem", "agent", async () =>
        {
            var r = await client.CallToolAsync("render_page", Scope(seen.Doc, seen.Session, new() { ["pageId"] = seen.Page, ["format"] = "png" }), cancellationToken: Ct());
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            var png = r.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray();
            File.WriteAllBytes(Path.Combine(Evidence, "step04-preview.png"), png);
            Assert.True(png.Length > 5_000);
            // the agent reads the geometry that goes with the picture: CRM and ERP are meant to sit in one row
            var els = Elements(snap);
            double Y(string alias) => els[IdOf(snap, alias)].GetProperty("bounds").GetProperty("y").GetDouble();
            var dy = Y("erp") - Y("crm");
            Assert.InRange(dy, 11.0, 11.7); // 4 mm
            Assert.Equal(Y("billing"), Y("data"), 3); // the other pair is aligned
            var layout = await Tool("inspect_layout", Scope(seen.Doc, seen.Session, new()));
            File.WriteAllText(Path.Combine(Evidence, "step04-findings.json"), JsonSerializer.Serialize(new
            {
                revision = seen.Revision, preview = "step04-preview.png",
                misaligned = new { elements = new[] { "crm", "erp" }, rowDeltaPt = Math.Round(dy, 2), expected = 0 },
                layoutInspector = layout, note = "inspect_layout has no alignment check; the agent compares the bounds returned with the preview.",
            }, Pretty));
            Record(4, "agent", "render_page and bounds comparison: ERP is 11.3 pt below CRM", null);
            return $"preview {png.Length} bytes; ERP sits {dy:F1} pt below CRM (inspect_layout reports {layout.GetProperty("issues").GetArrayLength()} issues)";
        });

        await Step(5, "LLM aligns only the affected elements", "agent", async () =>
        {
            var made = await Tool("apply_operations", Scope(seen.Doc, seen.Session, new()
            {
                ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = seen.Revision,
                ["operations"] = JsonSerializer.SerializeToElement(new object[] { new { op = "align", targets = new[] { "crm", "erp" }, edge = "top" } }),
            }));
            Save("step05-transaction.json", made.GetProperty("transaction"));
            snap = await Snapshot();
            Save("step05-before.json", before5); Save("step05-snapshot.json", snap);
            Record(5, "agent", "apply_operations: align top of crm and erp", "step05-snapshot.json", "step05-transaction.json", snap.GetProperty("revision").GetInt32());
            var els = Elements(snap);
            Assert.Equal(els[IdOf(snap, "crm")].GetProperty("bounds").GetProperty("y").GetDouble(), els[IdOf(snap, "erp")].GetProperty("bounds").GetProperty("y").GetDouble(), 3);
            var changed = made.GetProperty("transaction").GetProperty("changed").EnumerateArray().Select(x => x.GetString()!).ToList();
            return $"changed {changed.Count} object(s); the verifier checks the rest are byte-identical";
        });

        // ================================================================ step 6: the human edits with the mouse and the inspector
        var knownRevision = snap.GetProperty("revision").GetInt32();
        var dataId = IdOf(snap, "data"); var billingId = IdOf(snap, "billing");
        double dataYBefore = 0, dataXBefore = 0;
        await Step(6, "Human drags one element and changes one fill colour", "human", async () =>
        {
            var els = Elements(snap);
            dataXBefore = els[dataId].GetProperty("bounds").GetProperty("x").GetDouble(); dataYBefore = els[dataId].GetProperty("bounds").GetProperty("y").GetDouble();
            var from = await Centre("Data warehouse");
            await Mouse("mouseMoved", from.X, from.Y); await Mouse("mousePressed", from.X, from.Y);
            for (var i = 1; i <= 8; i++) { await Mouse("mouseMoved", from.X + i * 6, from.Y + i * 2); await Task.Delay(20); }
            await Mouse("mouseReleased", from.X + 48, from.Y + 16);
            await WaitFor(async () => (await Snapshot()).GetProperty("revision").GetInt32() > knownRevision);

            // select the billing block with a click, then use the inspector's fill colour
            var c = await Centre("Billing");
            await Mouse("mouseMoved", c.X, c.Y); await Mouse("mousePressed", c.X, c.Y); await Mouse("mouseReleased", c.X, c.Y);
            await WaitFor(async () => (await h.Eval("document.querySelector('[data-testid=field-fill-colour]') !== null")) == "true");
            await h.Eval("""
                (() => { const i = document.querySelector('[data-testid=field-fill-colour]');
                  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(i, '#fde68a');
                  i.dispatchEvent(new Event('input', { bubbles: true })); return true; })()
                """);
            await WaitFor(async () => Elements(await Snapshot())[billingId].GetProperty("style").GetProperty("fill").GetProperty("colour").GetString()!.Equals("#fde68a", StringComparison.OrdinalIgnoreCase));
            snap = await Snapshot();
            Save("step06-snapshot.json", snap);
            Record(6, "human", "dragged 'data' with the mouse; changed the fill of 'billing' in the inspector", "step06-snapshot.json", revision: snap.GetProperty("revision").GetInt32());
            var nowEls = Elements(snap);
            var moved = nowEls[dataId].GetProperty("bounds").GetProperty("x").GetDouble() - dataXBefore;
            Assert.True(moved > 20, $"the drag moved 'data' by only {moved:F1} pt");
            return $"'data' moved {moved:F1} pt right; 'billing' fill is #fde68a; revision {snap.GetProperty("revision").GetInt32()}";
        });

        // ================================================================ step 7: the agent catches up without losing the human's work
        var afterHuman = snap;
        await Step(7, "LLM reads changes since its previous revision and preserves the human edits", "agent", async () =>
        {
            // an agent still planning against its old revision is refused, and learns why
            var stale = await client.CallToolAsync("apply_operations", Scope(seen.Doc, seen.Session, new()
            {
                ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = knownRevision,
                ["operations"] = JsonSerializer.SerializeToElement(new object[] { new { op = "set", target = "note1", patch = new { text = new { value = "Customer records, synced nightly" } } } }),
            }), cancellationToken: Ct());
            Assert.True(stale.IsError == true);
            Assert.Equal("revision_conflict", Body(stale).GetProperty("error").GetProperty("code").GetString());
            var changes = await Tool("get_changes", Scope(seen.Doc, seen.Session, new() { ["sinceRevision"] = knownRevision }));
            Save("step07-changes.json", changes);
            var touched = changes.GetProperty("transactions").EnumerateArray().SelectMany(t => t.GetProperty("changed").EnumerateArray()).Select(x => x.GetString()!).ToHashSet();
            Assert.Contains(dataId, touched); Assert.Contains(billingId, touched);
            Assert.All(changes.GetProperty("transactions").EnumerateArray(), t => Assert.Equal("gui", t.GetProperty("source").GetString()));

            // the retry is based on what the agent just learned
            var fresh = await Summary();
            var made = await Tool("apply_operations", Scope(fresh.Doc, fresh.Session, new()
            {
                ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = fresh.Revision,
                ["operations"] = JsonSerializer.SerializeToElement(new object[] { new { op = "set", target = "note1", patch = new { text = new { value = "Customer records, synced nightly" } } } }),
            }));
            Save("step07-transaction.json", made.GetProperty("transaction"));
            snap = await Snapshot();
            Save("step07-before.json", afterHuman); Save("step07-snapshot.json", snap);
            Record(7, "agent", "get_changes then a follow-up edit to note1 at the current revision", "step07-snapshot.json", "step07-transaction.json", snap.GetProperty("revision").GetInt32());
            var els = Elements(snap);
            Assert.Equal(Elements(afterHuman)[dataId].GetRawText(), els[dataId].GetRawText()); // the dragged block is byte-identical to what the human left
            Assert.Equal("#fde68a", els[billingId].GetProperty("style").GetProperty("fill").GetProperty("colour").GetString()!.ToLowerInvariant());
            return $"stale write refused (revision_conflict); get_changes named the human's {touched.Count} objects; the follow-up edit left both human edits intact";
        });

        // ================================================================ steps 8 and 9: one approved logo swap
        var logoId = IdOf(snap, "esg_logo");
        await Step(8, "Human asks for a logo replacement; the LLM finds the approved asset by id", "agent", async () =>
        {
            var fresh = await Summary();
            var found = await Tool("list_assets", Scope(fresh.Doc, fresh.Session, new() { ["query"] = "ESG Global" }));
            Save("step08-assets.json", found);
            var hit = found.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "asset:esg-global-primary");
            Assert.Equal("library", hit.GetProperty("scope").GetString());
            Record(8, "agent", "list_assets query 'ESG Global' -> asset:esg-global-primary", null);
            return "list_assets returned asset:esg-global-primary (library scope)";
        });

        var beforeSwap = snap;
        await Step(9, "Only that image object changes", "agent", async () =>
        {
            var fresh = await Summary();
            var made = await Tool("execute_script", Scope(fresh.Doc, fresh.Session, new()
            {
                ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = fresh.Revision,
                ["script"] = "asset import prepared=\"asset:esg-global-primary\"\nset esg_logo asset=\"asset:esg-global-primary\"",
                ["libraryAssets"] = new[] { "asset:esg-global-primary" },
            }));
            Save("step09-transaction.json", made.GetProperty("transaction"));
            snap = await Snapshot();
            Save("step09-before.json", beforeSwap); Save("step09-snapshot.json", snap);
            Record(9, "agent", "execute_script: set esg_logo asset=asset:esg-global-primary", "step09-snapshot.json", "step09-transaction.json", snap.GetProperty("revision").GetInt32());
            var img = Elements(snap)[logoId];
            Assert.Equal("asset:esg-global-primary", img.GetProperty("assetId").GetString());
            var changed = made.GetProperty("transaction").GetProperty("changed").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal([logoId], changed);
            return "exactly one object changed: the image now points at asset:esg-global-primary";
        });

        // ================================================================ step 10: the document saves as .vsdx
        string exported = "";
        await Step(10, "Document saves as .vsdx", "agent", async () =>
        {
            var fresh = await Summary();
            var r = await Tool("export_document", Scope(fresh.Doc, fresh.Session, new() { ["format"] = "vsdx", ["path"] = "architecture-demo.vsdx" }));
            exported = r.GetProperty("path").GetString()!;
            Assert.True(File.Exists(exported));
            File.Copy(exported, Path.Combine(Evidence, "architecture-demo.vsdx"), true);
            Save("step10-export.json", r);
            Record(10, "agent", "export_document format=vsdx", "step09-snapshot.json");
            return $"{new FileInfo(exported).Length} bytes, lossy={r.GetProperty("lossy").GetBoolean()}";
        });

        // ================================================================ step 14a: the exported file reopens in the application and every id resolves
        var idsAtExport = Elements(snap).Keys.ToHashSet();
        await Step(14, "VSDX is reopened in the POC and agent ids still resolve (the app's own export)", "app", async () =>
        {
            var opened = await h.Window.Native.OpenAsync(exported, await Expected(h), Ct());
            Assert.True(opened.IsOk, opened.Error?.Message);
            var back = await Snapshot();
            Save("step14-snapshot.json", back);
            Record(14, "app", "native open of the app's own export", "step14-snapshot.json", revision: back.GetProperty("revision").GetInt32());
            var gone = idsAtExport.Where(i => !Elements(back).ContainsKey(i)).ToList();
            Assert.True(gone.Count == 0, "ids that no longer resolve: " + string.Join(", ", gone));
            return $"{idsAtExport.Count} ids resolve; identity preserved={opened.Value.Identity.Preserved}, generated={opened.Value.Identity.Generated}, repaired={opened.Value.Identity.Repaired}";
        });

        // ================================================================ steps 11 to 14b: Visio and Word on this very file
        await NativeSegment(h, exported, idsAtExport, Elements(snap), IdOf(snap, "billing"), IdOf(snap, "crm"), work);

        // ---------------------------------------------------------------- report
        File.WriteAllText(Path.Combine(Evidence, "transcript.json"), JsonSerializer.Serialize(transcript, Pretty));
        var result = new JsonObject
        {
            ["ran"] = DateTime.UtcNow.ToString("O"),
            ["environment"] = new JsonObject { ["os"] = Environment.OSVersion.VersionString, ["dotnet"] = Environment.Version.ToString(), ["webview2"] = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() },
            ["steps"] = new JsonArray(rows.OrderBy(r => r.Step).Select(r => (JsonNode)new JsonObject { ["step"] = r.Step, ["title"] = r.Title, ["actor"] = r.Actor, ["status"] = r.Status, ["detail"] = r.Detail }).ToArray()),
        };
        File.WriteAllText(Path.Combine(Evidence, "result.json"), result.ToJsonString(Pretty));
        try { Directory.Delete(work, true); } catch { }
        var failed = rows.Where(r => r.Status == "FAIL").ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed.Select(f => $"step {f.Step} {f.Title}: {f.Detail}")));
    }

    static Dictionary<string, JsonElement> Elements(JsonElement snap, int page = 0) =>
        snap.GetProperty("document").GetProperty("pages")[page].GetProperty("elements").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.Clone());

    static async Task<JsonElement> SnapshotOf(Harness h) { var r = await h.Send("doc.snapshot"); Assert.Null(r.Error); return r.Result!.Value.Clone(); }

    static async Task<ExpectedState> Expected(Harness h)
    {
        var s = (await h.Send("doc.status")).Result!.Value;
        return new ExpectedState { DocumentId = s.GetProperty("documentId").GetString()!, SessionId = s.GetProperty("sessionId").GetString()!, BaseRevision = s.GetProperty("revision").GetInt32() };
    }

    static async Task WaitFor(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(150); }
    }

    async Task NativeSegment(Harness h, string exported, HashSet<string> ids, Dictionary<string, JsonElement> elements, string billingId, string crmId, string work)
    {
        var visioType = Type.GetTypeFromProgID("Visio.Application");
        var wordType = Type.GetTypeFromProgID("Word.Application");
        if (visioType is null || wordType is null)
        {
            foreach (var n in new[] { 11, 12, 13 }) rows.Add(new Row(n, "Visio and Word steps", "visio", "NOT RUN", "Visio or Word is not installed"));
            return;
        }
        var wordBefore = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        var visioBefore = Process.GetProcessesByName("VISIO").Select(p => p.Id).ToHashSet();
        dynamic visio = OfficeAcceptanceTests.StartVisio()!;
        string visioSaved = Path.Combine(work, "demo-visio-saved.vsdx");
        double billingPinBefore = 0;
        try
        {
            dynamic doc = visio.Documents.Open(exported);
            try
            {
                dynamic page = doc.Pages.Item(1);
                Dictionary<string, dynamic> byId = [];
                foreach (var s in OfficeAcceptanceTests.AllShapes(page.Shapes)) if (OfficeAcceptanceTests.AgentId(s) is { } id) byId[id] = s;

                await Step(11, "File opens in Visio with editable objects", "visio", () =>
                {
                    var missing = ids.Where(i => !byId.ContainsKey(i)).ToList();
                    Assert.True(missing.Count == 0, "no Visio shape for " + string.Join(", ", missing));
                    byId[crmId].Text = "CRM edited in Visio"; // editable text
                    page.Export(Path.Combine(Evidence, "step11-visio.png"));
                    return Task.FromResult($"Visio {visio.Version}: all {ids.Count} objects are Visio shapes carrying their UUID; text edited through Visio's object model");
                });

                await Step(12, "Moving a connected shape in Visio keeps its connector attached", "visio", () =>
                {
                    dynamic billing = byId[billingId];
                    billingPinBefore = OfficeAcceptanceTests.In(billing, "PinY");
                    billing.CellsU["PinY"].ResultIU = billingPinBefore - 0.5;
                    var connectors = byId.Values.Where(s => (int)s.OneD != 0).ToList();
                    var attached = connectors.Where(c => (int)c.Connects.Count == 2).ToList();
                    Assert.Equal(3, attached.Count);
                    // the connector that ends at Billing follows it
                    var toBilling = attached.First(c => Math.Abs(OfficeAcceptanceTests.In(c, "EndY") - OfficeAcceptanceTests.In(billing, "PinY")) <= OfficeAcceptanceTests.In(billing, "Height") / 2 + 0.01);
                    page.Export(Path.Combine(Evidence, "step12-after-move.png"));
                    return Task.FromResult($"all 3 connectors still have 2 connections; one ends at y={OfficeAcceptanceTests.In(toBilling, "EndY"):F2} in beside Billing's centre y={OfficeAcceptanceTests.In(billing, "PinY"):F2}");
                });

                doc.SaveAs(visioSaved);
            }
            finally { try { doc.Close(); } catch { } }

            // ---------------------------------------------------------------- step 13: Word
            var docx = Path.Combine(work, "demo.docx");
            dynamic word = Activator.CreateInstance(wordType)!;
            word.Visible = false; word.DisplayAlerts = 0;
            try
            {
                dynamic wdoc = word.Documents.Add();
                dynamic shape = null!;
                await Step(13, "VSDX embedded in Word activates and stays editable as a Visio object", "word", async () =>
                {
                    shape = wdoc.InlineShapes.AddOLEObject(Type.Missing, visioSaved, false, false);
                    Assert.StartsWith("Visio.Drawing", (string)shape.OLEFormat.ProgID);
                    var verb = Task.Run(() => shape.OLEFormat.DoVerb(-2));
                    if (await Task.WhenAny(verb, Task.Delay(TimeSpan.FromSeconds(60))) != verb) throw new TimeoutException("Word did not finish activating the object");
                    await verb;
                    dynamic obj = await OfficeAcceptanceTests.Poll((Func<object?>)(() => (object?)shape.OLEFormat.Object));
                    dynamic target = null!;
                    foreach (var s in OfficeAcceptanceTests.AllShapes(obj.Pages.Item(1).Shapes)) if (OfficeAcceptanceTests.AgentId(s) == crmId) { target = s; break; }
                    Assert.NotNull((object?)target);
                    target.Text = "CRM edited inside Word";
                    obj.Close();
                    wdoc.SaveAs2(docx); wdoc.Close(0);
                    dynamic again = word.Documents.Open(docx);
                    dynamic ils = again.InlineShapes.Item(1);
                    var verb2 = Task.Run(() => ils.OLEFormat.DoVerb(-2));
                    if (await Task.WhenAny(verb2, Task.Delay(TimeSpan.FromSeconds(60))) != verb2) throw new TimeoutException("reactivation did not finish");
                    await verb2;
                    dynamic obj2 = await OfficeAcceptanceTests.Poll((Func<object?>)(() => (object?)ils.OLEFormat.Object));
                    var texts = new List<string>();
                    foreach (var s in OfficeAcceptanceTests.AllShapes(obj2.Pages.Item(1).Shapes)) { string t = s.Text; if (t.Length > 0) texts.Add(t); }
                    Assert.Contains(texts, t => t.Contains("CRM edited inside Word"));
                    obj2.Close(); again.Close(0);
                    File.Copy(docx, Path.Combine(Evidence, "demo.docx"), true);
                    return "embedded as Visio.Drawing, activated, edited, saved, reopened and reactivated: the edit persisted";
                });
            }
            finally { try { word.Quit(0); } catch { } }

            // ---------------------------------------------------------------- step 14b: the file Visio saved comes back with every id and the edits made in Visio
            await Step(14, "The Visio-saved file reopens in the POC; agent ids still resolve and Visio's edits arrive", "app", async () =>
            {
                var opened = await h.Window.Native.OpenAsync(visioSaved, await Expected(h), Ct());
                Assert.True(opened.IsOk, opened.Error?.Message);
                var back = await SnapshotOf(h);
                File.WriteAllText(Path.Combine(Evidence, "step14-visio-snapshot.json"), JsonSerializer.Serialize(back, Pretty));
                transcript.Add(new JsonObject { ["step"] = 14, ["actor"] = "app", ["what"] = "native open of the file Visio saved", ["snapshot"] = "step14-visio-snapshot.json", ["transaction"] = null, ["revision"] = back.GetProperty("revision").GetInt32() });
                var els = Elements(back);
                var gone = ids.Where(i => !els.ContainsKey(i)).ToList();
                Assert.True(gone.Count == 0, "ids that no longer resolve: " + string.Join(", ", gone));
                Assert.Equal("CRM edited in Visio", els[crmId].GetProperty("text").GetProperty("value").GetString());
                var dy = els[billingId].GetProperty("bounds").GetProperty("y").GetDouble() - elements[billingId].GetProperty("bounds").GetProperty("y").GetDouble();
                Assert.InRange(dy, 0.5 * 72 - 1, 0.5 * 72 + 1); // moved down the page by 0.5 in
                Assert.Equal("#fde68a", els[billingId].GetProperty("style").GetProperty("fill").GetProperty("colour").GetString()!.ToLowerInvariant()); // the human's fill survived Visio
                return $"{ids.Count} ids resolve; Visio's text edit and 0.5 in move arrived; the human's fill colour survived";
            });
        }
        finally
        {
            try { visio.Quit(); } catch { }
            foreach (var p in Process.GetProcessesByName("WINWORD").Where(p => !wordBefore.Contains(p.Id))) { try { p.Kill(); } catch { } }
            foreach (var p in Process.GetProcessesByName("VISIO").Where(p => !visioBefore.Contains(p.Id))) { try { p.Kill(); } catch { } }
        }
    }
}
