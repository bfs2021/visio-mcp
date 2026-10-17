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
public sealed class BridgeRouter : IEditorChannel
{
    readonly IFrontendTransport transport;
    readonly Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>? hostHandler;
    readonly object gate = new();
    readonly Dictionary<string, (TaskCompletionSource<JsonObject> Tcs, bool Mutation)> pending = new();
    readonly List<string> rejected = new();
    Scope? scope;
    int revision;
    long correlation;
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BridgeRouter(IFrontendTransport transport, Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>? hostHandler = null)
    {
        this.transport = transport;
        this.hostHandler = hostHandler;
    }

    public Scope? CurrentScope { get { lock (gate) return scope; } }
    public int CurrentRevision { get { lock (gate) return revision; } }
    public bool IsReady { get { lock (gate) return scope is not null; } }
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public IReadOnlyList<string> Rejected { get { lock (gate) return rejected.ToArray(); } }

    /// <summary>Raised for each committed-change event the page emits (the recovery journal subscribes in I51).</summary>
    public event Action<CommittedEvent>? Committed;

    public async Task WaitReadyAsync(CancellationToken ct)
    {
        Task t;
        lock (gate) t = scope is not null ? Task.CompletedTask : ready.Task;
        await t.WaitAsync(ct);
    }

    // ---------------------------------------------------------------- inbound from the page
    void Reject(string reason) { lock (gate) rejected.Add(reason); }

    /// <summary>Entry point for every message. A message from a child frame or another origin is dropped before it is parsed.</summary>
    public void OnMessage(string source, string json, bool topLevel)
    {
        if (!topLevel) { Reject("child_frame"); return; }
        if (!AppOrigin.IsAllowed(source)) { Reject("wrong_origin"); return; }

        JsonObject? msg;
        try { msg = JsonNode.Parse(json) as JsonObject; } catch (JsonException) { Reject("malformed"); return; }
        if (msg is null || msg["protocolVersion"]?.GetValue<int>() != 1) { Reject("bad_envelope"); return; }

        switch (msg["kind"]?.GetValue<string>(), msg["method"]?.GetValue<string>())
        {
            case ("event", "editor.ready"):
            case ("event", "editor.session"):
                SetScope(msg["params"] as JsonObject);
                break;
            case ("event", "editor.committed"):
                try
                {
                    if (msg["params"].Deserialize<CommittedEvent>(ContractJson.Options) is { } e)
                    {
                        lock (gate) revision = e.Revision;
                        Committed?.Invoke(e);
                    }
                }
                catch (JsonException) { Reject("bad_committed_event"); }
                break;
            case ("response", _):
                Complete(msg);
                break;
            case ("request", { } method) when method.StartsWith("host."):
                _ = AnswerPageRequest(msg);
                break;
            default:
                Reject("unexpected_message");
                break;
        }
    }

    void SetScope(JsonObject? p)
    {
        if (p?["documentId"]?.GetValue<string>() is not { } doc || p["sessionId"]?.GetValue<string>() is not { } session) { Reject("bad_scope"); return; }
        TaskCompletionSource toRelease;
        lock (gate)
        {
            scope = new Scope { DocumentId = doc, SessionId = session };
            revision = p["revision"]?.GetValue<int>() ?? 0;
            toRelease = ready;
        }
        toRelease.TrySetResult();
    }

    void Complete(JsonObject msg)
    {
        var id = msg["requestId"]?.GetValue<string>();
        TaskCompletionSource<JsonObject>? tcs = null;
        lock (gate) if (id is not null && pending.Remove(id, out var p)) tcs = p.Tcs;
        tcs?.TrySetResult(msg); // an unknown id (late or forged) is ignored: a page cannot answer a request it was not sent
    }

    async Task AnswerPageRequest(JsonObject msg)
    {
        var id = msg["requestId"]?.GetValue<string>() ?? "";
        ResponseEnvelope response;
        try
        {
            if (hostHandler is null) response = Fail(id, "method_not_found", "the host offers no services");
            else
            {
                var request = msg.Deserialize<RequestEnvelope>(ContractJson.Options)!;
                response = await hostHandler(request, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { response = Fail(id, "invalid_request", e.Message); }
        catch (Exception e) { response = Fail(id, "internal_error", e.GetType().Name); }
        transport.Post(JsonSerializer.Serialize(response with { RequestId = id }, ContractJson.Options));
    }

    /// <summary>The renderer died or the page reloaded: no pending call can be answered, and whether each took effect is unknown.</summary>
    public void OnRendererFailed(string reason)
    {
        List<TaskCompletionSource<JsonObject>> all;
        lock (gate)
        {
            scope = null;
            ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            all = pending.Values.Select(p => p.Tcs).ToList();
            pending.Clear();
        }
        foreach (var t in all)
            t.TrySetResult(new JsonObject { ["error"] = JsonSerializer.SerializeToNode(new AppError { Code = "timeout_unknown", Message = reason, Retryable = false, Outcome = "unknown" }, ContractJson.Options) });
    }

    // ---------------------------------------------------------------- outbound to the page
    static ResponseEnvelope Fail(string id, string code, string message, bool retryable = false, JsonObject? details = null, string? outcome = null) =>
        new() { RequestId = id, Error = new AppError { Code = code, Message = message, Retryable = retryable, Details = details, Outcome = outcome } };

    static TimeSpan TimeoutFor(string method) => method switch
    {
        "doc.apply" or "doc.undo" or "doc.redo" or "doc.replace" => TimeSpan.FromSeconds(15),
        "doc.render" => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(10),
    };

    static bool IsMutation(string method) => method is "doc.apply" or "doc.undo" or "doc.redo" or "doc.replace";

    public async Task<ResponseEnvelope> SendAsync(RequestEnvelope request, CancellationToken ct)
    {
        Scope? current;
        lock (gate) current = scope;
        if (current is null) return Fail(request.RequestId, "not_ready", "the editor has not signalled ready", retryable: true);
        if (request.Method.StartsWith("doc."))
        {
            if (request.DocumentId is null || request.SessionId is null) return Fail(request.RequestId, "invalid_request", "document methods need documentId and sessionId");
            if (request.DocumentId != current.DocumentId) return Fail(request.RequestId, "document_mismatch", "the document is not the open one");
            if (request.SessionId != current.SessionId) return Fail(request.RequestId, "session_mismatch", "the session is not the current one");
        }

        var correlationId = $"c{Interlocked.Increment(ref correlation)}-{Guid.NewGuid():N}"; // host-generated: caller ids can collide, these cannot
        var mutation = IsMutation(request.Method);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) pending[correlationId] = (tcs, mutation);

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
                lock (gate) revision = Math.Max(revision, n);
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

    public async Task<Result<Unit>> MarkSavedAsync(Scope scope, int revision, string path, CancellationToken ct)
    {
        var r = await SendAsync(For("doc.markSaved", scope, new JsonObject { ["revision"] = revision, ["path"] = path }), ct);
        return r.Error is { } e ? Result<Unit>.Fail(e) : Result<Unit>.Ok(default);
    }
}
