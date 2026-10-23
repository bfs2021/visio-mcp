using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Diagram.Host.Core;
using ModelContextProtocol.Protocol;
using Xunit;
using static Diagram.Windows.Tests.PerfRig;

namespace Diagram.Windows.Tests;

/// <summary>
/// A06 (#21): the measurements I52 left out, on the live stack and with the A05 method (shim first, quiet gate, every run checked against pool starvation).
/// Ten pages; drag and resize traced frame by frame; large pictures; text-heavy pages; opening a file Visio saved. Raw samples go to docs/acceptance/evidence/PERF.
/// Preferred targets are reported, never gated: the assertions are generous ceilings and the correctness of what was measured.
/// </summary>
public class PerformanceScenarioTests
{
    static async Task<ExpectedState> Expected(Harness h)
    {
        var s = (await h.Send("doc.status")).Result!.Value;
        return new ExpectedState { DocumentId = s.GetProperty("documentId").GetString()!, SessionId = s.GetProperty("sessionId").GetString()!, BaseRevision = s.GetProperty("revision").GetInt32() };
    }

    static async Task<JsonElement> Snapshot(Harness h) { var r = await h.Send("doc.snapshot"); Assert.Null(r.Error); return r.Result!.Value.Clone(); }

    // ================================================================ 1. ten pages
    [Fact]
    public async Task TenPagesOfFiftyFiveElements()
    {
        await using var rig = await StartAsync();
        var h = rig.H;
        var first = await rig.Summary();

        var script = new StringBuilder();
        for (var p = 1; p <= 10; p++)
        {
            if (p > 1) script.AppendLine($"page add \"Page {p}\"");
            if (p > 1) script.AppendLine($"page use \"Page {p}\"");
            for (var i = 0; i < 50; i++)
                script.AppendLine($"add shape id=p{p}s{i} x={20 + (i % 10) * 78}pt y={20 + (i / 10) * 100}pt w=64pt h=40pt text=\"P{p}-{i}\"");
            for (var i = 0; i < 5; i++) script.AppendLine($"connect id=p{p}c{i} from=p{p}s{i} to=p{p}s{i + 1}");
        }
        var build = rig.NewSeries("build");
        var built = await rig.Time(build, () => rig.Script(first.Doc, first.Session, first.Revision, script.ToString()));
        var after = await rig.Summary();
        var pages = after.Body.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal(10, pages.Count);
        Assert.All(pages, p => Assert.Equal(55, p.GetProperty("elements").GetArrayLength()));
        var pageIds = pages.Select(p => p.GetProperty("id").GetString()!).ToList();
        var rev = after.Revision;

        // ---- reads
        var summaryAll = rig.NewSeries("summaryAllPages", 250); var summaryOne = rig.NewSeries("summaryOnePage", 250); var bytesAll = 0;
        for (var i = 0; i < 10; i++)
        {
            await rig.Time(summaryAll, async () => { var r = await rig.Client.CallToolAsync("get_document_summary", new Dictionary<string, object?> { ["includeGeometry"] = true }, cancellationToken: Ct()); bytesAll = Body(r).GetRawText().Length; return 0; });
            await rig.Time(summaryOne, async () => { await rig.Client.CallToolAsync("get_document_summary", new Dictionary<string, object?> { ["includeGeometry"] = true, ["pageId"] = pageIds[9] }, cancellationToken: Ct()); return 0; });
        }

        // ---- an edit on the first page and on the last (the script names the page; DrawScript compiles and runs it as one transaction)
        var editFirst = rig.NewSeries("editOnPage1ViaScript", 250); var editLast = rig.NewSeries("editOnPage10ViaScript", 250);
        for (var i = 0; i < 10; i++)
        {
            var r1 = await rig.Time(editFirst, () => rig.Script(first.Doc, first.Session, rev, "page use \"Page 1\"\nmove p1s5 dx=1pt"));
            rev = r1.GetProperty("transaction").GetProperty("revision").GetInt32();
            var r2 = await rig.Time(editLast, () => rig.Script(first.Doc, first.Session, rev, "page use \"Page 10\"\nmove p10s5 dx=1pt"));
            rev = r2.GetProperty("transaction").GetProperty("revision").GetInt32();
        }

        // ---- previews of three different pages
        var renders = new[] { 0, 4, 9 }.ToDictionary(i => i, i => rig.NewSeries($"renderPage{i + 1}", 1000));
        for (var i = 0; i < 5; i++)
            foreach (var (idx, series) in renders)
                await rig.Time(series, async () =>
                {
                    var r = await rig.Client.CallToolAsync("render_page", new Dictionary<string, object?> { ["documentId"] = first.Doc, ["sessionId"] = first.Session, ["pageId"] = pageIds[idx], ["maxWidth"] = 1600, ["maxHeight"] = 1200 }, cancellationToken: Ct());
                    Assert.True(r.IsError != true, r.StructuredContent?.ToString());
                    Assert.True(r.Content.OfType<ImageContentBlock>().Single().DecodedData.Length > 5_000);
                    return 0;
                });

        // ---- switching pages in the editor: click the tab, wait until that page's first label is drawn
        var switchTab = rig.NewSeries("switchPageInEditor");
        for (var round = 0; round < 3; round++)
            foreach (var n in new[] { 10, 5, 1, 7, 2 })
            {
                var label = $"P{n}-0";
                await rig.Time(switchTab, async () =>
                {
                    var ms = JsonNode.Parse(await h.Eval($$"""
                        (async () => {
                          const t0 = performance.now();
                          [...document.querySelectorAll('[data-testid=page-tab]')][{{n - 1}}].click();
                          for (let i = 0; i < 400; i++) {
                            await new Promise(r => requestAnimationFrame(r));
                            if ([...document.querySelectorAll('[data-testid=canvas] svg text')].some(t => t.textContent === '{{label}}')) { await new Promise(r => requestAnimationFrame(r)); return performance.now() - t0; }
                          }
                          return -1;
                        })()
                        """))!.GetValue<double>();
                    Assert.True(ms > 0, $"page {n} never drew");
                    return 0;
                });
            }

        // ---- save, then open: the app's own export, and the file Visio saves from it
        var save = rig.NewSeries("exportVsdx10Pages", 3000); string exported = ""; long vsdxBytes = 0;
        for (var i = 0; i < 5; i++)
            await rig.Time(save, async () =>
            {
                var r = await rig.Tool("export_document", new() { ["documentId"] = first.Doc, ["sessionId"] = first.Session, ["format"] = "vsdx", ["path"] = $"ten-{i}.vsdx" });
                exported = r.GetProperty("path").GetString()!; vsdxBytes = new FileInfo(exported).Length; return 0;
            });

        var openOwn = rig.NewSeries("openAppExport10Pages");
        for (var i = 0; i < 5; i++)
            await rig.Time(openOwn, async () => { var o = await h.Window.Native.OpenAsync(exported, await Expected(h), Ct()); Assert.True(o.IsOk, o.Error?.Message); return 0; });
        Assert.Equal(10, (await Snapshot(h)).GetProperty("document").GetProperty("pages").GetArrayLength());

        var openVisio = rig.NewSeries("openVisioSaved10Pages"); string visioStatus; long visioBytes = 0;
        var visioSaved = Path.Combine(rig.Work, "ten-visio-saved.vsdx");
        var visioType = Type.GetTypeFromProgID("Visio.Application");
        if (visioType is null) visioStatus = "NOT RUN: Visio is not installed";
        else
        {
            visioStatus = "PASS";
            var visioBefore = Process.GetProcessesByName("VISIO").Select(p => p.Id).ToHashSet();
            dynamic? visio = OfficeAcceptanceTests.StartVisio();
            try
            {
                if (visio is null) visioStatus = "NOT RUN: Visio could not be started";
                else
                {
                    dynamic doc = visio.Documents.Open(exported);
                    try { doc.SaveAs(visioSaved); } finally { try { doc.Close(); } catch { } }
                    visioBytes = new FileInfo(visioSaved).Length;
                }
            }
            catch (Exception e) { visioStatus = "NOT RUN: Visio failed: " + e.GetBaseException().Message; }
            finally
            {
                try { visio?.Quit(); } catch { }
                foreach (var p in Process.GetProcessesByName("VISIO").Where(p => !visioBefore.Contains(p.Id))) { try { p.Kill(); } catch { } }
            }
            if (visioStatus == "PASS")
            {
                for (var i = 0; i < 5; i++)
                    await rig.Time(openVisio, async () => { var o = await h.Window.Native.OpenAsync(visioSaved, await Expected(h), Ct()); Assert.True(o.IsOk, o.Error?.Message); return 0; });
                Assert.Equal(10, (await Snapshot(h)).GetProperty("document").GetProperty("pages").GetArrayLength());
            }
        }

        var report = rig.Environment_();
        report["scenario"] = "ten pages, 50 shapes and 5 connectors on each (550 elements), short labels";
        report["fixture"] = Node(new { pages = 10, elementsPerPage = 55, elements = 550, summaryBytesAllPages = bytesAll, vsdxBytesAppExport = vsdxBytes, vsdxBytesVisioSaved = visioBytes });
        foreach (var s in new[] { build, summaryAll, summaryOne, editFirst, editLast, renders[0], renders[4], renders[9], switchTab, save, openOwn }) report[s.Name] = Node(rig.Stats(s));
        report["openVisioSaved10Pages"] = visioStatus == "PASS" ? Node(rig.Stats(openVisio)) : visioStatus;
        report["note"] = "Edits on a page are DrawScript transactions (execute_script) because apply_operations does not name a page; their time includes compiling the script.";
        rig.Write($"scenario-ten-pages-{Cfg.ToLowerInvariant()}.json", report);

        Assert.True(Median(save.Ms) < 15000);
        Assert.True(Median(renders[9].Ms) < 5000);
        Assert.Empty(rig.Sampler.StopsJson());
    }

