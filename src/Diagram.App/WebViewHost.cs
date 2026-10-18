using System.IO;
using System.Text;
using System.Windows.Threading;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Microsoft.Web.WebView2.Core;

namespace Diagram.App;

/// <summary>
/// The one place WebView2 is touched. It serves the packaged frontend and approved image bytes from a fixed virtual
/// HTTPS origin under a strict CSP, locks navigation, rejects child frames, and feeds everything else to the <see cref="BridgeRouter"/>.
/// All calls into WebView2 happen on the UI dispatcher.
/// </summary>
public sealed class WebViewHost : IFrontendTransport
{
    // No 'unsafe-eval': the frontend ships precompiled validators and never builds code at runtime.
    public const string Csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                              "connect-src 'self'; font-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'";

    readonly CoreWebView2 core;
    readonly Dispatcher ui;
    readonly string contentRoot;
    readonly BlobStore blobs;
    int blockedNavigations, blockedWindows, rendererFailures;

    public BridgeRouter Router { get; }
    public int BlockedNavigations => Volatile.Read(ref blockedNavigations);
    public int BlockedWindows => Volatile.Read(ref blockedWindows);
    public int RendererFailures => Volatile.Read(ref rendererFailures);

    public WebViewHost(CoreWebView2 core, Dispatcher ui, string contentRoot, BlobStore blobs,
        Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>? hostHandler = null)
    {
        this.core = core;
        this.ui = ui;
        this.contentRoot = Path.GetFullPath(contentRoot);
        this.blobs = blobs;
        Router = new BridgeRouter(this, hostHandler);

        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.AreHostObjectsAllowed = false; // no general host objects: messages only
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;

        core.WebMessageReceived += (_, e) => Router.OnMessage(e.Source, e.WebMessageAsJson, topLevel: true);
        // A frame that registers its own handler receives its messages instead of the top-level handler.
        core.FrameCreated += (_, e) => e.Frame.WebMessageReceived += (_, a) => Router.OnMessage(a.Source, a.WebMessageAsJson, topLevel: false);
        core.NavigationStarting += (_, e) =>
        {
            if (AppOrigin.IsAllowed(e.Uri)) return;
            e.Cancel = true;
            Interlocked.Increment(ref blockedNavigations);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; Interlocked.Increment(ref blockedWindows); };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += OnProcessFailed;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResource;
    }

    public void Navigate() => core.Navigate($"{AppOrigin.Uri}index.html");

    bool stopped;
    /// <summary>Cancel outstanding calls before WPF disposes the control; queued reply callbacks must not touch a closed WebView.</summary>
    public void Stop()
    {
        stopped = true;
        Router.OnRendererFailed("the window closed");
    }

    public void Post(string json)
    {
        void Send() { if (!stopped) core.PostWebMessageAsJson(json); }
        if (ui.CheckAccess()) Send(); else ui.BeginInvoke(Send);
    }

    void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            or CoreWebView2ProcessFailedKind.BrowserProcessExited)) return;
        Interlocked.Increment(ref rendererFailures);
        Router.OnRendererFailed("the renderer failed");
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) core.Reload(); // a fresh page starts a fresh session
    }

    /// <summary>Evaluates script and awaits a returned promise (ExecuteScriptAsync does not). For tests and diagnostics only.</summary>
    public async Task<string> EvaluateAsync(string js)
    {
        var args = System.Text.Json.JsonSerializer.Serialize(new { expression = js, awaitPromise = true, returnByValue = true });
        var r = System.Text.Json.Nodes.JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args))!;
        if (r["exceptionDetails"] is { } ex) throw new InvalidOperationException(ex.ToJsonString());
        return r["result"]?["value"]?.ToJsonString() ?? "null";
    }

    /// <summary>Starts a DevTools call without waiting for it (used to block or crash the renderer in tests).</summary>
    public Task FireDevToolsAsync(string method, string args = "{}") => core.CallDevToolsProtocolMethodAsync(method, args);

    public async Task<byte[]> CapturePreviewAsync()
    {
        using var ms = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- virtual origin content
    void OnResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        if (uri.Scheme != "https" || !string.Equals(uri.Host, AppOrigin.Host, StringComparison.OrdinalIgnoreCase))
        {
            e.Response = Respond(403, "Forbidden", "text/plain", "remote resources are denied"u8.ToArray());
            return;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            e.Response = ServeAsset(path["/assets/".Length..]);
            return;
        }

        var rel = path == "/" ? "index.html" : path.TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(contentRoot, rel));
        if (!full.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            e.Response = Respond(404, "Not Found", "text/plain", Array.Empty<byte>());
            return;
        }
        e.Response = Respond(200, "OK", MimeFor(full), File.ReadAllBytes(full));
    }

    CoreWebView2WebResourceResponse ServeAsset(string sha)
    {
        if (sha.Length != 64 || !blobs.Contains(sha)) return Respond(404, "Not Found", "text/plain", Array.Empty<byte>());
        try
        {
            var bytes = blobs.Read(sha); // synchronous on purpose: blocking on an async read here would deadlock the UI thread
            return Respond(200, "OK", ImageMime.Sniff(bytes), bytes);
        }
        catch (IOException) { return Respond(500, "Internal Server Error", "text/plain", Array.Empty<byte>()); }
    }

    static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".json" => "application/json",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    CoreWebView2WebResourceResponse Respond(int status, string reason, string mime, byte[] body) =>
        core.Environment.CreateWebResourceResponse(new MemoryStream(body), status, reason,
            $"Content-Type: {mime}\r\nContent-Security-Policy: {Csp}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store");
}

