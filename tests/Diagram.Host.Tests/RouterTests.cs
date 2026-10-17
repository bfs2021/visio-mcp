using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class RouterTests
{
    const string Origin = "https://app.agentic-diagram.invalid/index.html";
    const string Doc = "00000000-0000-4000-8000-0000000000d1";
    const string Session = "00000000-0000-4000-8000-0000000000a1";

    /// <summary>A scriptable page: records what the host posts and lets the test answer, out of order if it likes.</summary>
    sealed class FakePage : IFrontendTransport
    {
        public readonly BlockingCollection<JsonObject> Posted = new();
        public Func<JsonObject, JsonObject?>? Auto;
        public BridgeRouter Router = null!;
        public void Post(string json)
        {
            var msg = JsonNode.Parse(json)!.AsObject();
            Posted.Add(msg);
            if (Auto?.Invoke(msg) is { } reply) Router.OnMessage(Origin, reply.ToJsonString(), true);
        }
        public static JsonObject Response(JsonObject req, JsonNode? result = null, JsonObject? error = null)
        {
            var r = new JsonObject { ["protocolVersion"] = 1, ["kind"] = "response", ["requestId"] = req["requestId"]!.GetValue<string>() };
            if (error is not null) r["error"] = error; else r["result"] = result ?? new JsonObject();
            return r;
        }
    }

    static (BridgeRouter router, FakePage page) Make(Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>>? host = null)
    {
        var page = new FakePage();
        var router = new BridgeRouter(page, host);
        page.Router = router;
        return (router, page);
    }

    static void SignalReady(BridgeRouter r, string doc = Doc, string session = Session, int revision = 0) =>
        r.OnMessage(Origin, $$$"""{"protocolVersion":1,"kind":"event","method":"editor.ready","params":{"documentId":"{{{doc}}}","sessionId":"{{{session}}}","revision":{{{revision}}}}}""", true);

    static RequestEnvelope Req(string method, string? session = Session, string? doc = Doc, string id = "same", JsonObject? p = null) =>
        new() { RequestId = id, Method = method, SessionId = session, DocumentId = doc, Params = p ?? new JsonObject() };

    [Fact]
    public async Task NotReadyRejects()
    {
        var (router, page) = Make();
        var r = await router.SendAsync(Req("doc.snapshot"), default);
        Assert.Equal("not_ready", r.Error!.Code);
        Assert.True(r.Error.Retryable);
        Assert.Empty(page.Posted); // nothing was queued for a page that is not there
        SignalReady(router);
        Assert.True(router.IsReady);
    }

    [Theory]
    [InlineData("https://app.agentic-diagram.invalid/", true)]
    [InlineData("https://APP.agentic-diagram.invalid/x", true)]
    [InlineData("https://app.agentic-diagram.invalid.evil.example/", false)]
    [InlineData("https://xapp.agentic-diagram.invalid/", false)]
    [InlineData("https://app.agentic-diagram.invalid@evil.example/", false)]
    [InlineData("https://user@app.agentic-diagram.invalid/", false)]
    [InlineData("http://app.agentic-diagram.invalid/", false)]
    [InlineData("https://app.agentic-diagram.invalid:8443/", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OriginMatchIsExact(string? source, bool allowed) => Assert.Equal(allowed, AppOrigin.IsAllowed(source));

    [Fact]
    public void WrongOriginOrChildFrameRejects()
    {
        var (router, _) = Make();
        const string ready = $$$"""{"protocolVersion":1,"kind":"event","method":"editor.ready","params":{"documentId":"{{{Doc}}}","sessionId":"{{{Session}}}","revision":0}}""";
        router.OnMessage("https://evil.example/", ready, topLevel: true);
        router.OnMessage(Origin, ready, topLevel: false); // same origin, but a child frame
        router.OnMessage(Origin, "{not json", true);
        router.OnMessage(Origin, """{"protocolVersion":2,"kind":"event","method":"editor.ready","params":{}}""", true);
        Assert.False(router.IsReady); // none of them could establish a session
        Assert.Equal(new[] { "wrong_origin", "child_frame", "malformed", "bad_envelope" }, router.Rejected);
        SignalReady(router);
        Assert.True(router.IsReady);
    }

    [Fact]
    public async Task CorrelationsDoNotCollide()
    {
        var (router, page) = Make();
        SignalReady(router);
        // Fifty callers all use the same request id; the page answers in reverse order.
        var calls = Enumerable.Range(0, 50).Select(i => router.SendAsync(Req("doc.snapshot", p: new JsonObject { ["n"] = i }), default)).ToArray();
        var posted = Enumerable.Range(0, 50).Select(_ => page.Posted.Take()).ToList();
        Assert.Equal(50, posted.Select(p => p["requestId"]!.GetValue<string>()).Distinct().Count()); // host-generated ids are unique
        Assert.DoesNotContain(posted, p => p["requestId"]!.GetValue<string>() == "same");
        foreach (var p in Enumerable.Reverse(posted)) router.OnMessage(Origin, FakePage.Response(p, new JsonObject { ["n"] = p["params"]!["n"]!.GetValue<int>() }).ToJsonString(), true);
        var results = await Task.WhenAll(calls);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal("same", results[i].RequestId); // the caller sees its own id back
            Assert.Equal(i, results[i].Result!.Value.GetProperty("n").GetInt32());
        }
        Assert.Equal(0, router.PendingCount);
    }

    [Fact]
    public async Task UnknownOrForgedResponsesAreIgnored()
    {
        var (router, page) = Make();
        SignalReady(router);
        var call = router.SendAsync(Req("doc.snapshot"), default);
        var posted = page.Posted.Take();
        router.OnMessage(Origin, """{"protocolVersion":1,"kind":"response","requestId":"made-up","result":{"evil":true}}""", true);
        Assert.False(call.IsCompleted);
        router.OnMessage(Origin, FakePage.Response(posted, new JsonObject { ["ok"] = true }).ToJsonString(), true);
        router.OnMessage(Origin, FakePage.Response(posted, new JsonObject { ["ok"] = "second answer" }).ToJsonString(), true); // duplicate answer
        Assert.True((await call).Result!.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task PendingMutationCrashIsUnknown()
    {
        var (router, page) = Make();
        SignalReady(router);
        var apply = router.SendAsync(Req("doc.apply"), default);
        page.Posted.Take();
        Assert.Equal(1, router.PendingCount);
        router.OnRendererFailed("the renderer crashed");
        var r = await apply;
        Assert.Equal("timeout_unknown", r.Error!.Code);
        Assert.Equal("unknown", r.Error.Outcome); // it may or may not have committed: never reported as rolled back
        Assert.False(r.Error.Retryable);
        Assert.Equal(0, router.PendingCount);
        Assert.False(router.IsReady);
        Assert.Equal("not_ready", (await router.SendAsync(Req("doc.snapshot"), default)).Error!.Code);

        SignalReady(router, session: "00000000-0000-4000-8000-0000000000a2"); // recovery brings a new session
        Assert.Equal("session_mismatch", (await router.SendAsync(Req("doc.snapshot", session: Session), default)).Error!.Code);
    }

    [Fact]
    public async Task CallerCancellationOrDeadlineIsUnknownForMutationsAndNoneForReads()
    {
        var (router, _) = Make();
        SignalReady(router);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var apply = await router.SendAsync(Req("doc.apply"), cts.Token);
        Assert.Equal("timeout_unknown", apply.Error!.Code);
        Assert.Equal("unknown", apply.Error.Outcome);
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var read = await router.SendAsync(Req("doc.snapshot"), cts2.Token);
        Assert.Equal("none", read.Error!.Outcome);
        Assert.Equal(0, router.PendingCount);
    }

    [Fact]
    public async Task ScopeIsCheckedEarlyButTheEditorStaysAuthoritative()
    {
        var (router, page) = Make();
        SignalReady(router);
        Assert.Equal("session_mismatch", (await router.SendAsync(Req("doc.apply", session: "00000000-0000-4000-8000-0000000000ff"), default)).Error!.Code);
        Assert.Equal("document_mismatch", (await router.SendAsync(Req("doc.apply", doc: "00000000-0000-4000-8000-0000000000ff"), default)).Error!.Code);
        Assert.Equal("invalid_request", (await router.SendAsync(Req("doc.apply", session: null), default)).Error!.Code);
        Assert.Empty(page.Posted);
    }

    [Fact]
    public async Task EditorErrorsPassThroughUnchanged()
    {
        var (router, page) = Make();
        SignalReady(router);
        page.Auto = req => FakePage.Response(req, error: new JsonObject { ["code"] = "revision_conflict", ["message"] = "stale", ["retryable"] = false, ["details"] = new JsonObject { ["actual"] = 4 } });
        var r = await router.SendAsync(Req("doc.apply"), default);
        Assert.Equal("revision_conflict", r.Error!.Code);
        Assert.Equal(4, r.Error.Details!["actual"]!.GetValue<int>());
    }

    [Fact]
    public async Task EditorChannelMapsToDocMethods()
    {
        var (router, page) = Make();
        SignalReady(router);
        var methods = new List<(string Method, JsonObject Params)>();
        page.Auto = req =>
        {
            methods.Add((req["method"]!.GetValue<string>(), req["params"]!.AsObject().DeepClone().AsObject()));
            return req["method"]!.GetValue<string>() switch
            {
                "doc.snapshot" or "doc.replace" => FakePage.Response(req, JsonSerializer.SerializeToNode(new Snapshot { DocumentId = Doc, SessionId = Session, Revision = 3, Document = Docs.Make([]) }, ContractJson.Options)),
                _ => FakePage.Response(req),
            };
        };
        var scope = new Scope { DocumentId = Doc, SessionId = Session };
        var snap = await router.SnapshotAsync(scope, default);
        Assert.True(snap.IsOk, snap.Error?.Message);
        Assert.Equal(3, snap.Value.Revision);
        Assert.True((await router.MarkSavedAsync(scope, 3, @"C:\x.json", default)).IsOk);
        var rep = await router.ReplaceAsync(Docs.Make([]), new ExpectedState { DocumentId = Doc, SessionId = Session, BaseRevision = 3 }, default);
        Assert.True(rep.IsOk);
        Assert.Equal(new[] { "doc.snapshot", "doc.markSaved", "doc.replace" }, methods.Select(m => m.Method));
        Assert.Equal(3, methods[2].Params["baseRevision"]!.GetValue<int>());
        Assert.Equal(@"C:\x.json", methods[1].Params["path"]!.GetValue<string>());
        Assert.Equal(3, router.CurrentRevision);
    }

    [Fact]
    public async Task PageInitiatedHostRequestsAreAnsweredAndOthersRejected()
    {
        var (router, page) = Make((req, _) => Task.FromResult(new ResponseEnvelope { RequestId = req.RequestId, Result = JsonSerializer.SerializeToElement(new { pong = req.Method }) }));
        SignalReady(router);
        router.OnMessage(Origin, """{"protocolVersion":1,"kind":"request","requestId":"p1","method":"host.ping","params":{}}""", true);
        var reply = await Task.Run(() => page.Posted.Take());
        Assert.Equal("p1", reply["requestId"]!.GetValue<string>());
        Assert.Equal("host.ping", reply["result"]!["pong"]!.GetValue<string>());
        router.OnMessage(Origin, """{"protocolVersion":1,"kind":"request","requestId":"p2","method":"doc.apply","params":{}}""", true); // a page cannot call document methods on itself
        Assert.Contains("unexpected_message", router.Rejected);
    }

    [Fact]
    public void CommittedEventsAreForwardedAndMalformedOnesRejected()
    {
        var (router, _) = Make();
        SignalReady(router);
        var seen = new List<CommittedEvent>();
        router.Committed += seen.Add;
        router.OnMessage(Origin, """
            {"protocolVersion":1,"kind":"event","method":"editor.committed","params":{"sequence":1,"documentId":"__DOC__","sessionId":"__SESSION__","revision":1,
             "resolvedDiff":{"transactionId":"00000000-0000-4000-8000-0000000000b1","source":"gui","previousRevision":0,"revision":1,
              "upserts":{"elements":[],"pages":[],"layers":[],"assets":[]},"deletes":{"elementIds":[],"pageIds":[],"layerIds":[],"assetIds":[]}}}}
            """.Replace("__DOC__", Doc).Replace("__SESSION__", Session), true);
        Assert.Single(seen);
        Assert.Equal(1, seen[0].Sequence);
        router.OnMessage(Origin, """{"protocolVersion":1,"kind":"event","method":"editor.committed","params":{"sequence":"x"}}""", true);
        Assert.Contains("bad_committed_event", router.Rejected);
    }
}
