using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

/// <summary>How messages reach the page. The WebView2 implementation marshals to the UI dispatcher itself.</summary>
public interface IFrontendTransport
{
    void Post(string json);
}

/// <summary>The exact origin the packaged frontend is served from. A prefix, another port, scheme or user-info trick does not match.</summary>
public static class AppOrigin
{
    public const string Host = "app.agentic-diagram.invalid";
    public static readonly Uri Uri = new($"https://{Host}");

    public static bool IsAllowed(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase)
        && u.Port == 443
        && string.IsNullOrEmpty(u.UserInfo);
}

/// <summary>
/// Host side of the message-only bridge. Validates where a message came from, tracks the editor's scope, correlates
/// requests to responses with host-generated ids, and settles pending calls as unknown if the renderer dies.
/// The editor (frontend engine) owns sessions, duplicate delivery and revisions; this router never replays or retries.
/// </summary>
public sealed class BridgeRouter : IEditorChannel, IExportSource, IRasteriser
{
    public const string User = "user";
    public const string Agent = "agent";

    readonly Dictionary<string, IFrontendTransport> channels = new();
    readonly Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>? hostHandler;
    readonly object gate = new();
    readonly Dictionary<string, (TaskCompletionSource<JsonObject> Tcs, bool Mutation, string Channel)> pending = new();
    readonly List<string> rejected = new();
    // Every open document, by id, with the newest revision seen for it. The active one is what single-document callers mean by "current".
    readonly Dictionary<string, (Scope Scope, int Revision, string Channel)> docs = new();
    readonly HashSet<string> frozen = new();
    readonly HashSet<string> stoppedChannels = new();
    readonly Dictionary<string, TaskCompletionSource> channelReady = new();
    string? activeId;
    long correlation;
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BridgeRouter(IFrontendTransport transport, Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>? hostHandler = null)
    {
        channels[User] = transport;
        this.hostHandler = hostHandler;
    }

    /// <summary>Adds another page the router talks to (the agent-only view). Its documents are reachable by id; they are never the active document.</summary>
    public void AddChannel(string name, IFrontendTransport transport) { lock (gate) { channels[name] = transport; stoppedChannels.Remove(name); channelReady[name] = new(TaskCreationOptions.RunContinuationsAsynchronously); } }

    /// <summary>Completes once the named page has signalled that it can answer.</summary>
    public Task WaitChannelReadyAsync(string channel, CancellationToken ct)
    {
        Task t;
        lock (gate) t = channelReady.TryGetValue(channel, out var tcs) ? tcs.Task : Task.CompletedTask;
        return t.WaitAsync(ct);
    }

    /// <summary>The active document's scope: what a caller that names no document means.</summary>
    public Scope? CurrentScope { get { lock (gate) return activeId is not null && docs.TryGetValue(activeId, out var d) ? d.Scope : null; } }
    public int CurrentRevision { get { lock (gate) return activeId is not null && docs.TryGetValue(activeId, out var d) ? d.Revision : 0; } }
    public bool IsReady { get { lock (gate) return docs.Values.Any(d => d.Channel == User); } }

    /// <summary>Which page holds a document, or null if none does.</summary>
    public string? ChannelOf(string documentId) { lock (gate) return docs.TryGetValue(documentId, out var d) ? d.Channel : null; }

    /// <summary>Every open document with the page that holds it and its newest revision seen.</summary>
    public IReadOnlyList<(Scope Scope, string Channel, int Revision, bool Active)> DocumentList
    {
        get { lock (gate) return docs.Values.Select(d => (d.Scope, d.Channel, d.Revision, d.Scope.DocumentId == activeId)).OrderBy(d => d.Item4 ? 0 : 1).ToList(); }
    }

    /// <summary>While a document is being handed from one page to the other, changes to it are refused (retryably) so none can land in the page that is giving it up.</summary>
    public void Freeze(string documentId) { lock (gate) frozen.Add(documentId); }
    public void Unfreeze(string documentId) { lock (gate) frozen.Remove(documentId); }

    /// <summary>The scope of one open document, or null if no such document is open.</summary>
    public Scope? ScopeOf(string documentId) { lock (gate) return docs.TryGetValue(documentId, out var d) ? d.Scope : null; }

