using System.IO;
using System.Windows;
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
public sealed record AppOptions(string ContentRoot, string UserDataDir, string BlobDir, bool Offscreen = false, string? PipeName = null, string? ExportRoot = null, string? LibraryDir = null)
{
    public static AppOptions Default()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgenticDiagram");
        return new AppOptions(Path.Combine(AppContext.BaseDirectory, "web"), Path.Combine(data, "webview"), Path.Combine(data, "blobs"), PipeName: Diagram.Ipc.PipeNames.For(), ExportRoot: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgenticDiagram", "Exports"));
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
        Blobs = new BlobStore(options.BlobDir);
        Writer = new AtomicFileWriter();
        Preparer = new AssetPreparer(Blobs);
        Library = new AssetLibrary(options.LibraryDir ?? Path.GetFullPath(Path.Combine(options.BlobDir, "..", "library")), Blobs);
        Resolver = new AssetResolver(Library, Preparer);
    }

    public BlobStore Blobs { get; }
    public AtomicFileWriter Writer { get; }
    public AssetPreparer Preparer { get; }
    public AssetLibrary Library { get; }
    public AssetResolver Resolver { get; }
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
        Exports = new ExportService(this, options.ExportRoot ?? Path.Combine(Path.GetTempPath(), "AgenticDiagramExports"));
        Comparisons = new ReferenceComparisonService(this);
        if (options.PipeName is { } pipe) Live = new LiveControl(pipe, host.Router, Exports.HandleAsync, Library, Resolver, Comparisons.HandleAsync); // fails on a name collision rather than attaching to another owner
        Closed += async (_, _) => { if (Live is not null) await Live.DisposeAsync(); };
    }

    public void Navigate() => Host.Navigate();
}
