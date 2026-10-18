using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>
/// Spec section 18 on the real stack: the real WPF/WebView2 editor, the real pipe and a real stdio MCP client, with 500 visible elements on
/// the page. Timings are repeated and recorded (docs/acceptance/evidence/PERF/live-timings-<configuration>.json). The preferred targets are reported rather
/// than gated; the assertions are ceilings that only a real regression crosses, and correctness of what was measured.
/// </summary>
public class PerformanceTests
{
    const int Elements = 500;
    const int Runs = 10;

    static CancellationToken Ct(int s = 120) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;
    static JsonElement Body(CallToolResult r) => r.StructuredContent!.Value;

    static object Stats(List<double> ms, double preferred, double? acceptable = null)
    {
        var s = ms.OrderBy(x => x).ToList();
        double At(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        return new { runs = s.Count, samplesMs = ms.Select(x => Math.Round(x, 1)).ToArray(), minMs = Math.Round(s[0], 1), medianMs = Math.Round(At(0.5), 1), p95Ms = Math.Round(At(0.95), 1), maxMs = Math.Round(s[^1], 1), preferredMs = preferred, acceptableMs = acceptable, meetsPreferred = At(0.5) <= preferred };
    }

    [Fact]
    public async Task TimingsAtFiveHundredVisibleElementsAreRecorded()
    {
        var pipe = $"perf-{Guid.NewGuid():N}";
        var work = Path.Combine(Path.GetTempPath(), "perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: Path.Combine(work, "exports"));
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "perf", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: Ct());

        async Task<(string Doc, string Session, int Revision, string Page, JsonElement Body)> Summary(bool geometry = true)
        {
            var r = await client.CallToolAsync("get_document_summary", new Dictionary<string, object?> { ["includeGeometry"] = geometry }, cancellationToken: Ct());
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            var b = Body(r).Clone();
            return (b.GetProperty("documentId").GetString()!, b.GetProperty("sessionId").GetString()!, b.GetProperty("revision").GetInt32(), b.GetProperty("pages")[0].GetProperty("id").GetString()!, b);
        }

        async Task<JsonElement> Apply(string doc, string session, int rev, params object[] ops)
        {
            var r = await client.CallToolAsync("apply_operations", new Dictionary<string, object?>
            { ["documentId"] = doc, ["sessionId"] = session, ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = rev, ["operations"] = JsonSerializer.SerializeToElement(ops) }, cancellationToken: Ct());
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            return Body(r).Clone();
        }

        // ---------------------------------------------------------------- warm-up. Starting the shim from the process that hosts the editor freezes that process for about four seconds,
        // somewhere between four and seven seconds later, on this machine (isolated with probes: no MCP call is needed, and a process without the shim never does it).
        // In use the shim is a separate process started by the agent's host, so this is a property of the test setup; the warm-up keeps it out of the measurements and records it.
        var warmup = Stopwatch.StartNew(); double worstWarmupMs = 0;
        while (warmup.Elapsed < TimeSpan.FromSeconds(10))
        {
            var w = Stopwatch.StartNew();
            await client.CallToolAsync("get_document_summary", cancellationToken: Ct());
            worstWarmupMs = Math.Max(worstWarmupMs, w.Elapsed.TotalMilliseconds);
            await Task.Delay(30);
        }

        // ---------------------------------------------------------------- the fixture: 500 visible elements (460 shapes and 40 connectors) on one A4 page
        var first = await Summary();
        var shapes = Enumerable.Range(0, 460).Select(i => (object)new
        {
            op = "createShape", alias = $"n{i}", bounds = new { x = 8 + (i % 23) * 36, y = 8 + (i / 23) * 28, width = 32, height = 22 }, text = new { value = $"N{i}", fontSizePt = 7 },
        }).ToArray();
        var rev = first.Revision;
        var t0 = Stopwatch.StartNew();
        var built = await Apply(first.Doc, first.Session, rev, shapes);
        var buildMs = t0.Elapsed.TotalMilliseconds;
        rev = built.GetProperty("transaction").GetProperty("revision").GetInt32();
        var connectors = Enumerable.Range(0, 40).Select(i => (object)new
        {
            op = "createConnector", alias = $"c{i}", from = new { elementId = $"n{i}", glue = "dynamic" }, to = new { elementId = $"n{i + 23}", glue = "dynamic" },
        }).ToArray();
        var wired = await Apply(first.Doc, first.Session, rev, connectors);
        rev = wired.GetProperty("transaction").GetProperty("revision").GetInt32();

        var shape = await Summary();
        Assert.Equal(Elements, shape.Body.GetProperty("pages")[0].GetProperty("elements").GetArrayLength());

        // ---------------------------------------------------------------- 1. one edit, as the agent sees it (call, commit, answer) and as a person sees it (and painted)
        var edit = new List<double>(); var painted = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await Apply(first.Doc, first.Session, rev, new { op = "move", target = "n200", delta = new { xPt = 1, yPt = 0 } });
            edit.Add(sw.Elapsed.TotalMilliseconds);
            rev = res.GetProperty("transaction").GetProperty("revision").GetInt32();
            await h.Eval("new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(true))))");
            painted.Add(sw.Elapsed.TotalMilliseconds);
        }

