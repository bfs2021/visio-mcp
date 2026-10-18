using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

/// <summary>Two pages behind one router: the person's, and the agent-only view. Documents are reached by id, whichever page holds them.</summary>
public class ChannelTests
{
    const string Origin = "https://app.agentic-diagram.invalid/index.html";
    const string D1 = "00000000-0000-4000-8000-0000000000d1", S1 = "00000000-0000-4000-8000-0000000000a1";
    const string D2 = "00000000-0000-4000-8000-0000000000d2", S2 = "00000000-0000-4000-8000-0000000000a2";
    const string S3 = "00000000-0000-4000-8000-0000000000a3";

    /// <summary>A page that records what it was asked and answers every request.</summary>
    sealed class Page(string name) : IFrontendTransport
    {
        public BridgeRouter Router = null!;
        public readonly List<string> Seen = new();
        public readonly List<JsonObject> Replies = new();
        public void Post(string json)
        {
            var msg = JsonNode.Parse(json)!.AsObject();
            if (msg["kind"]?.GetValue<string>() == "response") { lock (Replies) Replies.Add(msg); return; }
            lock (Seen) Seen.Add(msg["method"]!.GetValue<string>());
            var reply = new JsonObject { ["protocolVersion"] = 1, ["kind"] = "response", ["requestId"] = msg["requestId"]!.GetValue<string>(), ["result"] = new JsonObject { ["page"] = name } };
            Task.Run(() => Router.OnMessage(Origin, reply.ToJsonString(), true, name));
        }
    }

    static (BridgeRouter router, Page user, Page agent) Make(Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>? host = null)
    {
        var user = new Page(BridgeRouter.User); var agent = new Page(BridgeRouter.Agent);
        var router = new BridgeRouter(user, host);
        router.AddChannel(BridgeRouter.Agent, agent);
        user.Router = agent.Router = router;
        return (router, user, agent);
    }

    static void Event(BridgeRouter r, string channel, string method, string doc, string? session = null)
    {
        var p = new JsonObject { ["documentId"] = doc };
        if (session is not null) { p["sessionId"] = session; p["revision"] = 0; }
        r.OnMessage(Origin, new JsonObject { ["protocolVersion"] = 1, ["kind"] = "event", ["method"] = method, ["params"] = p }.ToJsonString(), true, channel);
    }

    static RequestEnvelope Req(string method, string? doc, string? session, string id = "r") => new() { RequestId = id, Method = method, DocumentId = doc, SessionId = session, Params = new JsonObject() };

    [Fact]
    public async Task ARequestGoesToThePageThatHoldsTheDocument()
    {
        var (router, user, agent) = Make();
        Event(router, BridgeRouter.User, "editor.ready", D1, S1);
        await router.WaitChannelReadyAsync(BridgeRouter.User, default);
        Event(router, BridgeRouter.Agent, "editor.document.opened", D2, S2);
        Assert.Equal(BridgeRouter.Agent, router.ChannelOf(D2));
        Assert.Equal(D1, router.CurrentScope!.DocumentId); // an agent's document is never the one in front
        var fromAgent = await router.SendAsync(Req("doc.snapshot", D2, S2), default);
        Assert.Equal("agent", fromAgent.Result!.Value.GetProperty("page").GetString());
        await router.SendAsync(Req("doc.snapshot", D1, S1), default);
        Assert.Equal(new[] { "doc.snapshot" }, agent.Seen);
        Assert.Equal(new[] { "doc.snapshot" }, user.Seen);
        // a request that names no document goes to the person's page; an explicit channel overrides
        await router.SendAsync(Req("app.activateDocument", null, null), default);
        Assert.Equal(2, user.Seen.Count);
        await router.SendAsync(Req("app.createDocument", null, null), default, BridgeRouter.Agent);
        Assert.Equal(new[] { "doc.snapshot", "app.createDocument" }, agent.Seen);
    }

    [Fact]
    public async Task WaitingForAChannelEndsWhenItSaysItsReadyWithoutADocument()
    {
        var (router, _, _) = Make();
        var wait = router.WaitChannelReadyAsync(BridgeRouter.Agent, default);
        Assert.False(wait.IsCompleted);
        router.OnMessage(Origin, """{"protocolVersion":1,"kind":"event","method":"editor.ready","params":{"protocolVersion":1}}""", true, BridgeRouter.Agent);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(router.IsReady); // that is not the person's page: there is still no document in front
    }

