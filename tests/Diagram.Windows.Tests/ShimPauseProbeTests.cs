using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Ipc;
using ModelContextProtocol.Client;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>Runs only when DIAGRAM_RUN_DIAGNOSTICS=1: these probes take about thirty seconds each and exist to explain a measurement, not to guard behaviour.</summary>
public sealed class DiagnosticFactAttribute : FactAttribute
{
    public DiagnosticFactAttribute() { if (Environment.GetEnvironmentVariable("DIAGRAM_RUN_DIAGNOSTICS") != "1") Skip = "diagnostic probe: set DIAGRAM_RUN_DIAGNOSTICS=1 to run"; }
}

/// <summary>
/// A05 (#18). I52 saw the whole test process stop for about four seconds, four to seven seconds after it started the shim. These probes vary one
/// thing at a time (what is started, who starts it, whether the editor is in the process) and record, from a dedicated non-pool thread, what the
/// runtime was doing across any stop: garbage-collection pause time, collections, pool size and CPU time. A stop of that thread means the whole process
/// was suspended; a stop of pool timers alone would mean the pool was starved.
/// Evidence: docs/acceptance/evidence/A05/*.json.
/// </summary>
public class ShimPauseProbeTests
{
    static int Seconds => int.TryParse(Environment.GetEnvironmentVariable("DIAGRAM_PROBE_SECONDS"), out var n) ? n : 28;
    static int Repeats => int.TryParse(Environment.GetEnvironmentVariable("DIAGRAM_PROBE_REPEAT"), out var n) ? n : 1;
    const double GapMs = PoolSampler.GapMs;
    static string Out => Path.Combine(Harness.RepoRoot, "docs", "acceptance", "evidence", "A05");
    static string Cfg => AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
    static string ShimDll => Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", Cfg, "net10.0", "Diagram.Mcp.dll");

    sealed record Scenario(string Name, string Question, bool Editor, Func<Ctx, Task<IAsyncDisposable?>> Spawn, bool StandaloneApp = false);
    sealed class Ctx { public required string Pipe; public required string Marker; public Harness? Harness; }

    sealed class Handle(Action stop) : IAsyncDisposable { public ValueTask DisposeAsync() { try { stop(); } catch { } return ValueTask.CompletedTask; } }