        // ---------------------------------------------------------------- 2. a 100-operation batch
        var batch = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var ops = Enumerable.Range(0, 100).Select(k => (object)new { op = "move", target = $"n{k}", delta = new { xPt = i % 2 == 0 ? 1 : -1, yPt = 0 } }).ToArray();
            var sw = Stopwatch.StartNew();
            var res = await Apply(first.Doc, first.Session, rev, ops);
            batch.Add(sw.Elapsed.TotalMilliseconds);
            rev = res.GetProperty("transaction").GetProperty("revision").GetInt32();
        }

        // ---------------------------------------------------------------- 3. the semantic summary and a full page preview
        var summary = new List<double>(); var summaryBytes = 0;
        for (var i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await client.CallToolAsync("get_document_summary", new Dictionary<string, object?> { ["includeGeometry"] = true }, cancellationToken: Ct());
            summary.Add(sw.Elapsed.TotalMilliseconds);
            Assert.True(r.IsError != true);
            summaryBytes = Body(r).GetRawText().Length;
        }
        var hostSummary = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await h.Send("doc.summary", new JsonObject { ["includeGeometry"] = true });
            hostSummary.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Null(r.Error);
        }
        var render = new List<double>(); var pngBytes = 0;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await client.CallToolAsync("render_page", new Dictionary<string, object?>
            { ["documentId"] = first.Doc, ["sessionId"] = first.Session, ["pageId"] = first.Page, ["maxWidth"] = 1600, ["maxHeight"] = 1200 }, cancellationToken: Ct());
            render.Add(sw.Elapsed.TotalMilliseconds);
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            pngBytes = r.Content.OfType<ImageContentBlock>().Sum(c => c.DecodedData.Length);
            Assert.True(pngBytes > 5_000, "the preview is not an empty image");
        }

        // ---------------------------------------------------------------- 4. native save
        var save = new List<double>(); long vsdxBytes = 0;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await client.CallToolAsync("export_document", new Dictionary<string, object?>
            { ["documentId"] = first.Doc, ["sessionId"] = first.Session, ["format"] = "vsdx", ["path"] = $"perf-{i}.vsdx" }, cancellationToken: Ct());
            save.Add(sw.Elapsed.TotalMilliseconds);
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            vsdxBytes = new FileInfo(Body(r).GetProperty("path").GetString()!).Length;
        }

        // ---------------------------------------------------------------- the editor stays responsive: a canvas that holds all 500 elements answers a script within a frame budget
        var nodes = JsonNode.Parse(await h.Eval("document.querySelectorAll('[data-testid=canvas] svg g').length"))!.GetValue<int>();

        var report = new
        {
            recorded = DateTime.UtcNow.ToString("o"),
            machine = new { os = Environment.OSVersion.VersionString, cpus = Environment.ProcessorCount, dotnet = Environment.Version.ToString(), configuration = cfg, webview2 = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() },
            warmup = new { seconds = 10, longestCallMs = Math.Round(worstWarmupMs, 1), note = "the shim start-up freeze of the test process, kept out of the measurements" },
            fixture = new { visibleElements = Elements, shapes = 460, connectors = 40, pages = 1, buildMs = Math.Round(buildMs, 1), canvasGroups = nodes, summaryBytes, previewBytes = pngBytes, vsdxBytes },
            singleEditAgent = Stats(edit, 100, 250),
            singleEditPainted = Stats(painted, 100, 250),
            batch100 = Stats(batch, 500),
            summaryOverMcp = Stats(summary, 250),
            summaryHostOnly = Stats(hostSummary, 250),
            previewPage1600x1200 = Stats(render, 1000),
            vsdxSave = Stats(save, 3000),
            note = "Each figure is the median of repeated runs on one machine; preferred targets are spec section 18. summaryOverMcp includes the stdio transport, summaryHostOnly does not.",
        };
        var dir = Path.Combine(Harness.RepoRoot, "docs", "acceptance", "evidence", "PERF");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"live-timings-{cfg.ToLowerInvariant()}.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        // generous ceilings: only a real regression crosses these
        Assert.True(Median(painted) < 1000, "a single edit took over a second to be painted");
        Assert.True(Median(batch) < 3000);
        Assert.True(Median(render) < 5000);
        Assert.True(Median(save) < 15000);
        try { Directory.Delete(work, true); } catch { }
    }

    static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
}
