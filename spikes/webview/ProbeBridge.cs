using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace WebViewProbe;

/// <summary>
/// Host side of the message-only bridge. Everything touching WebView2 runs on the UI dispatcher; callers on
/// other threads go through <see cref="SendAsync"/>. The host, not the page, owns session scope, duplicate
/// delivery and base-revision checks.
/// </summary>
public sealed class ProbeBridge
{
    const string Csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                       "connect-src 'self'; font-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'";

    readonly CoreWebView2 core;
    readonly Dispatcher ui;
    readonly string distDir;
    readonly string documentId = Guid.NewGuid().ToString();
    readonly Dictionary<string, TaskCompletionSource<JsonObject>> pending = new();
    readonly Dictionary<(string, string, string), ResponseEnvelope> committed = new();
    readonly Queue<(string, string, string)> committedOrder = new();
    readonly List<string> rejected = new();
    SemaphoreSlim gate = new(1, 1);
    int revision;
    int blockedNavigations, blockedWindows;
    volatile SessionInfo? session;

    public AssetStore Assets { get; } = new();
    public SessionInfo? Session => session;
    public int BlockedNavigations => Volatile.Read(ref blockedNavigations);
    public int BlockedWindows => Volatile.Read(ref blockedWindows);
    public int PendingCount { get { lock (pending) return pending.Count; } }
    public IReadOnlyList<string> RejectedMessages { get { lock (rejected) return rejected.ToArray(); } }