    // ================================================================ 2. drag and resize, frame by frame
    const string TraceScript = """
        (() => {
          const t = window.__trace = { on: false, frames: [], moves: [] };
          const ch = new MessageChannel();
          ch.port1.onmessage = e => { const m = t.moves[e.data]; if (m) m.after = performance.now(); };
          const loop = ts => { if (t.on) t.frames.push(ts); requestAnimationFrame(loop); };
          requestAnimationFrame(loop);
          document.addEventListener('pointermove', e => {
            if (!t.on) return;
            const i = t.moves.push({ ev: e.timeStamp, next: null, after: null }) - 1;
            requestAnimationFrame(ts => { t.moves[i].next = performance.now(); ch.port2.postMessage(i); });
          }, true);
          return true;
        })()
        """;

    static object Spread(IEnumerable<double> values)
    {
        var s = values.OrderBy(x => x).ToList();
        if (s.Count == 0) return new { n = 0 };
        double At(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        return new { n = s.Count, p50 = Math.Round(At(0.5), 1), p95 = Math.Round(At(0.95), 1), max = Math.Round(s[^1], 1) };
    }

    [Fact]
    public async Task DragAndResizeAreTracedFrameByFrame()
    {
        await using var rig = await StartAsync();
        var h = rig.H;
        var first = await rig.Summary();
        var shapes = Enumerable.Range(0, 460).Select(i => (object)new { op = "createShape", alias = $"n{i}", bounds = new { x = 8 + (i % 23) * 36, y = 8 + (i / 23) * 28, width = 32, height = 22 }, text = new { value = $"N{i}", fontSizePt = 7 } }).ToArray();
        var built = await rig.Tool("apply_operations", new() { ["documentId"] = first.Doc, ["sessionId"] = first.Session, ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = first.Revision, ["operations"] = JsonSerializer.SerializeToElement(shapes) });
        var rev = built.GetProperty("transaction").GetProperty("revision").GetInt32();
        var connectors = Enumerable.Range(0, 40).Select(i => (object)new { op = "createConnector", alias = $"c{i}", from = new { elementId = $"n{i}", glue = "dynamic" }, to = new { elementId = $"n{i + 23}", glue = "dynamic" } }).ToArray();
        await rig.Tool("apply_operations", new() { ["documentId"] = first.Doc, ["sessionId"] = first.Session, ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = rev, ["operations"] = JsonSerializer.SerializeToElement(connectors) });
        await h.Eval(TraceScript);
        await Task.Delay(1000);

        async Task Mouse(string type, double x, double y) => await h.OnUi(async () =>
        {
            await h.Host.FireDevToolsAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = type == "mouseMoved" ? "none" : "left", buttons = type == "mouseReleased" ? 0 : 1, clickCount = type == "mouseMoved" ? 0 : 1 }));
            return 0;
        });
        async Task<(double X, double Y)> Centre(string label)
        {
            var j = JsonNode.Parse(await h.Eval($$"""
                (() => { const t = [...document.querySelectorAll('[data-testid=canvas] svg text')].find(n => n.textContent === '{{label}}');
                  if (!t) return null; const b = t.getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; })()
                """))!;
            return (j["x"]!.GetValue<double>(), j["y"]!.GetValue<double>());
        }
        async Task<Dictionary<string, string>> Bounds()
        {
            var snap = await Snapshot(h);
            return snap.GetProperty("document").GetProperty("pages")[0].GetProperty("elements").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "shape").ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("bounds").GetRawText());
        }
        async Task<int> Revision() => (await Snapshot(h)).GetProperty("revision").GetInt32();
        var trail = new JsonArray();
        async Task Trail(string at) => trail.Add(at + ": " + JsonNode.Parse(await h.Eval("[document.querySelector('[data-testid=status-selection]')?.textContent, document.querySelector('[data-testid=status-message]')?.textContent].join(' | ')"))!.GetValue<string>());

        var gestures = new JsonObject();
        async Task Gesture(string name, int fromRevision, Func<Task> act, Func<Task<string>> verify)
        {
            await h.Eval("window.__trace.frames = []; window.__trace.moves = []; window.__trace.on = true; true");
            var started = Stopwatch.StartNew();
            await act();
            var released = Stopwatch.StartNew();
            await h.Eval("new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => { window.__trace.on = false; r(true); })))");
            var wall = started.Elapsed.TotalMilliseconds;
            double commitMs = -1;
            while (released.Elapsed.TotalSeconds < 20) { if ((await Revision()) > fromRevision) { commitMs = released.Elapsed.TotalMilliseconds; break; } await Task.Delay(25); }
            var t = JsonNode.Parse(await h.Eval("JSON.stringify({ frames: window.__trace.frames, moves: window.__trace.moves })"))!;
            var tr = JsonNode.Parse(t.GetValue<string>())!;
            var frames = tr["frames"]!.AsArray().Select(x => x!.GetValue<double>()).ToList();
            var gaps = frames.Zip(frames.Skip(1), (a, b) => b - a).ToList();
            var moves = tr["moves"]!.AsArray().Select(m => (Ev: m!["ev"]!.GetValue<double>(), Next: m["next"]?.GetValue<double>(), After: m["after"]?.GetValue<double>())).ToList();
            gestures[name] = Node(new
            {
                releaseToCommittedMs = Math.Round(commitMs), pointerMoves = moves.Count, wallMs = Math.Round(wall), frames = frames.Count, effectiveFps = Math.Round(frames.Count / (wall / 1000.0), 1),
                frameIntervalMs = Spread(gaps), frameIntervalsOver20ms = gaps.Count(g => g > 20), frameIntervalsOver33ms = gaps.Count(g => g > 33), frameIntervalsOver50ms = gaps.Count(g => g > 50),
                inputToNextFrameMs = Spread(moves.Where(m => m.Next is not null).Select(m => m.Next!.Value - m.Ev)),
                inputToAfterFrameMs = Spread(moves.Where(m => m.After is not null).Select(m => m.After!.Value - m.Ev)),
                result = await verify(),
            });
        }

        await Trail("before any gesture");
        // ---- drag one shape across 40 mouse moves, about 16 ms apart
        var before = await Bounds(); var r0 = await Revision();
        var from = await Centre("N200");
        await Gesture("dragOneShape", r0, async () =>
        {
            await Mouse("mouseMoved", from.X, from.Y); await Mouse("mousePressed", from.X, from.Y);
            for (var i = 1; i <= 40; i++) { await Mouse("mouseMoved", from.X + i * 4, from.Y + i * 2); await Task.Delay(16); }
            await Mouse("mouseReleased", from.X + 160, from.Y + 80);
        }, async () => { await Task.Delay(300); var now = await Bounds(); var moved = now.Count(kv => before.TryGetValue(kv.Key, out var b) && b != kv.Value); return $"{moved} shape(s) moved, revision {await Revision() - r0} later"; });
        Assert.True((await Revision()) > r0, "the drag committed nothing");

        await Trail("after the single drag");
        // ---- resize it from the south-east handle
        before = await Bounds(); r0 = await Revision();
        var c = await Centre("N200");
        await Mouse("mouseMoved", c.X, c.Y); await Mouse("mousePressed", c.X, c.Y); await Mouse("mouseReleased", c.X, c.Y);
        await Task.Delay(300);
        var hd = JsonNode.Parse(await h.Eval("""
            (() => { const g = [...document.querySelectorAll('[data-testid=canvas] svg g')].find(g => /se-resize/.test(g.getAttribute('style') || ''));
              const r = g?.querySelector('rect'); if (!r) return null; const b = r.getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; })()
            """))!;
        var hx = hd["x"]!.GetValue<double>(); var hy = hd["y"]!.GetValue<double>();
        await Gesture("resizeOneShape", r0, async () =>
        {
            await Mouse("mouseMoved", hx, hy); await Mouse("mousePressed", hx, hy);
            for (var i = 1; i <= 40; i++) { await Mouse("mouseMoved", hx + i * 1.5, hy + i); await Task.Delay(16); }
            await Mouse("mouseReleased", hx + 60, hy + 40);
        }, async () => { await Task.Delay(300); var now = await Bounds(); var changed = now.Count(kv => before.TryGetValue(kv.Key, out var b) && b != kv.Value); return $"{changed} shape(s) resized"; });
        Assert.True((await Revision()) > r0, "the resize committed nothing");

        await Trail("after the resize");
        // ---- select everything (Ctrl+A) and drag the whole page: all 460 shapes and 40 glued connectors move in one gesture
        var sheet = JsonNode.Parse(await h.Eval("(() => { const b = document.querySelector('[data-testid=page-sheet]').getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width }; })()"))!;
        double sx = sheet["x"]!.GetValue<double>(), sy = sheet["y"]!.GetValue<double>(), kk = sheet["w"]!.GetValue<double>() / 841.89; // pixels per point
        // (no click on the empty page first: in this harness that click armed a placement tool; the menu alone selects everything)
        await Trail("after clicking the empty page");
        await h.Eval("document.querySelector('[data-testid=menu-edit]').click(); true");
        await Task.Delay(200);
        await Trail("Edit menu open");
        await h.Eval("document.querySelector('[data-testid=menu-select-all]').click(); true"); // Edit > Select all, the menu a person would use
        await Trail("after Select all");
        await Task.Delay(500);
        var inspectorAfterBand = JsonNode.Parse(await h.Eval("[document.querySelector('[data-testid=status-selection]')?.textContent, document.querySelector('[data-testid=status-message]')?.textContent].join(' | ')"))!.GetValue<string>();
        before = await Bounds(); r0 = await Revision();
        var g0 = await Centre("N400"); // a shape with no connector on it, inside the selection
        await Gesture("dragEverythingSelectedWithCtrlA", r0, async () =>
        {
            await Mouse("mouseMoved", g0.X, g0.Y); await Mouse("mousePressed", g0.X, g0.Y);
            for (var i = 1; i <= 40; i++) { await Mouse("mouseMoved", g0.X + i * 3, g0.Y + i * 2); await Task.Delay(16); }
            await Mouse("mouseReleased", g0.X + 120, g0.Y + 80);
        }, async () => { await Task.Delay(400); var now = await Bounds(); var moved = now.Count(kv => before.TryGetValue(kv.Key, out var b) && b != kv.Value); return $"{moved} shapes moved together (460 shapes and 40 glued connectors were selected)"; });
        var groupCommitted = (await Revision()) > r0;
        var statusAfterDrag = JsonNode.Parse(await h.Eval("[document.querySelector('[data-testid=status-selection]')?.textContent, document.querySelector('[data-testid=status-message]')?.textContent].join(' | ')"))!.GetValue<string>();

        var report = rig.Environment_();
        report["statusTrail"] = trail; report["statusAfterSelectAll"] = inspectorAfterBand; report["statusAfterGroupDrag"] = statusAfterDrag;
        report["scenario"] = "drag one shape, resize one shape, and drag the whole selected page (Ctrl+A) on the 500-element page (460 shapes, 40 glued connectors), real mouse events through WebView2 about 16 ms apart";
        report["method"] = "A script in the page records requestAnimationFrame timestamps and, for each pointermove, the time from the event to the next frame callback and to the message that follows the frame (an approximation of 'painted'). It is not a photon-to-pixel measurement.";
        report["gestures"] = gestures;
        report["limits"] = "The window is offscreen, so the compositor may not be vsync-locked as on a desktop; treat the frame rate as that of this harness.";
        rig.Write($"scenario-drag-resize-{Cfg.ToLowerInvariant()}.json", report);
        Assert.True(groupCommitted, "the group drag committed nothing; status after select-all: " + inspectorAfterBand + "; after the drag: " + statusAfterDrag);
        Assert.Empty(rig.Sampler.StopsJson());
    }

    // ================================================================ 3. large pictures
    static byte[] BigPng(int seed, int w, int h)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(seed);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var n = rnd.Next(-9, 10);
                px[i] = (byte)Math.Clamp(120 + 100 * Math.Sin((x + seed * 40) / 150.0) + n, 0, 255);
                px[i + 1] = (byte)Math.Clamp(120 + 100 * Math.Cos((y + seed * 25) / 110.0) + n, 0, 255);
                px[i + 2] = (byte)Math.Clamp(((x + y) / 30 + seed * 20) % 256 + n, 0, 255);
                px[i + 3] = 255;
            }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    [Fact]
    public async Task EightLargePicturesOnOnePageAndTheExportLimit()
    {
        await using var rig = await StartAsync();
        var h = rig.H;
        const int Count = 8, W = 2400, Hh = 1600;
        var prepare = rig.NewSeries("prepareAndImportToLibrary");
        var sizes = new List<long>(); var names = new List<string>();
        for (var i = 1; i <= Count; i++)
        {
            var bytes = BigPng(i, W, Hh); sizes.Add(bytes.Length);
            var name = $"pic{i:00}.png"; names.Add($"asset:pic{i:00}");
            await rig.Time(prepare, async () =>
            {
                var p = await h.Window.Preparer.PrepareAsync(new MemoryStream(bytes), "image/png", name, Ct());
                Assert.True(p.IsOk, p.Error?.Message);
                Assert.True((await h.Window.Library.ImportAsync(p.Value, ["perf"], Ct())).IsOk);
                return 0;
            });
        }

        var first = await rig.Summary();
        var script = new StringBuilder();
        foreach (var n in names) script.AppendLine($"asset import prepared=\"{n}\"");
        for (var i = 0; i < Count; i++) script.AppendLine($"add image id=img{i:00} asset=\"{names[i]}\" x={20 + (i % 4) * 200}pt y={20 + (i / 4) * 160}pt w=190pt h=127pt");
        script.AppendLine("add shape id=caption x=20pt y=520pt w=300pt h=40pt text=\"Eight 2400 x 1600 pictures\"");
        var place = rig.NewSeries("placeEightPicturesInOneScript");
        var placed = await rig.Time(place, () => rig.Script(first.Doc, first.Session, first.Revision, script.ToString(), [.. names]));
        var painted = rig.NewSeries("pictureDecodeAndDrawInEditor");
        await rig.Time(painted, async () =>
        {
            for (var i = 0; i < 400; i++)
            {
                var ok = JsonNode.Parse(await h.Eval("""
                    (async () => { await new Promise(r => requestAnimationFrame(r));
                      const imgs = [...document.querySelectorAll('[data-testid=canvas] svg image')];
                      if (imgs.length < 8) return false;
                      const sources = new Set(imgs.map(i => i.getAttribute('href') || i.getAttributeNS('http://www.w3.org/1999/xlink', 'href')));
                      const loaded = await Promise.all([...sources].map(s => new Promise(res => { const im = new Image(); im.onload = () => res(true); im.onerror = () => res(false); im.src = s; })));
                      return loaded.every(Boolean); })()
                    """))!.GetValue<bool>();
                if (ok) return 0;
                await Task.Delay(50);
            }
            throw new TimeoutException("the pictures never finished loading");
        });

        var after = await rig.Summary();
        var bytesTotal = sizes.Sum();
        var summary = rig.NewSeries("summary", 250); var render = rig.NewSeries("renderPage1600x1200", 1000); var layout = rig.NewSeries("inspectLayout"); var save = rig.NewSeries("exportVsdx", 3000); var open = rig.NewSeries("openAppExportWithPictures");
        string exported = ""; long vsdxBytes = 0;
        for (var i = 0; i < 5; i++) await rig.Time(summary, async () => { await rig.Summary(); return 0; });
        long pngBytes = 0;
        for (var i = 0; i < 5; i++)
            await rig.Time(render, async () =>
            {
                var r = await rig.Client.CallToolAsync("render_page", new Dictionary<string, object?> { ["documentId"] = after.Doc, ["sessionId"] = after.Session, ["pageId"] = after.Body.GetProperty("pages")[0].GetProperty("id").GetString(), ["maxWidth"] = 1600, ["maxHeight"] = 1200 }, cancellationToken: Ct());
                Assert.True(r.IsError != true, r.StructuredContent?.ToString());
                pngBytes = r.Content.OfType<ImageContentBlock>().Single().DecodedData.Length;
                Assert.True(pngBytes > 50_000, "the preview should show the pictures");
                return 0;
            });
        for (var i = 0; i < 3; i++) await rig.Time(layout, async () => { await rig.Tool("inspect_layout", new() { ["documentId"] = after.Doc, ["sessionId"] = after.Session }); return 0; });
        for (var i = 0; i < 3; i++)
            await rig.Time(save, async () =>
            {
                var r = await rig.Tool("export_document", new() { ["documentId"] = after.Doc, ["sessionId"] = after.Session, ["format"] = "vsdx", ["path"] = $"pics-{i}.vsdx" });
                exported = r.GetProperty("path").GetString()!; vsdxBytes = new FileInfo(exported).Length; return 0;
            });
        for (var i = 0; i < 3; i++)
            await rig.Time(open, async () => { var o = await h.Window.Native.OpenAsync(exported, await Expected(h), Ct()); Assert.True(o.IsOk, o.Error?.Message); return 0; });
        var reopened = await Snapshot(h);
        Assert.Equal(Count, reopened.GetProperty("document").GetProperty("pages")[0].GetProperty("elements").EnumerateArray().Count(e => e.GetProperty("kind").GetString() == "image"));

        // ---- past the export limit: four more pictures take the package over the 64 MiB read-back budget (InputLimits.NativeJsonBytes)
        await rig.Summary();
        var fresh = await rig.Summary();
        var more = new List<string>(); var moreScript = new StringBuilder(); long moreBytes = 0;
        for (var i = Count + 1; i <= Count + 4; i++)
        {
            var bytes = BigPng(i, W, Hh); moreBytes += bytes.Length;
            var p = await h.Window.Preparer.PrepareAsync(new MemoryStream(bytes), "image/png", $"pic{i:00}.png", Ct());
            Assert.True(p.IsOk && (await h.Window.Library.ImportAsync(p.Value, ["perf"], Ct())).IsOk);
            more.Add($"asset:pic{i:00}");
            moreScript.AppendLine($"asset import prepared=\"asset:pic{i:00}\"");
            moreScript.AppendLine($"add image id=img{i:00} asset=\"asset:pic{i:00}\" x={20 + ((i - Count - 1) % 4) * 200}pt y=360pt w=190pt h=127pt");
        }
        await rig.Script(fresh.Doc, fresh.Session, fresh.Revision, moreScript.ToString(), [.. more]);
        var limitSw = Stopwatch.StartNew();
        var refused = await rig.Client.CallToolAsync("export_document", new Dictionary<string, object?> { ["documentId"] = fresh.Doc, ["sessionId"] = fresh.Session, ["format"] = "vsdx", ["path"] = "beyond-limit.vsdx" }, cancellationToken: Ct());
        var limitMs = limitSw.Elapsed.TotalMilliseconds;
        var refusal = refused.IsError == true ? Body(refused).GetProperty("error").Clone() : default;
        var leftBehind = Directory.Exists(Path.Combine(rig.Work, "exports")) && Directory.GetFiles(Path.Combine(rig.Work, "exports"), "beyond-limit*").Length > 0;

        var report = rig.Environment_();
        report["exportLimitProbe"] = Node(new
        {
            picturesInDocument = Count + 4, pngBytesInDocument = bytesTotal + moreBytes, refused = refused.IsError == true, errorCode = refused.IsError == true ? refusal.GetProperty("code").GetString() : null,
            errorMessage = refused.IsError == true ? refusal.GetProperty("message").GetString() : null, msToRefusal = Math.Round(limitMs), fileLeftBehind = leftBehind,
            note = "A document whose pictures add up to more than the 64 MiB the exporter will read back cannot be saved as .vsdx; the application refuses and writes nothing. Native JSON has the same 64 MiB budget.",
        });
        report["scenario"] = $"{Count} PNG pictures of {W} x {Hh} pixels (pseudo-photographic, seeded noise), imported to the library, placed by one script on one A4 page, plus a caption";
        report["fixture"] = Node(new { pictures = Count, pixelsEach = $"{W} x {Hh}", pngBytesEach = sizes, pngBytesTotal = bytesTotal, previewBytes = pngBytes, vsdxBytes });
        foreach (var s in new[] { prepare, place, painted, summary, render, layout, save, open }) report[s.Name] = Node(rig.Stats(s));
        report["limits"] = "Pictures are generated, not photographs; their compression ratio differs from real photos. Memory use of the editor was not measured.";
        rig.Write($"scenario-large-pictures-{Cfg.ToLowerInvariant()}.json", report);
        Assert.Empty(rig.Sampler.StopsJson());
    }

    // ================================================================ 4. text-heavy pages
    static string Paragraph(int seed, int chars)
    {
        string[] words = ["integration", "service", "customer", "billing", "ledger", "release", "environment", "pipeline", "approval", "deployment", "record", "archive", "gateway", "queue", "schedule", "monitor"];
        var rnd = new Random(seed); var sb = new StringBuilder();
        while (sb.Length < chars) sb.Append(words[rnd.Next(words.Length)]).Append(' ');
        return sb.ToString(0, chars).TrimEnd();
    }

    [Fact]
    public async Task FiveTextHeavyPages()
    {
        await using var rig = await StartAsync();
        var h = rig.H;
        var first = await rig.Summary();
        var script = new StringBuilder();
        var chars = 0;
        for (var p = 1; p <= 5; p++)
        {
            if (p > 1) { script.AppendLine($"page add \"Notes {p}\""); script.AppendLine($"page use \"Notes {p}\""); }
            for (var i = 0; i < 40; i++)
            {
                var text = Paragraph(p * 100 + i, 300); chars += text.Length;
                script.AppendLine($"add text id=t{p}x{i} x={16 + (i % 4) * 205}pt y={16 + (i / 4) * 52}pt w=198pt h=48pt text=\"{text}\" fontSize=7pt wrap=true");
            }
            for (var i = 0; i < 20; i++)
            {
                var text = Paragraph(p * 100 + 50 + i, 60); chars += text.Length;
                script.AppendLine($"add shape id=s{p}x{i} x={16 + (i % 5) * 165}pt y={540 - (i / 5) * 1}pt w=160pt h=40pt text=\"{text}\" fontSize=9pt");
            }
        }
        var build = rig.NewSeries("buildFivePages");
        await rig.Time(build, () => rig.Script(first.Doc, first.Session, first.Revision, script.ToString()));
        var after = await rig.Summary();
        var pages = after.Body.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal(5, pages.Count);
        Assert.All(pages, p => Assert.Equal(60, p.GetProperty("elements").GetArrayLength()));
        var page1 = pages[0].GetProperty("id").GetString()!;
        var textIds = pages[0].GetProperty("elements").EnumerateArray().Take(50).Select(e => e.GetProperty("id").GetString()!).ToArray();

        var summary = rig.NewSeries("summary", 250); var layout = rig.NewSeries("inspectLayout"); var textLayout = rig.NewSeries("getTextLayoutFiftyElements"); var render = rig.NewSeries("renderPage1600x1200", 1000); var save = rig.NewSeries("exportVsdx", 3000); var open = rig.NewSeries("openAppExport");
        int summaryBytes = 0, issues = 0; string exported = ""; long vsdxBytes = 0;
        for (var i = 0; i < 5; i++) await rig.Time(summary, async () => { var r = await rig.Client.CallToolAsync("get_document_summary", new Dictionary<string, object?> { ["includeGeometry"] = true }, cancellationToken: Ct()); summaryBytes = Body(r).GetRawText().Length; return 0; });
        for (var i = 0; i < 5; i++) await rig.Time(layout, async () => { var r = await rig.Tool("inspect_layout", new() { ["documentId"] = after.Doc, ["sessionId"] = after.Session }); issues = r.GetProperty("issues").GetArrayLength(); return 0; });
        for (var i = 0; i < 5; i++) await rig.Time(textLayout, async () => { await rig.Tool("get_text_layout", new() { ["documentId"] = after.Doc, ["sessionId"] = after.Session, ["ids"] = textIds, ["target"] = "visio" }); return 0; });
        for (var i = 0; i < 5; i++)
            await rig.Time(render, async () =>
            {
                var r = await rig.Client.CallToolAsync("render_page", new Dictionary<string, object?> { ["documentId"] = after.Doc, ["sessionId"] = after.Session, ["pageId"] = page1, ["maxWidth"] = 1600, ["maxHeight"] = 1200 }, cancellationToken: Ct());
                Assert.True(r.IsError != true, r.StructuredContent?.ToString()); return 0;
            });
        for (var i = 0; i < 3; i++)
            await rig.Time(save, async () =>
            {
                var r = await rig.Tool("export_document", new() { ["documentId"] = after.Doc, ["sessionId"] = after.Session, ["format"] = "vsdx", ["path"] = $"text-{i}.vsdx", ["acceptLoss"] = true });
                exported = r.GetProperty("path").GetString()!; vsdxBytes = new FileInfo(exported).Length; return 0;
            });
        for (var i = 0; i < 3; i++)
            await rig.Time(open, async () => { var o = await h.Window.Native.OpenAsync(exported, await Expected(h), Ct()); Assert.True(o.IsOk, o.Error?.Message); return 0; });
        Assert.Equal(5, (await Snapshot(h)).GetProperty("document").GetProperty("pages").GetArrayLength());

        var report = rig.Environment_();
        report["scenario"] = "five pages, each with 40 wrapped text boxes of 300 characters (7 pt, 198 x 48 pt) and 20 shapes labelled with 60 characters (9 pt)";
        report["fixture"] = Node(new { pages = 5, textBoxes = 200, labelledShapes = 100, totalCharacters = chars, summaryBytes, layoutIssuesReported = issues, vsdxBytes });
        foreach (var s in new[] { build, summary, layout, textLayout, render, save, open }) report[s.Name] = Node(rig.Stats(s));
        report["limits"] = "The shapes at the bottom of each page overlap by construction, so inspect_layout reports many issues; that is the workload, not a defect. Words are drawn from a 16-word list.";
        rig.Write($"scenario-text-heavy-{Cfg.ToLowerInvariant()}.json", report);
        Assert.Empty(rig.Sampler.StopsJson());
    }

    static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}