    static async Task<IAsyncDisposable?> SdkShim(Ctx c)
    {
        var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        { Name = "probe", Command = "dotnet", Arguments = [ShimDll, "--probe=" + c.Marker], EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = c.Pipe } }));
        return client;
    }

    static Task<IAsyncDisposable?> RawChild(string file, string args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return Task.FromResult<IAsyncDisposable?>(new Handle(() => { if (!p.HasExited) p.Kill(true); }));
    }

    /// <summary>Starts the shim from another process (as an agent's host would) and returns at once; the shim is found again by a marker argument.</summary>
    static Task<IAsyncDisposable?> ShimViaHelper(Ctx c)
    {
        var script = $"Start-Process -FilePath dotnet -ArgumentList '\"{ShimDll}\"','--probe={c.Marker}' -WindowStyle Hidden";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"$env:DIAGRAM_PIPE='{c.Pipe}'; {script.Replace("\"", "\\\"")}\"") { UseShellExecute = false, CreateNoWindow = true };
        Process.Start(psi)!.WaitForExit();
        return Task.FromResult<IAsyncDisposable?>(new Handle(() => KillByMarker(c.Marker)));
    }

    static void KillByMarker(string marker)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='dotnet.exe'\\\" | Where-Object {{ $_.CommandLine -like '*--probe={marker}*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}\"") { UseShellExecute = false, CreateNoWindow = true };
        Process.Start(psi)!.WaitForExit();
    }

    static readonly Scenario[] Scenarios =
    [
        new("s1-editor-only", "Control: the editor in the process and nothing started.", true, _ => Task.FromResult<IAsyncDisposable?>(null)),
        new("s2-editor-sdk-shim", "I52's setup: editor in the process, shim started through the MCP client library.", true, SdkShim),
        new("s3-editor-raw-shim", "Same, but the shim is started with plain Process.Start and no MCP library.", true, c => RawChild("dotnet", $"\"{ShimDll}\" --probe={c.Marker}", new() { ["DIAGRAM_PIPE"] = c.Pipe })),
        new("s4-editor-cmd-child", "Editor in the process; the child is not .NET at all (cmd running ping).", true, _ => RawChild("cmd.exe", "/c ping -n 40 127.0.0.1 >nul")),
        new("s5-bare-sdk-shim", "No editor in the process; the shim is started through the MCP client library.", false, SdkShim),
        new("s6-bare-raw-shim", "No editor; the shim is started with plain Process.Start.", false, c => RawChild("dotnet", $"\"{ShimDll}\" --probe={c.Marker}", new() { ["DIAGRAM_PIPE"] = c.Pipe })),
        new("s7-bare-cmd-child", "No editor; a non-.NET child.", false, _ => RawChild("cmd.exe", "/c ping -n 40 127.0.0.1 >nul")),
        new("s8-editor-shim-from-other-process", "Editor in the process; the shim is started by a separate PowerShell process, the way an agent host would.", true, ShimViaHelper),
        new("s10-editor-dotnet-version", "Editor in the process; a short-lived .NET child (dotnet --version) that exits within a fraction of a second.", true, _ => RawChild("dotnet", "--version")),
        new("s11-editor-apphost-shim", "Editor in the process; the shim started through its native launcher Diagram.Mcp.exe instead of the dotnet host.", true, c => RawChild(Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", Cfg, "net10.0", "Diagram.Mcp.exe"), "--probe=" + c.Marker, new() { ["DIAGRAM_PIPE"] = c.Pipe })),
        new("s12-editor-cpu-burner", "Editor in the process; the child is a PowerShell that keeps every logical processor busy for three seconds, so machine-wide CPU saturation is the only thing it does.", true, _ => RawChild("powershell.exe", "-NoProfile -Command \"1..([Environment]::ProcessorCount) | ForEach-Object { Start-Job { $e=[DateTime]::Now.AddSeconds(3); while([DateTime]::Now -lt $e){} } } | Wait-Job | Out-Null\"")),
        new("s13-bare-cpu-burner", "No editor; the same CPU-saturating child.", false, _ => RawChild("powershell.exe", "-NoProfile -Command \"1..([Environment]::ProcessorCount) | ForEach-Object { Start-Job { $e=[DateTime]::Now.AddSeconds(3); while([DateTime]::Now -lt $e){} } } | Wait-Job | Out-Null\"")),
        new("s9-standalone-app", "Production topology: the real Diagram.App.exe in its own process, the shim started from another process; the app is observed through its pipe.", false, SdkShim, StandaloneApp: true),
    ];

    async Task Run(Scenario s)
    {
        for (var i = 1; i <= Repeats; i++) await RunOnce(s, Repeats > 1 ? $"{s.Name}.r{i}" : s.Name);
    }

    async Task RunOnce(Scenario s, string file)
    {
        Directory.CreateDirectory(Out);
        var marker = Guid.NewGuid().ToString("N")[..12];
        var pipe = $"a05-{Guid.NewGuid():N}";
        Harness? h = null;
        Process? app = null;
        var uiLate = new JsonArray(); var poolLate = new JsonArray(); var pipeSlow = new JsonArray();
        double spawnedAtMs = -1;
        try
        {
            if (s.StandaloneApp)
            {
                if (Process.GetProcessesByName("Diagram.App").Length > 0) { File.WriteAllText(Path.Combine(Out, file + ".json"), "{\"status\":\"NOT RUN\",\"reason\":\"a Diagram.App is already running for this user\"}"); return; }
                var exe = Path.Combine(Harness.RepoRoot, "src", "Diagram.App", "bin", Cfg, "net10.0-windows", "Diagram.App.exe");
                app = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized })!;
                pipe = PipeNames.For();
            }
            else if (s.Editor) h = await Harness.StartAsync(pipe: pipe);

            await using var raw = new PipeClient(pipe, TimeSpan.FromSeconds(2), "a05-probe");
            if (s.StandaloneApp)
            {
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (true)
                {
                    var r = await raw.CallAsync(new RequestEnvelope { RequestId = "w", Method = "doc.summary", Params = new JsonObject() }, default);
                    if (r.Error is null) break;
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("the standalone app never answered: " + r.Error.Code + " " + r.Error.Message);
                    await Task.Delay(500);
                }
            }

            using var sampler = new PoolSampler();
            var stop = false;
            var tasks = new List<Task>
            {
                Task.Run(async () => { while (!stop) { var w = Stopwatch.StartNew(); await Task.Delay(20); if (w.ElapsedMilliseconds > GapMs) poolLate.Add(new JsonObject { ["atMs"] = Math.Round(sampler.Clock.Elapsed.TotalMilliseconds), ["lateMs"] = w.ElapsedMilliseconds }); } }),
            };
            if (h is not null) tasks.Add(Task.Run(async () => { while (!stop) { var w = Stopwatch.StartNew(); await h.Ui.InvokeAsync(() => 0).Task; if (w.ElapsedMilliseconds > GapMs) uiLate.Add(new JsonObject { ["atMs"] = Math.Round(sampler.Clock.Elapsed.TotalMilliseconds), ["answeredAfterMs"] = w.ElapsedMilliseconds }); await Task.Delay(20); } }));
            if (s.StandaloneApp) tasks.Add(Task.Run(async () => { while (!stop) { var w = Stopwatch.StartNew(); await raw.CallAsync(new RequestEnvelope { RequestId = "p", Method = "doc.summary", Params = new JsonObject() }, default); if (w.ElapsedMilliseconds > GapMs) pipeSlow.Add(new JsonObject { ["atMs"] = Math.Round(sampler.Clock.Elapsed.TotalMilliseconds), ["answeredAfterMs"] = w.ElapsedMilliseconds }); await Task.Delay(20); } }));

            await Task.Delay(2000); // a quiet start, so the signal is not the editor's own start-up
            await using var child = await s.Spawn(new Ctx { Pipe = pipe, Marker = marker, Harness = h });
            spawnedAtMs = sampler.Clock.Elapsed.TotalMilliseconds;
            await Task.Delay(TimeSpan.FromSeconds(Seconds));
            stop = true; await Task.WhenAll(tasks);

            var stops = sampler.StopsJson();
            var report = new JsonObject
            {
                ["scenario"] = s.Name, ["question"] = s.Question, ["configuration"] = Cfg, ["observedSeconds"] = Seconds, ["childStartedAtMs"] = Math.Round(spawnedAtMs),
                ["machineCpuBusyPercentOverall"] = sampler.OverallBusyPercent(), ["processStops"] = stops, ["poolStarvedWindows"] = sampler.StarvationJson(), ["poolTimerLate"] = poolLate, ["uiThreadLate"] = uiLate, ["appPipeSlow"] = pipeSlow,
                ["verdict"] = stops.Count > 0 ? "process stopped" : poolLate.Count > 0 ? "pool timers late, process not stopped" : "no stop",
                ["ran"] = DateTime.UtcNow.ToString("O"),
            };
            File.WriteAllText(Path.Combine(Out, file + ".json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (h is not null) await h.DisposeAsync();
            if (app is not null) { try { app.CloseMainWindow(); if (!app.WaitForExit(15000)) app.Kill(true); } catch { } }
            KillByMarker(marker);
        }
    }

    [DiagnosticFact] public Task S1() => Run(Scenarios[0]);
    [DiagnosticFact] public Task S2() => Run(Scenarios[1]);
    [DiagnosticFact] public Task S3() => Run(Scenarios[2]);
    [DiagnosticFact] public Task S4() => Run(Scenarios[3]);
    [DiagnosticFact] public Task S5() => Run(Scenarios[4]);
    [DiagnosticFact] public Task S6() => Run(Scenarios[5]);
    [DiagnosticFact] public Task S7() => Run(Scenarios[6]);
    [DiagnosticFact] public Task S8() => Run(Scenarios[7]);
    [DiagnosticFact] public Task S10() => Run(Scenarios[8]);
    [DiagnosticFact] public Task S11() => Run(Scenarios[9]);
    [DiagnosticFact] public Task S12() => Run(Scenarios[10]);
    [DiagnosticFact] public Task S13() => Run(Scenarios[11]);
    [DiagnosticFact] public Task S9() => Run(Scenarios[12]);
}
