using System.Windows;
using System.Windows.Threading;
using Diagram.Host.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Diagram.App;

/// <summary>
/// The second WebView2: a page nobody looks at, where documents an agent creates live and are edited, laid out and rendered without touching
/// the person's canvas, history or input thread. It runs the same frontend in agent mode (no editor chrome, no tabs), joins the main router as
/// the "agent" channel, and is started on first use. It holds no host services: only the person's page may ask the host to open, save or recover.
/// </summary>
public sealed class AgentView : IAsyncDisposable
{
    readonly Func<Dispatcher> ui;
    readonly Func<Task<CoreWebView2Environment>> environment;
    readonly Func<BridgeRouter> router;
    readonly string contentRoot;
    readonly BlobStore blobs;
    readonly object gate = new();
    Task? starting;
    Window? window;
    WebViewHost? host;

    public AgentView(Func<Dispatcher> ui, Func<Task<CoreWebView2Environment>> environment, Func<BridgeRouter> router, string contentRoot, BlobStore blobs)
    {
        this.ui = ui; this.environment = environment; this.router = router; this.contentRoot = contentRoot; this.blobs = blobs;
    }

    public bool IsStarted { get { lock (gate) return starting is { IsCompletedSuccessfully: true }; } }

    /// <summary>Starts the view if it is not running (or is not running any more after a failure) and waits until its page can answer.</summary>
    public Task EnsureStartedAsync(CancellationToken ct)
    {
        lock (gate)
        {
            if (starting is null || starting.IsFaulted || starting.IsCanceled) starting = StartAsync();
            return starting.WaitAsync(ct);
        }
    }

    async Task StartAsync()
    {
        var env = await environment();
        var dispatcher = ui();
        var r = router();
        await dispatcher.InvokeAsync(async () =>
        {
            var web = new WebView2();
            window = new Window
            {
                Title = "Diagram (background)", Width = 1280, Height = 800, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = 0, Content = web,
            };
            window.Show();
            await web.EnsureCoreWebView2Async(env);
            host = new WebViewHost(web.CoreWebView2, dispatcher, contentRoot, blobs, null, r, BridgeRouter.Agent);
            host.Navigate("?view=agent");
        }).Task.Unwrap();
        await r.WaitChannelReadyAsync(BridgeRouter.Agent, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
    }

    public async ValueTask DisposeAsync()
    {
        Window? w; lock (gate) { w = window; window = null; }
        if (w is null) return;
        try { await w.Dispatcher.InvokeAsync(() => w.Close()).Task; } catch (Exception e) when (e is InvalidOperationException or TaskCanceledException) { }
    }
}
