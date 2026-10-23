using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>
/// The live stack for performance work: the editor, the pipe, the shim and an MCP client, started in the order A05 found to keep the process's own
/// start-up cost out of the timings (shim first, then the editor, then a quiet gate), with every timed run checked against thread-pool starvation.
/// </summary>
sealed class PerfRig : IAsyncDisposable
{
    public static string Cfg => AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
    public static CancellationToken Ct(int s = 180) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;
    public static JsonElement Body(CallToolResult r) => r.StructuredContent!.Value;

    /// <summary>One measurement repeated, with the interval of the sampler's clock each run covered.</summary>
    public sealed class Series(string name, double? preferredMs = null)
    {
        public string Name { get; } = name;
        public double? PreferredMs { get; } = preferredMs;
        public List<double> Ms { get; } = [];
        public List<(double From, double To)> Spans { get; } = [];
    }

    public PoolSampler Sampler { get; } = new();
    public Harness H { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;
    public string Work { get; } = Path.Combine(Path.GetTempPath(), "perf-" + Guid.NewGuid().ToString("N"));
    public bool QuietGateReached { get; private set; }
    public double? QuietGateWaitedMs { get; private set; }
    readonly List<Series> all = [];

    public static async Task<PerfRig> StartAsync()
    {
        var rig = new PerfRig();
        Directory.CreateDirectory(rig.Work);
        var pipe = $"perf-{Guid.NewGuid():N}";
        rig.Client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "perf", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", Cfg, "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: Ct());
        rig.H = await Harness.StartAsync(pipe: pipe, exportRoot: Path.Combine(rig.Work, "exports"));
        var settled = await rig.Sampler.WaitQuietAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
        rig.QuietGateReached = settled is not null;
        rig.QuietGateWaitedMs = settled is { } w ? Math.Round(w.TotalMilliseconds) : null;
        return rig;
    }

    public double Now => Sampler.Clock.Elapsed.TotalMilliseconds;

    public Series NewSeries(string name, double? preferredMs = null) { var s = new Series(name, preferredMs); all.Add(s); return s; }

    /// <summary>Runs <paramref name="work"/> and records how long it took on <paramref name="series"/>.</summary>
    public async Task<T> Time<T>(Series series, Func<Task<T>> work)
    {
        var from = Now; var sw = Stopwatch.StartNew();
        var result = await work();
        series.Ms.Add(sw.Elapsed.TotalMilliseconds); series.Spans.Add((from, Now));
        return result;
    }

    static object Plain(List<double> ms, double? preferred)
    {
        var s = ms.OrderBy(x => x).ToList();
        double At(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        return new { runs = s.Count, samplesMs = ms.Select(x => Math.Round(x, 1)).ToArray(), minMs = Math.Round(s[0], 1), medianMs = Math.Round(At(0.5), 1), p95Ms = Math.Round(At(0.95), 1), maxMs = Math.Round(s[^1], 1), preferredMs = preferred, meetsPreferred = preferred is null ? (bool?)null : At(0.5) <= preferred };
    }

    public object Stats(Series s)
    {
        if (s.Ms.Count == 0) return new { runs = 0 };
        var hit = s.Spans.Select(x => Sampler.StarvedBetween(x.From, x.To)).ToList();
        var clean = s.Ms.Where((_, i) => !hit[i]).ToList();
        return new { all = Plain(s.Ms, s.PreferredMs), contaminatedRuns = hit.Count(x => x), clean = clean.Count > 0 ? Plain(clean, s.PreferredMs) : null };
    }

    public JsonObject Environment_() => new()
    {
        ["recorded"] = DateTime.UtcNow.ToString("o"),
        ["machine"] = new JsonObject
        {
            ["os"] = System.Environment.OSVersion.VersionString, ["logicalProcessors"] = System.Environment.ProcessorCount, ["dotnet"] = System.Environment.Version.ToString(), ["configuration"] = Cfg,
            ["webview2"] = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(),
        },
        ["setup"] = new JsonObject
        {
            ["shimStartedBeforeEditor"] = true, ["quietGateReached"] = QuietGateReached, ["quietGateWaitedMs"] = QuietGateWaitedMs, ["machineCpuBusyPercentOverall"] = Sampler.OverallBusyPercent(),
            ["starvedStretchesSeen"] = Sampler.StarvationJson(), ["wholeProcessStops"] = Sampler.StopsJson(),
        },
    };

    public void Write(string file, JsonObject report)
    {
        var dir = Path.Combine(Harness.RepoRoot, "docs", "acceptance", "evidence", "PERF");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static JsonNode Node(object o) => JsonSerializer.SerializeToNode(o)!;

    // ---- MCP helpers
    public async Task<(string Doc, string Session, int Revision, JsonElement Body)> Summary(Dictionary<string, object?>? args = null)
    {
        var r = await Client.CallToolAsync("get_document_summary", args ?? [], cancellationToken: Ct());
        Assert.True(r.IsError != true, r.StructuredContent?.ToString());
        var b = Body(r).Clone();
        return (b.GetProperty("documentId").GetString()!, b.GetProperty("sessionId").GetString()!, b.GetProperty("revision").GetInt32(), b);
    }

    public async Task<JsonElement> Tool(string name, Dictionary<string, object?> args)
    {
        var r = await Client.CallToolAsync(name, args, cancellationToken: Ct());
        Assert.True(r.IsError != true, name + ": " + r.StructuredContent?.ToString());
        return Body(r).Clone();
    }

    public async Task<JsonElement> Script(string doc, string session, int revision, string script, params string[] library) =>
        await Tool("execute_script", new()
        {
            ["documentId"] = doc, ["sessionId"] = session, ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = revision, ["script"] = script,
            ["libraryAssets"] = library,
        });

    public async ValueTask DisposeAsync()
    {
        Sampler.Dispose();
        if (H is not null) await H.DisposeAsync();
        if (Client is not null) await Client.DisposeAsync();
        try { Directory.Delete(Work, true); } catch { }
    }
}