    /// <summary>Every open document's scope, the active one first.</summary>
    public IReadOnlyList<Scope> Documents { get { lock (gate) return docs.Values.Select(d => d.Scope).OrderBy(s => s.DocumentId == activeId ? 0 : 1).ToList(); } }
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public IReadOnlyList<string> Rejected { get { lock (gate) return rejected.ToArray(); } }

    /// <summary>Raised for each committed-change event the page emits (the recovery journal subscribes in I51).</summary>
    public event Action<CommittedEvent>? Committed;

    /// <summary>Raised when the page announces a session (first ready, or after the document was replaced). The recovery manager starts its own checkpoint here.</summary>
    public event Action<Scope, int>? SessionChanged;

    /// <summary>True from a renderer failure until someone takes it: the next session is a restart, so unfinished work should be offered back at once.</summary>
    public bool RendererFailedRecently { get; private set; }
    public bool TakeRendererFailure() { lock (gate) { var v = RendererFailedRecently; RendererFailedRecently = false; return v; } }

    public async Task WaitReadyAsync(CancellationToken ct)
    {
        Task t;
        lock (gate) t = docs.Values.Any(d => d.Channel == User) ? Task.CompletedTask : ready.Task;
        await t.WaitAsync(ct);
    }

    // ---------------------------------------------------------------- inbound from the page
    void Reject(string reason) { lock (gate) rejected.Add(reason); }

    /// <summary>Entry point for every message. A message from a child frame or another origin is dropped before it is parsed.</summary>
    public void OnMessage(string source, string json, bool topLevel, string channel = User)
    {
        if (!topLevel) { Reject("child_frame"); return; }
        if (!AppOrigin.IsAllowed(source)) { Reject("wrong_origin"); return; }

        JsonObject? msg;
        try { msg = JsonNode.Parse(json) as JsonObject; } catch (JsonException) { Reject("malformed"); return; }
        if (msg is null || msg["protocolVersion"]?.GetValue<int>() != 1) { Reject("bad_envelope"); return; }

        switch (msg["kind"]?.GetValue<string>(), msg["method"]?.GetValue<string>())
        {
            case ("event", "editor.ready") when (channel != User && msg["params"]?["documentId"] is null):
                lock (gate) if (channelReady.TryGetValue(channel, out var cr)) cr.TrySetResult(); // a page that starts with no document
                break;
            case ("event", "editor.ready"):
            case ("event", "editor.session"):
                SetScope(msg["params"] as JsonObject, replacesActive: true, channel);
                break;
            case ("event", "editor.document.opened"):
                SetScope(msg["params"] as JsonObject, replacesActive: false, channel);
                break;
            case ("event", "editor.document.closed"):
                CloseDocument(msg["params"]?["documentId"]?.GetValue<string>(), channel);
                break;
            case ("event", "editor.document.activated"):
                Activate(msg["params"]?["documentId"]?.GetValue<string>(), channel);
                break;
            case ("event", "editor.committed"):
                try
                {
                    if (msg["params"].Deserialize<CommittedEvent>(ContractJson.Options) is { } e)
                    {
                        lock (gate) if (docs.TryGetValue(e.DocumentId, out var d) && d.Channel == channel) docs[e.DocumentId] = (d.Scope, Math.Max(d.Revision, e.Revision), d.Channel);
                        Committed?.Invoke(e);
                    }
                }
                catch (JsonException) { Reject("bad_committed_event"); }
                break;
            case ("response", _):
                Complete(msg);
                break;
            case ("request", { } method) when method.StartsWith("host."):
                _ = AnswerPageRequest(msg, channel);
                break;
            default:
                Reject("unexpected_message");
                break;
        }
    }

