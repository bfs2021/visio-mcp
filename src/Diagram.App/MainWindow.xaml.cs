using System.IO;
using System.Windows;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Visio;
using Microsoft.Web.WebView2.Core;

namespace Diagram.App;

/// <param name="ContentRoot">Folder holding the packaged frontend (index.html and app/).</param>
/// <param name="UserDataDir">WebView2 profile folder.</param>
/// <param name="BlobDir">Durable content-addressed asset bytes.</param>
/// <param name="Offscreen">Place the window out of sight (automated tests).</param>
/// <param name="PipeName">Pipe the MCP shim connects to; null leaves live control off.</param>
/// <param name="ExportRoot">Folder a client may export into (besides the open document's own folder).</param>
public sealed record AppOptions(string ContentRoot, string UserDataDir, string BlobDir, bool Offscreen = false, string? PipeName = null, string? ExportRoot = null, string? LibraryDir = null, string? RecoveryDir = null, bool PromptOnClose = false)
{
    public static AppOptions Default()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgenticDiagram");
        return new AppOptions(Path.Combine(AppContext.BaseDirectory, "web"), Path.Combine(data, "webview"), Path.Combine(data, "blobs"), PipeName: Diagram.Ipc.PipeNames.For(), PromptOnClose: true, ExportRoot: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgenticDiagram", "Exports"));
    }
}

public partial class MainWindow : Window
{
    readonly AppOptions options;
    WebViewHost? host;

    public MainWindow(AppOptions options)
    {
        this.options = options;
        InitializeComponent();
        if (options.Offscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000; Top = 0; ShowActivated = false; ShowInTaskbar = false;
        }
        Closing += OnClosing;
        Blobs = new BlobStore(options.BlobDir);
        Writer = new AtomicFileWriter();
        Preparer = new AssetPreparer(Blobs);
        Library = new AssetLibrary(options.LibraryDir ?? Path.GetFullPath(Path.Combine(options.BlobDir, "..", "library")), Blobs);
        Resolver = new AssetResolver(Library, Preparer);
        Recovery = new RecoveryStore(options.RecoveryDir ?? Path.GetFullPath(Path.Combine(options.BlobDir, "..", "recovery")), Blobs);
    }

    public BlobStore Blobs { get; }
    public AtomicFileWriter Writer { get; }
    public AssetPreparer Preparer { get; }
    public AssetLibrary Library { get; }
    public AssetResolver Resolver { get; }
    public RecoveryStore Recovery { get; }
    public RecoveryManager RecoveryMgr { get; private set; } = null!;
    public WebViewHost Host => host ?? throw new InvalidOperationException("call InitializeAsync first");
    public BridgeRouter Router => Host.Router;
    public SessionCoordinator Coordinator { get; private set; } = null!;
    public HostServices Services { get; private set; } = null!;
    public LiveControl? Live { get; private set; }
    public NativeFileCoordinator Native { get; private set; } = null!;
    public ExportService Exports { get; private set; } = null!;
    public ReferenceComparisonService Comparisons { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: options.UserDataDir);
        await Web.EnsureCoreWebView2Async(env);
        var services = new HostServices(this);
        host = new WebViewHost(Web.CoreWebView2, Dispatcher, options.ContentRoot, Blobs, services.HandleAsync);
        Coordinator = new SessionCoordinator(host.Router, Blobs, Preparer, Writer);
        Services = services;
        Native = new NativeFileCoordinator(host.Router, host.Router, Blobs, Writer, new NativeVsdxImporter(), new NativeVsdxExporter(), host.Router);
        Exports = new ExportService(this, options.ExportRoot ?? Path.Combine(Path.GetTempPath(), "AgenticDiagramExports"), options.Offscreen ? null : new ExportConsentWindow(this));
        RecoveryMgr = new RecoveryManager(host.Router, Recovery, Blobs, () => Services.CurrentPath);
        RecoveryMgr.Start();
        Comparisons = new ReferenceComparisonService(this);
        if (options.PipeName is { } pipe) Live = new LiveControl(pipe, host.Router, Exports.HandleAsync, Library, Resolver, Comparisons.HandleAsync); // fails on a name collision rather than attaching to another owner
        Closed += async (_, _) =>
        {
            if (Live is not null) await Live.DisposeAsync();
            try { await RecoveryMgr.CloseCleanlyAsync(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // leaving recovery files behind is the safe failure
            await RecoveryMgr.DisposeAsync();
        };
    }

    // ---------------------------------------------------------------- closing with unsaved changes (W01)
    bool closeApproved, closeAsked;

    /// <summary>
    /// The first attempt to close is held while the page says whether anything is unsaved. If nothing is, the window closes. If something is,
    /// the page shows Save / Don't save / Cancel and answers through host.app.close or host.app.closeCancelled. A page that does not answer in time
    /// is not allowed to trap the window: it closes, and recovery still holds the work.
    /// </summary>
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!options.PromptOnClose || closeApproved || host is null) return;
        e.Cancel = true;
        if (closeAsked) return; // a prompt is already showing
        closeAsked = true;
        _ = AskPageAsync();
    }

    async Task AskPageAsync()
    {
        var dirty = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var r = await Router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "app.beforeClose", Params = new System.Text.Json.Nodes.JsonObject() }, cts.Token);
            dirty = r.Error is null && r.Result is { } res && res.TryGetProperty("dirty", out var d) && d.GetBoolean();
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException) { }
        if (dirty) return; // the page is asking the person; the answer arrives as host.app.close or host.app.closeCancelled
        await Dispatcher.InvokeAsync(() => { closeApproved = true; Close(); });
    }

    /// <summary>The person answered the prompt (or the page decided): close now, throwing away the unsaved work when asked.</summary>
    public async Task<bool> ApproveCloseAsync(bool discard)
    {
        if (!closeAsked) return false;
        if (discard) { try { await RecoveryMgr.DiscardCurrentAsync(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        await Dispatcher.InvokeAsync(() => { closeApproved = true; Close(); });
        return true;
    }

    /// <summary>The person chose Cancel: stay open, and ask again next time.</summary>
    public bool CancelClose() { var was = closeAsked; closeAsked = false; return was; }

    public void Navigate() => Host.Navigate();
}