    public ProbeBridge(CoreWebView2 core, Dispatcher ui, string distDir)
    {
        this.core = core;
        this.ui = ui;
        this.distDir = Path.GetFullPath(distDir);

        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;

        core.WebMessageReceived += (_, e) => HandleWebMessage(e.Source, e.WebMessageAsJson, topLevel: true);
        // A frame that registers its own handler receives its messages instead of the top-level handler, so nothing from a child frame is trusted.
        core.FrameCreated += (_, e) => e.Frame.WebMessageReceived += (_, _) => Reject("child_frame");
        core.NavigationStarting += (_, e) =>
        {
            if (OriginPolicy.IsAllowed(e.Uri)) return;
            e.Cancel = true;
            Interlocked.Increment(ref blockedNavigations);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; Interlocked.Increment(ref blockedWindows); };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.BrowserProcessExited)
                SettleUnknown("the renderer failed");
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
    }

    // ---- inbound messages ----------------------------------------------------------------------

    void Reject(string reason) { lock (rejected) rejected.Add(reason); }

    /// <summary>Test entry point: the same path real messages take, with the source and frame kind supplied.</summary>
    public void HandleWebMessageForTest(string source, string json, bool topLevel) => ui.Invoke(() => HandleWebMessage(source, json, topLevel));

    void HandleWebMessage(string source, string json, bool topLevel)
    {
        if (!topLevel) { Reject("child_frame"); return; }
        if (!OriginPolicy.IsAllowed(source)) { Reject("wrong_origin"); return; }
        JsonObject? msg;
        try { msg = JsonNode.Parse(json) as JsonObject; } catch (JsonException) { Reject("malformed"); return; }
        if (msg is null || msg["protocolVersion"]?.GetValue<int>() != 1) { Reject("bad_envelope"); return; }

        switch (msg["kind"]?.GetValue<string>())
        {
            case "event" when msg["method"]?.GetValue<string>() == "editor.ready":
                _ = StartSessionAsync();
                break;
            case "response":
                var id = msg["requestId"]?.GetValue<string>();
                TaskCompletionSource<JsonObject>? tcs = null;
                lock (pending) if (id != null && pending.Remove(id, out var t)) tcs = t;
                tcs?.TrySetResult(msg);
                break;
            default:
                Reject("unexpected_message");
                break;
        }
    }

    // ---- session -------------------------------------------------------------------------------

    async Task StartSessionAsync()
    {
        SettleUnknown("the editor restarted"); // a second ready means the page reloaded: earlier calls cannot be answered
        var next = new SessionInfo(Guid.NewGuid().ToString(), documentId);
        var reply = await ForwardAsync("session.start", new JsonObject { ["sessionId"] = next.SessionId, ["documentId"] = next.DocumentId }, null, null, TimeSpan.FromSeconds(10), CancellationToken.None);
        if (reply.Error != null) return;
        revision = reply.Revision ?? 0;
        gate = new SemaphoreSlim(1, 1);
        lock (committed) { committed.Clear(); committedOrder.Clear(); }
        session = next;
    }

    public async Task WaitReadyAsync(CancellationToken ct)
    {
        while (session is null) await Task.Delay(25, ct);
    }

    void SettleUnknown(string why)
    {
        session = null;
        List<TaskCompletionSource<JsonObject>> all;
        lock (pending) { all = pending.Values.ToList(); pending.Clear(); }
        foreach (var t in all)
            t.TrySetResult(new JsonObject { ["error"] = new JsonObject { ["code"] = "timeout_unknown", ["message"] = why, ["retryable"] = false, ["details"] = new JsonObject { ["outcome"] = "unknown" } } });
    }

    public async Task CrashRendererAsync()
    {
        // Page.crash never returns normally once the renderer dies, so do not wait for it.
        var call = core.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
        await Task.WhenAny(call, Task.Delay(500));
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        core.Reload();
        await WaitReadyAsync(ct);
    }

    // ---- outbound requests ---------------------------------------------------------------------

    public Task<ResponseEnvelope> SendAsync(RequestEnvelope request, CancellationToken ct) =>
        ui.InvokeAsync(() => SendOnUiAsync(request, ct)).Task.Unwrap();

    static ResponseEnvelope Fail(string id, string code, string message, bool retryable = false, JsonObject? details = null) =>
        new(id, null, new ErrorBody(code, message, retryable, details));

    async Task<ResponseEnvelope> SendOnUiAsync(RequestEnvelope req, CancellationToken ct)
    {
        var current = session;
        if (current is null) return Fail(req.RequestId, "not_ready", "the editor has not signalled ready", retryable: true);
        if (req.SessionId != current.SessionId) return Fail(req.RequestId, "session_mismatch", "the session is not the current one");
        if (req.DocumentId != current.DocumentId) return Fail(req.RequestId, "document_mismatch", "the document is not the open one");

        var myGate = gate;
        await myGate.WaitAsync(ct); // one request at a time per session, in arrival order
        try
        {
            if (session != current) return Fail(req.RequestId, "session_mismatch", "the session ended while the request was queued");
            var timeout = req.Method == "doc.render" ? TimeSpan.FromSeconds(30) : req.Method == "doc.apply" ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(10);

            (string, string, string)? key = null;
            if (req.Method == "doc.apply")
            {
                if (!Guid.TryParse(req.Params["transactionId"]?.GetValue<string>(), out var tx) || req.Params["baseRevision"] is null || req.Params["atomic"]?.GetValue<bool>() != true)
                    return Fail(req.RequestId, "invalid_request", "doc.apply needs transactionId, baseRevision and atomic:true");
                key = (current.SessionId, current.DocumentId, tx.ToString());
                // Duplicate delivery is answered from the cache before the base-revision check, so a retry after a lost reply succeeds.
                lock (committed) if (committed.TryGetValue(key.Value, out var cached)) return cached with { RequestId = req.RequestId, Duplicate = true };
                if (req.Params["baseRevision"]!.GetValue<int>() != revision)
                    return Fail(req.RequestId, "revision_conflict", $"base revision {req.Params["baseRevision"]} is not the current revision {revision}");
            }

            var reply = await ForwardAsync(req.Method, req.Params, current.SessionId, current.DocumentId, timeout, ct, req.RequestId, mutation: req.Method == "doc.apply");
            if (reply.Revision is int r && reply.Error == null) revision = r;
            if (key is { } k && reply.Error == null)
            {
                lock (committed)
                {
                    committed[k] = reply;
                    committedOrder.Enqueue(k);
                    while (committedOrder.Count > 256) committed.Remove(committedOrder.Dequeue());
                }
            }
            return reply;
        }
        finally { myGate.Release(); }
    }

    async Task<ResponseEnvelope> ForwardAsync(string method, JsonObject @params, string? sessionId, string? documentId, TimeSpan timeout, CancellationToken ct, string? callerId = null, bool mutation = false)
    {
        var id = Guid.NewGuid().ToString(); // bridge correlation, independent of the caller's request id
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending) pending[id] = tcs;
        var envelope = new JsonObject
        {
            ["protocolVersion"] = 1, ["kind"] = "request", ["requestId"] = id, ["method"] = method,
            ["sessionId"] = sessionId, ["documentId"] = documentId, ["params"] = JsonNode.Parse(@params.ToJsonString()),
        };
        core.PostWebMessageAsJson(envelope.ToJsonString());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var msg = await tcs.Task.WaitAsync(cts.Token);
            var rid = callerId ?? id;
            if (msg["error"] is JsonObject e)
                return Fail(rid, e["code"]?.GetValue<string>() ?? "internal_error", e["message"]?.GetValue<string>() ?? "", e["retryable"]?.GetValue<bool>() ?? false, e["details"] as JsonObject);
            return new ResponseEnvelope(rid, msg["result"] as JsonObject, null);
        }
        catch (OperationCanceledException)
        {
            lock (pending) pending.Remove(id);
            var rid = callerId ?? id;
            // A timeout says nothing about whether the page acted on it.
            return Fail(rid, "timeout_unknown", "no reply before the deadline", details: new JsonObject { ["outcome"] = mutation ? "unknown" : "none" });
        }
    }

    // ---- page content --------------------------------------------------------------------------

    /// <summary>Evaluates a script and awaits a returned promise (ExecuteScriptAsync does not), returning the JSON value.</summary>
    public async Task<string> ExecuteScriptAsync(string js)
    {
        var args = JsonSerializer.Serialize(new { expression = js, awaitPromise = true, returnByValue = true });
        var r = JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args))!;
        if (r["exceptionDetails"] is { } ex) throw new InvalidOperationException(ex.ToJsonString());
        return r["result"]?["value"]?.ToJsonString() ?? "null";
    }

    public async Task<byte[]> CapturePreviewAsync()
    {
        using var ms = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        if (!string.Equals(uri.Host, OriginPolicy.Host, StringComparison.OrdinalIgnoreCase) || uri.Scheme != "https")
        {
            e.Response = Respond(403, "Forbidden", "text/plain", Encoding.UTF8.GetBytes("remote resources are denied"));
            return;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            var sha = path["/assets/".Length..];
            e.Response = Assets.TryGet(sha, out var item)
                ? Respond(200, "OK", item.Mime, item.Bytes)
                : Respond(404, "Not Found", "text/plain", Array.Empty<byte>());
            return;
        }

        var rel = path == "/" ? "webview.html" : path.TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(distDir, rel));
        if (!full.StartsWith(distDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            e.Response = Respond(404, "Not Found", "text/plain", Array.Empty<byte>());
            return;
        }
        e.Response = Respond(200, "OK", MimeFor(full), File.ReadAllBytes(full));
    }

    static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".json" or ".map" => "application/json",
        _ => "application/octet-stream",
    };

    CoreWebView2WebResourceResponse Respond(int status, string reason, string mime, byte[] body) =>
        core.Environment.CreateWebResourceResponse(new MemoryStream(body), status, reason,
            $"Content-Type: {mime}\r\nContent-Security-Policy: {Csp}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store");
}