    /// <summary>
    /// A document announced itself. <c>editor.ready</c> and <c>editor.session</c> mean the document was (re)started and take the place of
    /// <c>replaces</c> (default: the active document) and become active unless the page says <c>active:false</c>; <c>editor.document.opened</c> adds one.
    /// </summary>
    void SetScope(JsonObject? p, bool replacesActive, string channel)
    {
        if (p?["documentId"]?.GetValue<string>() is not { } doc || p["sessionId"]?.GetValue<string>() is not { } session) { Reject("bad_scope"); return; }
        TaskCompletionSource toRelease;
        Scope announced; int announcedRevision; Scope? replaced = null;
        lock (gate)
        {
            var replaces = replacesActive ? p["replaces"]?.GetValue<string>() ?? (channel == User ? activeId : null) : null;
            if (replaces is not null && replaces != doc && docs.TryGetValue(replaces, out var old) && old.Channel == channel) { docs.Remove(replaces); replaced = old.Scope; }
            announced = new Scope { DocumentId = doc, SessionId = session };
            announcedRevision = p["revision"]?.GetValue<int>() ?? 0;
            docs[doc] = (announced, announcedRevision, channel);
            if (channel == User && (p["active"]?.GetValue<bool>() != false || activeId is null || !docs.ContainsKey(activeId))) activeId = doc;
            toRelease = ready;
        }
        if (channel == User) toRelease.TrySetResult();
        if (replaced is not null) try { DocumentReplaced?.Invoke(replaced, announced); } catch (Exception) { }
        try { SessionChanged?.Invoke(announced, announcedRevision); } catch (Exception) { /* a recovery failure must not break the bridge */ }
    }

    void CloseDocument(string? documentId, string channel)
    {
        if (documentId is null) { Reject("bad_scope"); return; }
        Scope? closed;
        lock (gate)
        {
            if (!docs.TryGetValue(documentId, out var d) || d.Channel != channel) return; // a page giving up a document it has handed over says nothing about the new holder
            docs.Remove(documentId);
            closed = d.Scope;
            if (activeId == documentId) activeId = null; // the page names the next one with editor.document.activated
        }
        try { DocumentClosed?.Invoke(closed); } catch (Exception) { }
    }

    void Activate(string? documentId, string channel)
    {
        lock (gate)
        {
            if (documentId is null || !docs.TryGetValue(documentId, out var d) || d.Channel != channel || channel != User) { rejected.Add("bad_scope"); return; }
            activeId = documentId;
        }
    }

    /// <summary>Raised when the page closes a document (its tab). The recovery manager decides what becomes of its session.</summary>
    public event Action<Scope>? DocumentClosed;

    /// <summary>Raised when a page's renderer failed: every document it held is gone from the registry.</summary>
    public event Action<string>? ChannelReset;

    /// <summary>Raised when a document was restarted as another (open, import, recovery): what belonged to the old one, such as its file, belongs to the new one.</summary>
    public event Action<Scope, Scope>? DocumentReplaced;

    void Complete(JsonObject msg)
    {
        var id = msg["requestId"]?.GetValue<string>();
        TaskCompletionSource<JsonObject>? tcs = null;
        lock (gate) if (id is not null && pending.Remove(id, out var p)) tcs = p.Tcs;
        tcs?.TrySetResult(msg); // an unknown id (late or forged) is ignored: a page cannot answer a request it was not sent
    }

