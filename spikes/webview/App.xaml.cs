using System.IO;
using System.Windows;
using IpcProbe;

namespace WebViewProbe;

/// <summary>
/// Demo shell: shows the canvas and serves doc.* from the live page on the user-scoped pipe, so the
/// McpProbe shim (run with no DIAGRAM_PIPE) can read it. Tests do not use this class.
/// </summary>
public partial class App : Application
{
    TestHost? pipeHost;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dist = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "canvas", "dist"));
        var userData = Path.Combine(Path.GetTempPath(), "webview-probe-app");
        var window = new MainWindow(new ProbeOptions(dist, userData)) { Left = 100, Top = 100, ShowActivated = true, ShowInTaskbar = true };
        window.Show();
        await window.InitializeAsync();
        window.Navigate();
        await window.Bridge.WaitReadyAsync(CancellationToken.None);

        pipeHost = TestHost.Start(PipeNames.For(Envelope.ProtocolVersion), router: async (method, _, ct) =>
        {
            var session = window.Bridge.Session ?? throw new HostException("not_ready", "the editor is not ready", retryable: true);
            var r = await window.Bridge.SendAsync(new RequestEnvelope(method, session.SessionId, session.DocumentId, new System.Text.Json.Nodes.JsonObject()), ct);
            if (r.Error is { } err) throw new HostException(err.Code, err.Message, err.Retryable);
            return r.Result!;
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pipeHost?.DisposeAsync().AsTask().Wait(2000);
        base.OnExit(e);
    }
}
