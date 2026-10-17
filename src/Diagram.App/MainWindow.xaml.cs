using System.IO;
using System.Windows;
using Diagram.Host.Core;
using Microsoft.Web.WebView2.Core;

namespace Diagram.App;

/// <param name="ContentRoot">Folder holding the packaged frontend (index.html and app/).</param>
/// <param name="UserDataDir">WebView2 profile folder.</param>
/// <param name="BlobDir">Durable content-addressed asset bytes.</param>
/// <param name="Offscreen">Place the window out of sight (automated tests).</param>
public sealed record AppOptions(string ContentRoot, string UserDataDir, string BlobDir, bool Offscreen = false)
{
    public static AppOptions Default()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgenticDiagram");
        return new AppOptions(Path.Combine(AppContext.BaseDirectory, "web"), Path.Combine(data, "webview"), Path.Combine(data, "blobs"));
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
    }

    public BlobStore Blobs { get; }
    public AtomicFileWriter Writer { get; }
    public AssetPreparer Preparer { get; }
    public WebViewHost Host => host ?? throw new InvalidOperationException("call InitializeAsync first");
    public BridgeRouter Router => Host.Router;
    public SessionCoordinator Coordinator { get; private set; } = null!;
    public HostServices Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: options.UserDataDir);
        await Web.EnsureCoreWebView2Async(env);
        var services = new HostServices(this);
        host = new WebViewHost(Web.CoreWebView2, Dispatcher, options.ContentRoot, Blobs, services.HandleAsync);
        Coordinator = new SessionCoordinator(host.Router, Blobs, Preparer, Writer);
        Services = services;
    }

    public void Navigate() => Host.Navigate();
}