    async Task AnswerPageRequest(JsonObject msg, string channel)
    {
        var id = msg["requestId"]?.GetValue<string>() ?? "";
        ResponseEnvelope response;
        try
        {
            if (hostHandler is null) response = Fail(id, "method_not_found", "the host offers no services");
            else if (channel != User && msg["method"]?.GetValue<string>() is not ("host.ping" or "host.preferences.get")) response = Fail(id, "method_not_found", "the agent view has no host services"); // only the page the person works in may ask the host to open, save or recover
            else
            {
                var request = msg.Deserialize<RequestEnvelope>(ContractJson.Options)!;
                response = await hostHandler(request, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { response = Fail(id, "invalid_request", e.Message); }
        catch (Exception e) { response = Fail(id, "internal_error", e.GetType().Name); }
        IFrontendTransport? back; lock (gate) channels.TryGetValue(channel, out back);
        back?.Post(JsonSerializer.Serialize(response with { RequestId = id }, ContractJson.Options));
    }

    /// <summary>Stop a transport during clean shutdown without dropping document/recovery ownership.</summary>
    public void OnTransportStopped(string reason, string channel = User)
    {
        List<TaskCompletionSource<JsonObject>> all;
        lock (gate)
        {
            stoppedChannels.Add(channel);
            var mine = pending.Where(p => p.Value.Channel == channel).ToList();
            all = mine.Select(p => p.Value.Tcs).ToList();
            foreach (var p in mine) pending.Remove(p.Key);
        }
        foreach (var t in all)
            t.TrySetResult(new JsonObject { ["error"] = JsonSerializer.SerializeToNode(new AppError { Code = "timeout_unknown", Message = reason, Retryable = false, Outcome = "unknown" }, ContractJson.Options) });
    }

    /// <summary>The renderer died or the page reloaded: no pending call can be answered, and whether each took effect is unknown.</summary>
    public void OnRendererFailed(string reason, string channel = User)
    {
        List<TaskCompletionSource<JsonObject>> all;
        lock (gate)
        {
            foreach (var id in docs.Where(d => d.Value.Channel == channel).Select(d => d.Key).ToList()) docs.Remove(id);
            if (channel == User)
            {
                activeId = null;
                RendererFailedRecently = true;
                ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else if (channelReady.ContainsKey(channel)) channelReady[channel] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var mine = pending.Where(p => p.Value.Channel == channel).ToList();
            all = mine.Select(p => p.Value.Tcs).ToList();
            foreach (var p in mine) pending.Remove(p.Key);
        }
        try { ChannelReset?.Invoke(channel); } catch (Exception) { }
        foreach (var t in all)
            t.TrySetResult(new JsonObject { ["error"] = JsonSerializer.SerializeToNode(new AppError { Code = "timeout_unknown", Message = reason, Retryable = false, Outcome = "unknown" }, ContractJson.Options) });
    }

    // ---------------------------------------------------------------- outbound to the page
    static ResponseEnvelope Fail(string id, string code, string message, bool retryable = false, JsonObject? details = null, string? outcome = null) =>
        new() { RequestId = id, Error = new AppError { Code = code, Message = message, Retryable = retryable, Details = details, Outcome = outcome } };

    static TimeSpan TimeoutFor(string method) => method switch
    {
        "doc.apply" or "doc.executeScript" or "doc.undo" or "doc.redo" or "doc.replace" => TimeSpan.FromSeconds(15),
        "doc.render" or "doc.exportSnapshot" or "assets.rasterise" => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(10),
    };

    static bool IsMutation(string method) => method is "doc.apply" or "doc.executeScript" or "doc.undo" or "doc.redo" or "doc.replace";

    /// <summary>Sends a request to the page that holds the document it names; one that names none goes to the person's page, unless <paramref name="channel"/> says otherwise.</summary>
    public async Task<ResponseEnvelope> SendAsync(RequestEnvelope request, CancellationToken ct, string? channel = null)
    {
        bool any; Scope? target = null; string route = channel ?? User; bool isFrozen = false;
        IFrontendTransport? transport;
        lock (gate)
        {
            any = docs.Count > 0 || (channel is not null && channels.ContainsKey(channel));
            if (request.DocumentId is not null && docs.TryGetValue(request.DocumentId, out var d)) { target = d.Scope; if (channel is null) route = d.Channel; isFrozen = frozen.Contains(request.DocumentId); }
            channels.TryGetValue(route, out transport);
        }
        if (!any || transport is null) return Fail(request.RequestId, "not_ready", "the editor has not signalled ready", retryable: true);
        if (isFrozen && IsMutation(request.Method)) return Fail(request.RequestId, "busy", "the document is being moved to the editor window; try again in a moment", retryable: true, outcome: "none");
        if (request.Method.StartsWith("doc."))
        {
            if (request.DocumentId is null || request.SessionId is null) return Fail(request.RequestId, "invalid_request", "document methods need documentId and sessionId");
            if (target is null) return Fail(request.RequestId, "document_mismatch", "the document is not the open one");
            if (request.SessionId != target.SessionId) return Fail(request.RequestId, "session_mismatch", "the session is not the current one");
        }

        var correlationId = $"c{Interlocked.Increment(ref correlation)}-{Guid.NewGuid():N}"; // host-generated: caller ids can collide, these cannot
        var mutation = IsMutation(request.Method);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (stoppedChannels.Contains(route)) return Fail(request.RequestId, "not_ready", "the editor transport is closed", outcome: "none");
            pending[correlationId] = (tcs, mutation, route);
        }

        var forwarded = request with { RequestId = correlationId };
        transport.Post(JsonSerializer.Serialize(forwarded, ContractJson.Options));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutFor(request.Method));
        try
        {
            var msg = await tcs.Task.WaitAsync(cts.Token);
            if (msg["error"] is JsonNode e) return new ResponseEnvelope { RequestId = request.RequestId, Error = e.Deserialize<AppError>(ContractJson.Options)! };
            var result = msg["result"] is JsonNode r ? JsonSerializer.SerializeToElement(r) : (JsonElement?)null;
            if (result is { } rv && rv.ValueKind == JsonValueKind.Object && rv.TryGetProperty("revision", out var rev) && rev.TryGetInt32(out var n))
                lock (gate) if (request.DocumentId is not null && docs.TryGetValue(request.DocumentId, out var d) && d.Channel == route) docs[request.DocumentId] = (d.Scope, Math.Max(d.Revision, n), d.Channel);
            return new ResponseEnvelope { RequestId = request.RequestId, Result = result };
        }
        catch (OperationCanceledException)
        {
            lock (gate) pending.Remove(correlationId);
            // A deadline says nothing about whether the page acted: never report it as a rollback.
            return Fail(request.RequestId, "timeout_unknown", "no reply before the deadline", outcome: mutation ? "unknown" : "none");
        }
    }

    // ---------------------------------------------------------------- IEditorChannel
    static RequestEnvelope For(string method, Scope s, JsonObject @params) =>
        new() { RequestId = Guid.NewGuid().ToString(), Method = method, DocumentId = s.DocumentId, SessionId = s.SessionId, Params = @params };

    static Result<T> Unpack<T>(ResponseEnvelope r)
    {
        if (r.Error is { } e) return Result<T>.Fail(e);
        try { return Result<T>.Ok(r.Result!.Value.Deserialize<T>(ContractJson.Options)!); }
        catch (JsonException ex) { return Result<T>.Fail("internal_error", "the editor returned an unreadable result: " + ex.Message); }
    }

    public async Task<Result<Snapshot>> SnapshotAsync(Scope scope, CancellationToken ct) =>
        Unpack<Snapshot>(await SendAsync(For("doc.snapshot", scope, new JsonObject()), ct));

    public async Task<Result<Snapshot>> ReplaceAsync(DiagramDocument document, ExpectedState expected, CancellationToken ct)
    {
        var p = new JsonObject { ["baseRevision"] = expected.BaseRevision, ["document"] = JsonSerializer.SerializeToNode(document, ContractJson.Options) };
        var s = new Scope { DocumentId = expected.DocumentId, SessionId = expected.SessionId };
        return Unpack<Snapshot>(await SendAsync(For("doc.replace", s, p), ct));
    }

    public async Task<Result<ExportSnapshot>> ExportSnapshotAsync(Scope scope, CancellationToken ct) =>
        Unpack<ExportSnapshot>(await SendAsync(For("doc.exportSnapshot", scope, new JsonObject()), ct));

    public async Task<Result<byte[]>> RasteriseAsync(string sha256, CancellationToken ct)
    {
        var scope = CurrentScope;
        if (scope is null) return Result<byte[]>.Fail("not_ready", "the editor has not signalled ready", retryable: true);
        var r = await SendAsync(For("assets.rasterise", scope, new JsonObject { ["sha256"] = sha256, ["maxPixels"] = 1024 }), ct);
        if (r.Error is { } e) return Result<byte[]>.Fail(e);
        try { return Result<byte[]>.Ok(Convert.FromBase64String(r.Result!.Value.GetProperty("base64").GetString()!)); }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException) { return Result<byte[]>.Fail("internal_error", "the editor returned an unreadable picture"); }
    }

    /// <summary>A rendered page (or the page as SVG) for MCP image export and previews: the editor draws it from a snapshot barrier.</summary>
    public async Task<Result<JsonElement>> RenderAsync(Scope scope, JsonObject parameters, CancellationToken ct)
    {
        var r = await SendAsync(For("doc.render", scope, parameters), ct);
        return r.Error is { } e ? Result<JsonElement>.Fail(e) : Result<JsonElement>.Ok(r.Result!.Value);
    }

    public async Task<Result<Unit>> MarkSavedAsync(Scope scope, int revision, string path, CancellationToken ct)
    {
        var r = await SendAsync(For("doc.markSaved", scope, new JsonObject { ["revision"] = revision, ["path"] = path }), ct);
        return r.Error is { } e ? Result<Unit>.Fail(e) : Result<Unit>.Ok(default);
    }
}
