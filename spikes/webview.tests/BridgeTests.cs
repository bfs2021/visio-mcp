using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using IpcProbe;
using ModelContextProtocol.Client;
using WebViewProbe;
using Xunit;

/// <summary>Runs a WPF window with a real WebView2 on its own STA thread and message pump, one per test.</summary>
sealed class Harness : IAsyncDisposable
{
    public Dispatcher Ui = null!;
    public MainWindow Window = null!;
    public ProbeBridge Bridge => Window.Bridge;
    Thread thread = null!;
    readonly string userData = Path.Combine(Path.GetTempPath(), "webview-probe-" + Guid.NewGuid().ToString("N"));

    public static string Spikes => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    public static string Dist => Path.Combine(Spikes, "canvas", "dist");

    public static async Task<Harness> StartAsync(bool navigate = true)
    {
        EnsureBuilt();
        var h = new Harness();
        var started = new TaskCompletionSource<Dispatcher>();
        h.thread = new Thread(() => { started.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true };
        h.thread.SetApartmentState(ApartmentState.STA);
        h.thread.Start();
        h.Ui = await started.Task;
        await h.Ui.InvokeAsync(async () =>
        {
            h.Window = new MainWindow(new ProbeOptions(Dist, h.userData));
            h.Window.Show();
            await h.Window.InitializeAsync();
            if (navigate) h.Window.Navigate();
        }).Task.Unwrap();
        if (navigate) await h.Bridge.WaitReadyAsync(Ct(30));
        return h;
    }

    static void EnsureBuilt()
    {
        if (File.Exists(Path.Combine(Dist, "webview.html"))) return;
        var psi = new ProcessStartInfo("cmd.exe", "/c npm run build") { WorkingDirectory = Path.Combine(Spikes, "canvas"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("npm run build failed");
    }

    public static CancellationToken Ct(int s = 20) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;

    public Task<T> OnUi<T>(Func<Task<T>> f) => Ui.InvokeAsync(f).Task.Unwrap();
    public Task<string> Script(string js) => OnUi(() => Bridge.ExecuteScriptAsync(js));

    public Task<ResponseEnvelope> Send(string method, JsonObject? p = null, string? session = "current", string? document = "current", int timeoutSeconds = 20)
    {
        var s = session == "current" ? Bridge.Session?.SessionId : session;
        var d = document == "current" ? Bridge.Session?.DocumentId : document;
        return Bridge.SendAsync(new RequestEnvelope(method, s, d, p ?? new JsonObject()), Ct(timeoutSeconds));
    }

    public static JsonObject ApplyParams(string tx, int baseRevision, params JsonObject[] ops) => new()
    {
        ["transactionId"] = tx, ["baseRevision"] = baseRevision, ["atomic"] = true, ["operations"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()),
    };

    public static JsonObject Move(string id, double dx, double dy) => new() { ["op"] = "moveElement", ["id"] = id, ["dx"] = dx, ["dy"] = dy };

    public async ValueTask DisposeAsync()
    {
        try { await Ui.InvokeAsync(() => Window.Close()).Task; } catch { }
        Ui.InvokeShutdown();
        thread.Join(5000);
        try { Directory.Delete(userData, true); } catch { }
    }
}

public class BridgeTests
{
    static CancellationToken Ct(int s = 20) => Harness.Ct(s);
    static string Tx() => Guid.NewGuid().ToString();

    [Fact]
    public async Task ReadyBeforeApply()
    {
        await using var h = await Harness.StartAsync(navigate: false);
        // The page has not loaded, so there is no editor.ready and no session: apply must be refused, not queued.
        var early = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, Harness.Move("rect", 10, 0)), session: Guid.NewGuid().ToString(), document: Guid.NewGuid().ToString());
        Assert.Equal("not_ready", early.Error!.Code);

        await h.OnUi(async () => { h.Window.Navigate(); await h.Bridge.WaitReadyAsync(Ct(30)); return 0; });
        var response = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, Harness.Move("rect", 10, 0)));
        Assert.Null(response.Error);
        Assert.Equal(1, response.Revision);
    }

    [Fact]
    public async Task MismatchedSessionRejected()
    {
        await using var h = await Harness.StartAsync();
        var stale = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, Harness.Move("rect", 10, 0)), session: Guid.NewGuid().ToString());
        Assert.Equal("session_mismatch", stale.Error!.Code);
        var wrongDoc = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, Harness.Move("rect", 10, 0)), document: Guid.NewGuid().ToString());
        Assert.Equal("document_mismatch", wrongDoc.Error!.Code);
        var summary = await h.Send("doc.summary");
        Assert.Equal(0, summary.Revision); // nothing was applied
    }

    [Fact]
    public async Task DuplicateTransactionChangesOnce()
    {
        await using var h = await Harness.StartAsync();
        var before = (await h.Send("doc.summary")).Result!["rect"]!["x"]!.GetValue<double>();
        var tx = Tx();
        var first = await h.Send("doc.apply", Harness.ApplyParams(tx, 0, Harness.Move("rect", 25, 0)));
        var duplicate = await h.Send("doc.apply", Harness.ApplyParams(tx, 0, Harness.Move("rect", 25, 0)));
        Assert.Equal(1, first.Revision);
        Assert.Equal(1, duplicate.Revision);
        Assert.True(duplicate.Duplicate);
        Assert.False(first.Duplicate);
        var after = (await h.Send("doc.summary")).Result!["rect"]!["x"]!.GetValue<double>();
        Assert.Equal(before + 25, after, 3); // moved once, not twice

        var stale = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, Harness.Move("rect", 5, 0)));
        Assert.Equal("revision_conflict", stale.Error!.Code);
    }

    [Theory]
    [InlineData("https://app.agentic-diagram.invalid/webview.html", true)]
    [InlineData("https://APP.agentic-diagram.invalid/", true)]
    [InlineData("https://app.agentic-diagram.invalid.evil.example/", false)]
    [InlineData("https://evilapp.agentic-diagram.invalid/", false)]
    [InlineData("https://app.agentic-diagram.invalid@evil.example/", false)]
    [InlineData("https://user@app.agentic-diagram.invalid/", false)]
    [InlineData("http://app.agentic-diagram.invalid/", false)]
    [InlineData("https://app.agentic-diagram.invalid:8443/", false)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OriginMatchIsExactNotAPrefix(string? source, bool allowed) => Assert.Equal(allowed, OriginPolicy.IsAllowed(source));

    [Fact]
    public async Task WrongOriginAndChildFrameRejected()
    {
        await using var h = await Harness.StartAsync();
        var session = h.Bridge.Session!.SessionId;

        // 1. A message that claims a foreign origin is dropped before it is parsed.
        h.Bridge.HandleWebMessageForTest("https://evil.example/", """{"protocolVersion":1,"kind":"event","method":"editor.ready","params":{"protocolVersion":1}}""", topLevel: true);
        Assert.Contains("wrong_origin", h.Bridge.RejectedMessages);

        // 2. A same-origin child frame tries to hijack the handshake: rejected as a non-top-level frame, session unchanged.
        var before = h.Bridge.RejectedMessages.Count;
        // The packaged page posts editor.ready on load, so a same-origin iframe of it is a faithful attacker.
        await h.Script("(() => { const f = document.createElement('iframe'); f.src = '/webview.html'; document.body.appendChild(f); return 1; })()");
        await WaitFor(() => h.Bridge.RejectedMessages.Count > before);
        Assert.Contains("child_frame", h.Bridge.RejectedMessages);
        Assert.Equal(session, h.Bridge.Session!.SessionId);

        // 3. Navigation away and new windows are blocked; the page keeps its origin.
        await h.Script("location.href = 'https://example.com/'");
        await WaitFor(() => h.Bridge.BlockedNavigations >= 1);
        await h.Script("window.open('https://example.com/')");
        await WaitFor(() => h.Bridge.BlockedWindows >= 1);
        Assert.Equal("\"https://app.agentic-diagram.invalid\"", await h.Script("location.origin"));

        // 4. Remote resources are denied: fetch to another origin fails.
        var fetched = await h.Script("fetch('https://example.com/').then(() => 'allowed', () => 'blocked')");
        Assert.Equal("\"blocked\"", fetched);
    }

    [Fact]
    public async Task RendererCrashSettlesPendingUnknown()
    {
        await using var h = await Harness.StartAsync();
        var oldSession = h.Bridge.Session!.SessionId;
        var hang = new JsonObject { ["op"] = "test.hang" };
        var pending = h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, hang), timeoutSeconds: 60);
        await WaitFor(() => h.Bridge.PendingCount == 1);

        await h.OnUi(async () => { await h.Bridge.CrashRendererAsync(); return 0; });
        var settled = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("timeout_unknown", settled.Error!.Code);
        Assert.Equal("unknown", settled.Error.Details?["outcome"]?.GetValue<string>());
        Assert.Equal(0, h.Bridge.PendingCount);
        Assert.Null(h.Bridge.Session); // not ready until the editor says so again

        var early = await h.Send("doc.summary", session: oldSession, document: Guid.NewGuid().ToString());
        Assert.Equal("not_ready", early.Error!.Code);

        // Recovery: reload; a fresh session id is issued and the old one no longer works.
        await h.OnUi(async () => { await h.Bridge.RecoverAsync(Ct(30)); return 0; });
        Assert.NotEqual(oldSession, h.Bridge.Session!.SessionId);
        var stale = await h.Send("doc.summary", session: oldSession);
        Assert.Equal("session_mismatch", stale.Error!.Code);
        Assert.Equal(0, (await h.Send("doc.summary")).Revision);
    }

    // ---- joined to the canvas and to I03 -------------------------------------------------------

    [Fact]
    public async Task CanvasShowsAppliedChange()
    {
        await using var h = await Harness.StartAsync();
        var r = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, Harness.Move("rect", 120, 40), new JsonObject { ["op"] = "setText", ["id"] = "rect", ["text"] = "Moved" }));
        Assert.Equal(1, r.Revision);
        Assert.Equal(new[] { "rect" }, r.Result!["changedIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        // The DOM label is driven by the canvas model, so this is the visible change and not only the store.
        Assert.Equal("\"1\"", await h.Script("document.body.dataset.revision"));
        Assert.Equal("\"rev 1\"", await h.Script("document.getElementById('rev').textContent"));

        var png = await h.OnUi(() => h.Bridge.CapturePreviewAsync());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.True(png.Length > 4000, "preview should not be blank");
        var dir = Path.Combine(Harness.Spikes, "..", "docs", "m0", "evidence", "G4");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "webview-after-apply.png"), png);
    }

    [Fact]
    public async Task AssetPngRendersWithoutTaint()
    {
        await using var h = await Harness.StartAsync();
        var fixtures = await File.ReadAllTextAsync(Path.Combine(Harness.Spikes, "canvas", "src", "fixtures.ts"));
        var png = Convert.FromBase64String(Regex.Match(fixtures, "PNG_BASE64 = '([^']+)'").Groups[1].Value);
        var sha = h.Bridge.Assets.Add(png, "image/png");
        var url = $"https://app.agentic-diagram.invalid/assets/{sha}";

        var set = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, new JsonObject { ["op"] = "setImage", ["id"] = "picture", ["assetUrl"] = url, ["sha256"] = sha }));
        Assert.Null(set.Error);

        var render = await h.Send("doc.render", timeoutSeconds: 30);
        Assert.Null(render.Error);
        var bytes = Convert.FromBase64String(render.Result!["png"]!.GetValue<string>());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]); // toBlob succeeded, so the canvas was not tainted

        // A wrong hash is refused: the asset URL is not trusted on its own.
        var bad = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 1, new JsonObject { ["op"] = "setImage", ["id"] = "picture", ["assetUrl"] = url, ["sha256"] = new string('0', 64) }));
        Assert.Equal("internal_error", bad.Error!.Code);
        Assert.Equal(1, (await h.Send("doc.summary")).Revision); // failed batch changed nothing
    }

    static string ShimDll()
    {
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(Harness.Spikes, "mcp", "bin", cfg, "net10.0", "McpProbe.dll");
    }

    [Fact]
    public async Task McpToolReadsLiveCanvasThroughPipe()
    {
        await using var h = await Harness.StartAsync();
        var pipe = $"probe-{Guid.NewGuid():N}";
        // The pipe host routes doc.* to the bridge, which asks the live frontend.
        await using var host = TestHost.Start(pipe, router: async (method, _, ct) =>
        {
            var r = await h.Send(method);
            if (r.Error is { } e) throw new HostException(e.Code, e.Message, e.Retryable);
            return r.Result!;
        });
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "probe", Command = "dotnet", Arguments = [ShimDll()],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct(60));

        var sw = Stopwatch.StartNew();
        var first = await client.CallToolAsync("get_document_summary", cancellationToken: Ct());
        Assert.Equal(0, first.StructuredContent!.Value.GetProperty("revision").GetInt32());

        var applied = await h.Send("doc.apply", Harness.ApplyParams(Tx(), 0, Harness.Move("rect", 50, 0)));
        Assert.Equal(1, applied.Revision);

        var second = await client.CallToolAsync("get_document_summary", cancellationToken: Ct());
        sw.Stop();
        var s = second.StructuredContent!.Value;
        Assert.Equal(1, s.GetProperty("revision").GetInt32()); // matches the reply revision and the label in the DOM
        Assert.Equal(110, s.GetProperty("rect").GetProperty("x").GetDouble(), 3); // fixture x=60, moved by 50
        Assert.Equal("\"1\"", await h.Script("document.body.dataset.revision"));
        Evidence.Record("endToEndTwoCallsMs", sw.Elapsed.TotalMilliseconds);
    }

    static async Task WaitFor(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(50); }
    }
}

static class Evidence
{
    static readonly object Gate = new();
    public static void Record(string key, double value)
    {
        lock (Gate)
        {
            var dir = Path.Combine(Harness.Spikes, "..", "docs", "m0", "evidence", "G4");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "measurements.json");
            var obj = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            obj[key] = Math.Round(value, 1);
            File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