    [Fact]
    public void AMovedDocumentBelongsToItsNewPageAndTheOldPageLettingGoChangesNothing()
    {
        var (router, _, _) = Make();
        Event(router, BridgeRouter.User, "editor.ready", D1, S1);
        Event(router, BridgeRouter.Agent, "editor.document.opened", D2, S2);
        // the person's page takes the document (a new session), then the agent page drops its copy
        Event(router, BridgeRouter.User, "editor.document.opened", D2, S3);
        Assert.Equal(BridgeRouter.User, router.ChannelOf(D2));
        Assert.Equal(S3, router.ScopeOf(D2)!.SessionId);
        Event(router, BridgeRouter.Agent, "editor.document.closed", D2);
        Assert.Equal(BridgeRouter.User, router.ChannelOf(D2)); // the stale close did not remove the new holder's entry
        Assert.Equal(S3, router.ScopeOf(D2)!.SessionId);
    }

    [Fact]
    public async Task ChangesAreRefusedWhileADocumentIsFrozenButReadsAreNot()
    {
        var (router, _, agent) = Make();
        Event(router, BridgeRouter.User, "editor.ready", D1, S1);
        Event(router, BridgeRouter.Agent, "editor.document.opened", D2, S2);
        router.Freeze(D2);
        var refused = await router.SendAsync(Req("doc.apply", D2, S2), default);
        Assert.Equal("busy", refused.Error!.Code);
        Assert.True(refused.Error.Retryable);
        Assert.Equal("none", refused.Error.Outcome);
        Assert.Null((await router.SendAsync(Req("doc.snapshot", D2, S2), default)).Error);
        Assert.Equal(new[] { "doc.snapshot" }, agent.Seen); // the refused one never reached the page
        router.Unfreeze(D2);
        Assert.Null((await router.SendAsync(Req("doc.apply", D2, S2), default)).Error);
    }

    [Fact]
    public async Task OnlyThePersonsPageMayAskTheHostForServices()
    {
        var asked = new List<string>();
        var (router, user, agent) = Make((r, _) => { asked.Add(r.Method); return Task.FromResult(new ResponseEnvelope { RequestId = r.RequestId, Result = JsonSerializer.SerializeToElement(new { ok = true }) }); });
        router.OnMessage(Origin, """{"protocolVersion":1,"kind":"request","requestId":"a","method":"host.openJson","params":{}}""", true, BridgeRouter.Agent);
        router.OnMessage(Origin, """{"protocolVersion":1,"kind":"request","requestId":"b","method":"host.openJson","params":{}}""", true, BridgeRouter.User);
        for (var i = 0; i < 100 && (agent.Replies.Count == 0 || user.Replies.Count == 0); i++) await Task.Delay(20);
        Assert.Equal("method_not_found", agent.Replies.Single()["error"]!["code"]!.GetValue<string>());
        Assert.Equal(new[] { "host.openJson" }, asked);   // only the person's request reached the host
        Assert.NotNull(user.Replies.Single()["result"]);  // and each answer went back to the page that asked
    }

    [Fact]
    public async Task APageThatFailsLosesOnlyItsOwnDocumentsAndCalls()
    {
        var (router, _, _) = Make();
        Event(router, BridgeRouter.User, "editor.ready", D1, S1);
        Event(router, BridgeRouter.Agent, "editor.document.opened", D2, S2);
        var reset = new List<string>();
        router.ChannelReset += reset.Add;
        router.OnRendererFailed("the agent view crashed", BridgeRouter.Agent);
        Assert.Equal(new[] { BridgeRouter.Agent }, reset);
        Assert.Null(router.ChannelOf(D2));
        Assert.Equal(BridgeRouter.User, router.ChannelOf(D1));
        Assert.True(router.IsReady);
        Assert.False(router.RendererFailedRecently); // that is about the person's page
        Assert.Equal("document_mismatch", (await router.SendAsync(Req("doc.snapshot", D2, S2), default)).Error!.Code);
    }
}
