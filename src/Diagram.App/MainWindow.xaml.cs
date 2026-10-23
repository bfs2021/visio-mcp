using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Visio;
using Microsoft.Web.WebView2.Core;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Diagram.App;

/// <param name="ContentRoot">Folder holding the packaged frontend (index.html and app/).</param>
/// <param name="UserDataDir">WebView2 profile folder.</param>
/// <param name="BlobDir">Durable content-addressed asset bytes.</param>
/// <param name="Offscreen">Place the window out of sight (automated tests).</param>
/// <param name="PipeName">Pipe the MCP shim connects to; null leaves live control off.</param>
/// <param name="ExportRoot">Folder a client may export into (besides the open document's own folder).</param>
public sealed record AppOptions(string ContentRoot, string UserDataDir, string BlobDir, bool Offscreen = false, string? PipeName = null, string? ExportRoot = null, string? LibraryDir = null, string? RecoveryDir = null, bool PromptOnClose = false, TimeProvider? RecoveryTimeProvider = null)
{
    public static AppOptions Default()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgenticDiagram");
        return new AppOptions(Path.Combine(AppContext.BaseDirectory, "web"), Path.Combine(data, "webview"), Path.Combine(data, "blobs"), PipeName: Diagram.Ipc.PipeNames.For(), PromptOnClose: false, ExportRoot: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgenticDiagram", "Exports"));
    }
}

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    string chromeTheme = "light";
    public void ApplyChromeTheme(string theme)
    {
        chromeTheme = theme;
        var dark = theme == "dark";
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x11, 0x18, 0x27) : Color.FromRgb(0xf1, 0xf5, 0xf9));
        Web.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x11, 0x18, 0x27) : System.Drawing.Color.FromArgb(0xf1, 0xf5, 0xf9);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var enabled = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
        }
    }
    readonly AppOptions options;
    WebViewHost? host;
    NativeFileDropAdapter? nativeDropAdapter;

    public MainWindow(AppOptions options)
    {
        this.options = options;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyChromeTheme(chromeTheme);
        if (options.Offscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000; Top = 0; ShowActivated = false; ShowInTaskbar = false;
        }
        Closing += OnClosing;
        Blobs = new BlobStore(options.BlobDir);
        Writer = new AtomicFileWriter();
        Recents = new RecentFilesStore(Path.GetFullPath(Path.Combine(options.BlobDir, "..", "recent-files.json")), new AtomicFileWriter());
        Preparer = new AssetPreparer(Blobs);
        Library = new AssetLibrary(options.LibraryDir ?? Path.GetFullPath(Path.Combine(options.BlobDir, "..", "library")), Blobs);
        Resolver = new AssetResolver(Library, Preparer);
        Workspace = new WorkspaceStore(Path.GetFullPath(Path.Combine(options.RecoveryDir ?? Path.Combine(options.BlobDir, "..", "recovery"), "..", "workspace.json")), Writer);
        Preferences = new PreferencesStore(Path.Combine(Path.GetDirectoryName(Workspace.File)!, "preferences.json"), Writer);
        Recovery = new RecoveryStore(options.RecoveryDir ?? Path.GetFullPath(Path.Combine(options.BlobDir, "..", "recovery")), Blobs);
    }

    public AppOptions Options => options;
    public BlobStore Blobs { get; }
    public AtomicFileWriter Writer { get; }
    public RecentFilesStore Recents { get; }
    public AssetPreparer Preparer { get; }
    public AssetLibrary Library { get; }
    public AssetResolver Resolver { get; }
    public RecoveryStore Recovery { get; }
    public WorkspaceStore Workspace { get; }
    public PreferencesStore Preferences { get; }
    public RecoveryManager RecoveryMgr { get; private set; } = null!;
    public WebViewHost Host => host ?? throw new InvalidOperationException("call InitializeAsync first");
    public BridgeRouter Router => Host.Router;
    public SessionCoordinator Coordinator { get; private set; } = null!;
    public HostServices Services { get; private set; } = null!;
    public LiveControl? Live { get; private set; }
    public NativeFileCoordinator Native { get; private set; } = null!;
    public ExportService Exports { get; private set; } = null!;
    public ReferenceComparisonService Comparisons { get; private set; } = null!;

    CoreWebView2Environment? webEnvironment;
    public AgentView Agents { get; private set; } = null!;
    public DocumentService Documents { get; private set; } = null!;

    readonly TaskCompletionSource shutdownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ShutdownCompleted => shutdownCompleted.Task;

    public async Task InitializeAsync()
    {
        var env = webEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: options.UserDataDir);
        await Web.EnsureCoreWebView2Async(env);
        Web.CoreWebView2.DocumentTitleChanged += (_, _) => Title = Web.CoreWebView2.DocumentTitle; // the page names the file and marks unsaved work (W04)
        var services = new HostServices(this);
        host = new WebViewHost(Web.CoreWebView2, Dispatcher, options.ContentRoot, Blobs, services.HandleAsync);
        Coordinator = new SessionCoordinator(host.Router, Blobs, Preparer, Writer);
        Services = services;
        nativeDropAdapter = new NativeFileDropAdapter(services.IngressTickets, services.IngressWindowId,
            () => Router.CurrentScope is { } scope && Router.ChannelOf(scope.DocumentId) == BridgeRouter.User ? BridgeRouter.User : null,
            NotifyNativeIngressAsync);
        services.Attach(host.Router);
        Native = new NativeFileCoordinator(host.Router, host.Router, Blobs, Writer, new NativeVsdxImporter(), new NativeVsdxExporter(), host.Router);
        Exports = new ExportService(this, options.ExportRoot ?? Path.Combine(Path.GetTempPath(), "AgenticDiagramExports"), options.Offscreen ? null : new ExportConsentWindow(this));
        var initialPreferences = await Preferences.ReadAsync();
        await Dispatcher.InvokeAsync(() => ApplyChromeTheme(initialPreferences.Preferences.Theme));
        RecoveryMgr = new RecoveryManager(host.Router, Recovery, Blobs, () => Services.CurrentPath,
            new RecoveryOptions(CheckpointEvery: TimeSpan.FromSeconds(initialPreferences.Preferences.Recovery.CheckpointSeconds)),
            documentPath: Services.PathOf, timeProvider: options.RecoveryTimeProvider);
        RecoveryMgr.Start();
        Agents = new AgentView(() => Dispatcher, () => Task.FromResult(webEnvironment!), () => host!.Router, options.ContentRoot, Blobs);
        Documents = new DocumentService(this);
        Comparisons = new ReferenceComparisonService(this);
        if (options.PipeName is { } pipe) Live = new LiveControl(pipe, host.Router, Exports.HandleAsync, Library, Resolver, Comparisons.HandleAsync, Documents); // fails on a name collision rather than attaching to another owner
        Closed += async (_, _) =>
        {
            try
            {
                Services.ClearIngressOnShutdown();
                if (Live is not null) await Live.DisposeAsync();
                await Agents.DisposeAsync();
                try { await RecoveryMgr.CloseCleanlyAsync(); await Workspace.MarkCleanExitAsync(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // leaving recovery files behind is the safe failure
                await RecoveryMgr.DisposeAsync();
            }
            finally { shutdownCompleted.TrySetResult(); }
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
        if (!options.PromptOnClose || closeApproved || host is null) { host?.Stop(); return; }
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
        if (discard) { try { await RecoveryMgr.DiscardAllAsync(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        await Dispatcher.InvokeAsync(() => { closeApproved = true; Close(); });
        return true;
    }

    /// <summary>The person chose Cancel: stay open, and ask again next time.</summary>
    public bool CancelClose() { var was = closeAsked; closeAsked = false; return was; }

    public void Navigate() => Host.Navigate();

    /// <summary>
    /// Opens files the shell named (a double-click, Open with, or a second launch). The window comes to the front, each file is approved for one
    /// open without a dialog, and the page opens them in tabs once it has finished bringing back the last session. Returns what could not be opened.
    /// </summary>
    public async Task<IReadOnlyList<string>> OpenFromShellAsync(LaunchRequest launch, CancellationToken ct = default)
    {
        await Dispatcher.InvokeAsync(BringToFront);
        if (launch.Files.Count == 0) return launch.Skipped;
        await Router.WaitReadyAsync(ct);
        Services.ApproveLaunch(launch.Files);
        var files = new System.Text.Json.Nodes.JsonArray(launch.Files.Select(f => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["path"] = f.Path, ["kind"] = f.Kind }).ToArray());
        var r = await Router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "app.openFiles", Params = new System.Text.Json.Nodes.JsonObject { ["files"] = files } }, ct);
        return r.Error is { } e ? [.. launch.Skipped, e.Message] : launch.Skipped;
    }

    internal async Task<bool> NotifyIngressAvailableAsync(IngressDropBatch batch, CancellationToken ct = default, IngressDropTarget? target = null)
    {
        var message = new IngressAvailableEvent(batch.BatchId, batch.Items, batch.Rejections, target);
        var response = await Router.SendAsync(new RequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString(), Method = "app.ingressAvailable",
            Params = System.Text.Json.JsonSerializer.SerializeToNode(message, ContractJson.Options)!.AsObject(),
        }, ct, BridgeRouter.User);
        return response.Error is null;
    }

    async Task<bool> NotifyNativeIngressAsync(IngressDropBatch batch, System.Windows.Point point, CancellationToken ct)
    {
        var scope = Router.CurrentScope;
        if (scope is null || Router.ChannelOf(scope.DocumentId) != BridgeRouter.User) return false;
        var captured = await Router.SendAsync(new RequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString(), Method = "app.captureIngressTarget", DocumentId = scope.DocumentId, SessionId = scope.SessionId,
            Params = new JsonObject { ["clientX"] = point.X, ["clientY"] = point.Y },
        }, ct, BridgeRouter.User);
        if (captured.Error is not null || captured.Result is null || Router.CurrentScope != scope) return false;
        IngressDropTarget? target;
        try { target = captured.Result.Value.Deserialize<IngressDropTarget>(ContractJson.Options); }
        catch (JsonException) { return false; }
        if (target is null || target.DocumentId != scope.DocumentId || target.SessionId != scope.SessionId || target.BaseRevision < 0 ||
            string.IsNullOrWhiteSpace(target.PageId) || target.PageId.Length > 128 || !double.IsFinite(target.X) || !double.IsFinite(target.Y)) return false;
        return await NotifyIngressAvailableAsync(batch, ct, target);
    }

    void OnNativeDragEnter(object sender, DragEventArgs e) => SetNativeDropEffect(e);
    void OnNativeDragOver(object sender, DragEventArgs e) => SetNativeDropEffect(e);

    void SetNativeDropEffect(DragEventArgs e)
    {
        var canDrop = e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false) && nativeDropAdapter is not null;
        if (!canDrop) return; // app-owned HTML palette/asset drags continue through WebView2
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnNativeDrop(object sender, DragEventArgs e)
    {
        if (nativeDropAdapter is null || !e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Copy;
        _ = ProcessNativeDropAsync(e.Data, e.GetPosition(Web));
    }

    async Task ProcessNativeDropAsync(IDataObject data, System.Windows.Point point)
    {
        try { if (nativeDropAdapter is { } adapter) await adapter.HandleDropAsync(data, point); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    void BringToFront()
    {
        if (options.Offscreen) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true; Topmost = false; // Windows lets a window it did not start take focus only this way
    }
}

