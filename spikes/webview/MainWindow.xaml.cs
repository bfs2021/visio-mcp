using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace WebViewProbe;

public partial class MainWindow : Window
{
    readonly ProbeOptions options;
    ProbeBridge? bridge;

    public MainWindow(ProbeOptions options)
    {
        this.options = options;
        InitializeComponent();
    }

    public ProbeBridge Bridge => bridge ?? throw new InvalidOperationException("call InitializeAsync first");

    public async Task InitializeAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: options.UserDataDir);
        await Web.EnsureCoreWebView2Async(env);
        bridge = new ProbeBridge(Web.CoreWebView2, Dispatcher, options.DistDir);
    }

    /// <summary>Loads the packaged frontend from the fixed virtual origin.</summary>
    public void Navigate() => Web.CoreWebView2.Navigate($"{OriginPolicy.Origin}webview.html");
}
