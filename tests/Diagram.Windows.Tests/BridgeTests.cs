using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Diagram.App;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>A real WPF window with a real WebView2 on its own STA thread, one per test, with temp profile and blob folders.</summary>
sealed class Harness : IAsyncDisposable
{
    public Dispatcher Ui = null!;
    public MainWindow Window = null!;
    public readonly string Root = Path.Combine(Path.GetTempPath(), "diagram-win-" + Guid.NewGuid().ToString("N"));
    Thread thread = null!;

    public BridgeRouter Router => Window.Router;
    public WebViewHost Host => Window.Host;

    public static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    public static string Dist => Path.Combine(RepoRoot, "src", "web", "dist");

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
            h.Window = new MainWindow(new AppOptions(Dist, Path.Combine(h.Root, "webview"), Path.Combine(h.Root, "blobs"), Offscreen: true));
            h.Window.Show();
            await h.Window.InitializeAsync();
            if (navigate) h.Window.Navigate();
        }).Task.Unwrap();
        if (navigate) await h.Router.WaitReadyAsync(Ct(30));
        return h;
    }

    static void EnsureBuilt()
    {
        if (File.Exists(Path.Combine(Dist, "index.html"))) return;
        var psi = new ProcessStartInfo("cmd.exe", "/c npm run build") { WorkingDirectory = Path.Combine(RepoRoot, "src", "web"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("npm run build failed");
    }

    public static CancellationToken Ct(int s = 20) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;
    public Task<T> OnUi<T>(Func<Task<T>> f) => Ui.InvokeAsync(f).Task.Unwrap();
    public Task<string> Eval(string js) => OnUi(() => Host.EvaluateAsync(js));

    public Task<ResponseEnvelope> Send(string method, JsonObject? p = null, string? requestId = null, Scope? scope = null)
    {
        scope ??= Router.CurrentScope;
        return Router.SendAsync(new RequestEnvelope
        {
            RequestId = requestId ?? Guid.NewGuid().ToString(), Method = method, DocumentId = scope?.DocumentId, SessionId = scope?.SessionId, Params = p ?? new JsonObject(),
        }, Ct(30));
    }

    public Task<ResponseEnvelope> Apply(int baseRevision, string? requestId = null, params JsonObject[] ops) =>
        Send("doc.apply", new JsonObject
        {
            ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = baseRevision, ["atomic"] = true,
            ["operations"] = new JsonArray(ops.Select(o => (JsonNode)o.DeepClone()).ToArray()),
        }, requestId);

    public static JsonObject Shape(string alias, double x = 10, double y = 10) => new()
    {
        ["op"] = "createShape", ["alias"] = alias, ["bounds"] = new JsonObject { ["x"] = x, ["y"] = y, ["width"] = 50, ["height"] = 30 },
    };

    public async ValueTask DisposeAsync()
    {
        try { await Ui.InvokeAsync(() => Window.Close()).Task; } catch { }
        Ui.InvokeShutdown();
        thread.Join(5000);
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class BridgeTests
{
    static async Task WaitFor(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(50); }
    }

    [Fact]
    public async Task NotReadyRejects()
    {
        await using var h = await Harness.StartAsync(navigate: false);
        var r = await h.Send("doc.snapshot", scope: new Scope { DocumentId = Guid.NewGuid().ToString(), SessionId = Guid.NewGuid().ToString() });
        Assert.Equal("not_ready", r.Error!.Code);
        Assert.False(h.Router.IsReady);
    }

    [Fact]
    public async Task AppStartsReadyWithA4Snapshot()
    {
        await using var h = await Harness.StartAsync();
        Assert.True(h.Router.IsReady);
        var r = await h.Send("doc.snapshot");
        Assert.Null(r.Error);
        var snap = r.Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        Assert.Equal(0, snap.Revision);
        var page = Assert.Single(snap.Document.Pages);
        Assert.Equal(841.89, page.WidthPt, 2); // A4 landscape
        Assert.Equal(595.28, page.HeightPt, 2);
        Assert.Equal(h.Router.CurrentScope!.SessionId, snap.SessionId);
        Assert.Contains("Ready", await h.Eval("document.getElementById('status').textContent")); // visible, not just in the store
        Assert.Equal("\"0\"", await h.Eval("document.body.dataset.revision"));
    }

    [Fact]
    public async Task WrongOriginOrChildFrameRejects()
    {
        await using var h = await Harness.StartAsync();
        var scope = h.Router.CurrentScope!;

        // A message that claims another origin is dropped before it is parsed.
        h.Router.OnMessage("https://evil.example/", """{"protocolVersion":1,"kind":"event","method":"editor.ready","params":{"documentId":"x","sessionId":"y","revision":0}}""", true);
        Assert.Contains("wrong_origin", h.Router.Rejected);

        // A same-origin iframe of the packaged page posts its own editor.ready on load: a faithful attacker. It must not take over the session.
        await h.Eval("(() => { const f = document.createElement('iframe'); f.src = '/index.html'; document.body.appendChild(f); return 1; })()");
        await WaitFor(() => h.Router.Rejected.Contains("child_frame"));
        Assert.Equal(scope.SessionId, h.Router.CurrentScope!.SessionId);

        // Navigation away, new windows and remote fetches are all refused; the page keeps its origin.
        await h.Eval("location.href = 'https://example.com/'");
        await WaitFor(() => h.Host.BlockedNavigations >= 1);
        await h.Eval("window.open('https://example.com/')");
        await WaitFor(() => h.Host.BlockedWindows >= 1);
        Assert.Equal("\"https://app.agentic-diagram.invalid\"", await h.Eval("location.origin"));
        Assert.Equal("\"blocked\"", await h.Eval("fetch('https://example.com/').then(() => 'allowed', () => 'blocked')"));
    }

    [Fact]
    public async Task CorrelationsDoNotCollide()
    {
        await using var h = await Harness.StartAsync();
        // Thirty concurrent mutations all carry the same caller request id; each must get back its own answer.
        // They run one after another inside the engine, so each response has a distinct revision.
        var calls = Enumerable.Range(0, 30).Select(i => h.Apply(0, "same", Harness.Shape($"s{i}", i * 5))).ToArray();
        var done = await Task.WhenAll(calls);
        // Only the first can win with baseRevision 0; the rest are conflicts, each reported to its own caller.
        Assert.All(done, r => Assert.Equal("same", r.RequestId));
        Assert.Equal(1, done.Count(r => r.Error is null));
        Assert.Equal(29, done.Count(r => r.Error?.Code == "revision_conflict"));
        Assert.Equal(0, h.Router.PendingCount);

        // And a sequence of snapshots with colliding ids each returns the current state.
        var snaps = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => h.Send("doc.snapshot", requestId: "same")));
        Assert.All(snaps, s => Assert.Equal(1, s.Result!.Value.GetProperty("revision").GetInt32()));
    }

    [Fact]
    public async Task PendingMutationCrashIsUnknown()
    {
        await using var h = await Harness.StartAsync();
        var oldSession = h.Router.CurrentScope!.SessionId;

        // Block the page's main thread so it cannot answer, then send a mutation and kill the renderer under it.
        _ = h.OnUi<string>(async () => { _ = h.Host.FireDevToolsAsync("Runtime.evaluate", """{"expression":"const t = Date.now(); while (Date.now() - t < 20000) {}"}"""); return ""; });
        await Task.Delay(300);
        var pending = h.Apply(0, null, Harness.Shape("a"));
        await WaitFor(() => h.Router.PendingCount == 1);

        await h.OnUi(async () => { _ = h.Host.FireDevToolsAsync("Page.crash"); await Task.Delay(300); return 0; });
        var settled = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("timeout_unknown", settled.Error!.Code);
        Assert.Equal("unknown", settled.Error.Outcome); // never reported as rolled back
        Assert.Equal(0, h.Router.PendingCount);
        Assert.True(h.Host.RendererFailures >= 1);

        // The host reloads the page; the engine mints a new session and the old one is refused.
        await h.Router.WaitReadyAsync(Harness.Ct(30));
        Assert.NotEqual(oldSession, h.Router.CurrentScope!.SessionId);
        var stale = await h.Send("doc.snapshot", scope: new Scope { DocumentId = h.Router.CurrentScope.DocumentId, SessionId = oldSession });
        Assert.Equal("session_mismatch", stale.Error!.Code);
        Assert.Equal(0, (await h.Send("doc.snapshot")).Result!.Value.GetProperty("revision").GetInt32()); // fresh document: the unknown mutation is not in it
    }

    [Fact]
    public async Task CspIsEnforcedAndThePageStillWorksWithoutEval()
    {
        await using var h = await Harness.StartAsync();
        // The policy header is on every response from the virtual origin and has no unsafe-eval.
        var header = JsonNode.Parse(await h.Eval("fetch('/index.html').then(r => r.headers.get('content-security-policy'))"))!.GetValue<string>();
        Assert.Equal(WebViewHost.Csp, header);
        Assert.DoesNotContain("unsafe-eval", header);
        // DevTools evaluation is exempt from CSP, so prove enforcement the way an attacker would hit it: an injected inline script must not run.
        Assert.Equal("\"blocked\"", await h.Eval("""
            (async () => {
              const s = document.createElement('script');
              s.textContent = 'window.__injected = true';
              document.head.appendChild(s);
              await new Promise(r => setTimeout(r, 100));
              return window.__injected === true ? 'ran' : 'blocked';
            })()
            """));
        // Validation is precompiled, so the engine works under that policy.
        var applied = await h.Apply(0, null, Harness.Shape("a"));
        Assert.Null(applied.Error);
        Assert.Equal(1, applied.Result!.Value.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task HostServedAssetsComeFromTheBlobStoreByHash()
    {
        await using var h = await Harness.StartAsync();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var sha = await h.Window.Blobs.PutDurableAsync(png, default);
        Assert.Equal("\"image/png|12\"", await h.Eval($"fetch('/assets/{sha}').then(async r => r.headers.get('content-type') + '|' + (await r.arrayBuffer()).byteLength)"));
        Assert.Equal("\"404\"", await h.Eval($"fetch('/assets/{new string('0', 64)}').then(r => String(r.status))"));
        Assert.Equal("\"404\"", await h.Eval("fetch('/assets/..%2f..%2findex.html').then(r => String(r.status))")); // no path escapes
    }

    [Fact]
    public async Task PageInitiatedHostCallsAreAnswered()
    {
        await using var h = await Harness.StartAsync();
        const string call = """
            new Promise(resolve => {
              const wv = window.chrome.webview;
              const handler = e => { if (e.data && e.data.kind === 'response' && e.data.requestId === 'probe-1') { wv.removeEventListener('message', handler); resolve(JSON.stringify(e.data)); } };
              wv.addEventListener('message', handler);
              wv.postMessage({ protocolVersion: 1, kind: 'request', requestId: 'probe-1', method: 'host.ping', params: {} });
            })
            """;
        var reply = JsonNode.Parse(JsonNode.Parse(await h.Eval(call))!.GetValue<string>())!;
        Assert.True(reply["result"]!["pong"]!.GetValue<bool>());
        Assert.Equal("probe-1", reply["requestId"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAndOpenRoundTripThroughTheRealStack()
    {
        await using var h = await Harness.StartAsync();
        var path = Path.Combine(h.Root, "doc.diagram.json");
        var applied = await h.Apply(0, null, Harness.Shape("a", 40, 40), Harness.Shape("b", 200, 40));
        Assert.Null(applied.Error);

        h.Window.Services.DialogOverride = kind => kind == "save" ? path : null;
        var save = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "s", Method = "host.saveJson", Params = new JsonObject() }, default);
        Assert.Null(save.Error);
        var status = (await h.Send("doc.status")).Result!.Value;
        Assert.False(status.GetProperty("dirty").GetBoolean()); // the saved revision is the current one

        await h.Apply(1, null, Harness.Shape("c", 300, 40));
        Assert.True((await h.Send("doc.status")).Result!.Value.GetProperty("dirty").GetBoolean()); // edited after saving

        var oldSession = h.Router.CurrentScope!.SessionId;
        h.Window.Services.DialogOverride = kind => kind == "open" ? path : null;
        var open = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "o", Method = "host.openJson", Params = new JsonObject() }, default);
        Assert.Null(open.Error);
        await WaitFor(() => h.Router.CurrentScope!.SessionId != oldSession); // the editor announced its new session
        var snap = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        Assert.Equal(new[] { "a", "b" }, snap.Document.Pages[0].Elements.OfType<ShapeElement>().Select(e => e.Alias).ToArray()); // the unsaved shape "c" is gone
        Assert.Equal("native-json", snap.Document.Source!.Format);
        Assert.Equal(path, snap.Document.Source.Path);
    }
}
