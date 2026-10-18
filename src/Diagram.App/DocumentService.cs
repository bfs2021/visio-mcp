using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Ipc;

namespace Diagram.App;

/// <summary>
/// The open documents as an agent and the person see them: list them, create one in the background, show one. A document an agent creates
/// lives in the agent view; showing it hands it to the person's window as a tab (the same document id, a fresh session, its recovery session
/// carried across), after which it is edited there like any other tab.
/// </summary>
public sealed class DocumentService
{
    readonly MainWindow window;
    CancellationTokenSource? pushing;

    public DocumentService(MainWindow window)
    {
        this.window = window;
        window.Router.Committed += e => { if (window.Router.ChannelOf(e.DocumentId) == BridgeRouter.Agent) PushSoon(); };
        window.Router.DocumentClosed += _ => PushSoon();
        window.Router.ChannelReset += _ => PushSoon();
    }

    BridgeRouter Router => window.Router;

    static RequestEnvelope App(string method, JsonObject p) => new() { RequestId = Guid.NewGuid().ToString(), Method = method, Params = p };

    // ---------------------------------------------------------------- describing
    async Task<JsonObject?> DescribeAsync(Scope scope, string channel, bool active, CancellationToken ct)
    {
        var r = await Router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "doc.status", DocumentId = scope.DocumentId, SessionId = scope.SessionId, Params = new JsonObject() }, ct);
        if (r.Error is not null || r.Result is not { } res) return null;
        var o = new JsonObject
        {
            ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId,
            ["title"] = res.TryGetProperty("title", out var t) ? t.GetString() : null,
            ["revision"] = res.TryGetProperty("revision", out var rev) ? rev.GetInt32() : 0,
            ["pages"] = res.TryGetProperty("pageCount", out var pc) ? pc.GetInt32() : 1,
            ["unsaved"] = res.TryGetProperty("unsaved", out var u) && u.GetBoolean(),
            ["savedPath"] = res.TryGetProperty("savedPath", out var sp) && sp.ValueKind == System.Text.Json.JsonValueKind.String ? sp.GetString() : null,
            ["view"] = channel == BridgeRouter.User ? "editor" : "background",
            ["active"] = active,
        };
        return o;
    }

    public async Task<JsonArray> ListAsync(CancellationToken ct, bool agentOnly = false)
    {
        var all = new JsonArray();
        foreach (var d in Router.DocumentList)
        {
            if (agentOnly && d.Channel != BridgeRouter.Agent) continue;
            if (await DescribeAsync(d.Scope, d.Channel, d.Active, ct) is { } o) all.Add(o);
        }
        return all;
    }

    // ---------------------------------------------------------------- requests
    public async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        switch (request.Method)
        {
            case "docs.list":
                return Wire.Ok(request.RequestId, new JsonObject { ["documents"] = await ListAsync(ct) });
            case "docs.create":
            {
                try { await window.Agents.EnsureStartedAsync(ct); }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException or InvalidOperationException) { return Wire.Error(request.RequestId, "not_ready", "the background editor could not be started: " + e.Message, retryable: true); }
                var r = await Router.SendAsync(App("app.createDocument", request.Params.DeepClone().AsObject()), ct, BridgeRouter.Agent);
                if (r.Error is not null) return r with { RequestId = request.RequestId };
                PushSoon();
                var o = JsonNode.Parse(r.Result!.Value.GetRawText())!.AsObject();
                o["view"] = "background";
                o["note"] = "The document is open in the background. Edit it with the returned documentId and sessionId; call show_document to put it in front of the person.";
                return Wire.Ok(request.RequestId, o);
            }
            case "docs.show":
                return await ShowAsync(request, ct);
            default:
                return Wire.Error(request.RequestId, "method_not_found", $"{request.Method} is not available");
        }
    }

    /// <summary>Puts a document in front of the person. A document already in the editor window is activated; one in the background is handed over first.</summary>
    public async Task<ResponseEnvelope> ShowAsync(RequestEnvelope request, CancellationToken ct)
    {
        var id = request.Params["documentId"]?.GetValue<string>();
        if (!Guid.TryParse(id, out _)) return Wire.Error(request.RequestId, "invalid_request", "documentId must be a UUID");
        var channel = Router.ChannelOf(id!);
        if (channel is null) return Wire.Error(request.RequestId, "document_mismatch", "no open document has that id; call list_documents");
        if (channel == BridgeRouter.User)
        {
            var r = await Router.SendAsync(App("app.activateDocument", new JsonObject { ["documentId"] = id }), ct, BridgeRouter.User);
            return r.Error is not null ? r with { RequestId = request.RequestId } : Wire.Ok(request.RequestId, new JsonObject { ["documentId"] = id, ["view"] = "editor", ["shown"] = true });
        }

        var scope = Router.ScopeOf(id!)!;
        Router.Freeze(id!); // nothing may change in the agent view while its copy is taken
        try
        {
            var snap = await Router.SnapshotAsync(scope, ct);
            if (!snap.IsOk) return Wire.Error(request.RequestId, snap.Error!.Code, snap.Error.Message, snap.Error.Retryable);
            var document = System.Text.Json.JsonSerializer.SerializeToNode(snap.Value.Document, ContractJson.Options);
            var adopt = await Router.SendAsync(App("app.adoptDocument", new JsonObject { ["document"] = document, ["activate"] = true }), ct, BridgeRouter.User);
            if (adopt.Error is not null) return adopt with { RequestId = request.RequestId }; // busy, or refused: the document stays where it was
            // the person's window holds it now; the background copy is dropped and its recovery session, redundant, is finished once the new one is safe
            await Router.SendAsync(App("app.dropDocument", new JsonObject { ["documentId"] = id }), ct, BridgeRouter.Agent);
            await window.RecoveryMgr.FinishRestoreAsync(scope.SessionId);
            var now = Router.ScopeOf(id!);
            return Wire.Ok(request.RequestId, new JsonObject { ["documentId"] = id, ["sessionId"] = now?.SessionId, ["view"] = "editor", ["shown"] = true, ["note"] = "The document moved to the editor window with a new sessionId; use it from now on." });
        }
        finally { Router.Unfreeze(id!); PushSoon(); }
    }

    /// <summary>The person closed the tab of a background document: it and its recovery session are thrown away.</summary>
    public async Task<ResponseEnvelope> DiscardAsync(RequestEnvelope request, CancellationToken ct)
    {
        var id = request.Params["documentId"]?.GetValue<string>();
        if (id is null || Router.ChannelOf(id) != BridgeRouter.Agent) return Wire.Error(request.RequestId, "document_mismatch", "that is not a background document");
        var scope = Router.ScopeOf(id)!;
        Router.Freeze(id);
        try
        {
            await Router.SendAsync(App("app.dropDocument", new JsonObject { ["documentId"] = id }), ct, BridgeRouter.Agent);
            await window.RecoveryMgr.DiscardSessionAsync(scope.SessionId);
        }
        finally { Router.Unfreeze(id); PushSoon(); }
        return Wire.Ok(request.RequestId, new JsonObject { ["discarded"] = id });
    }

    // ---------------------------------------------------------------- telling the person's window
    /// <summary>The background documents change rarely and often come in bursts; one message after things settle.</summary>
    public void PushSoon()
    {
        CancellationTokenSource cts;
        lock (this) { pushing?.Cancel(); pushing = cts = new CancellationTokenSource(); }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                await PushNowAsync(cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException) { }
        });
    }

    public async Task PushNowAsync(CancellationToken ct)
    {
        var list = await ListAsync(ct, agentOnly: true);
        await Router.SendAsync(App("app.agentDocuments", new JsonObject { ["documents"] = list }), ct, BridgeRouter.User);
    }
}
