using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

/// <summary>Several documents open at once: the router addresses each by id, and recovery keeps one session for each.</summary>
public class MultiDocumentTests
{
    const string Origin = "https://app.agentic-diagram.invalid/index.html";
    const string D1 = "00000000-0000-4000-8000-0000000000d1", S1 = "00000000-0000-4000-8000-0000000000a1";
    const string D2 = "00000000-0000-4000-8000-0000000000d2", S2 = "00000000-0000-4000-8000-0000000000a2";
    const string D3 = "00000000-0000-4000-8000-0000000000d3", S3 = "00000000-0000-4000-8000-0000000000a3";
    static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    /// <summary>A page holding several documents: answers snapshots and the list of pictures each still needs.</summary>
    sealed class Page : IFrontendTransport
    {
        public BridgeRouter Router = null!;
        public readonly Dictionary<string, (string Session, int Revision, string[] Blobs)> Docs = new();
        public readonly List<string> Seen = new();
        public void Post(string json)
        {
            var req = JsonNode.Parse(json)!.AsObject();
            var method = req["method"]!.GetValue<string>();
            var doc = req["documentId"]?.GetValue<string>() ?? "";
            Seen.Add($"{method}:{doc}");
            if (!Docs.TryGetValue(doc, out var d)) return;
            JsonNode result = method switch
            {
                "doc.snapshot" => JsonSerializer.SerializeToNode(new Snapshot { DocumentId = doc, SessionId = d.Session, Revision = d.Revision, Document = Support(doc, d.Revision) }, ContractJson.Options)!,
                "doc.retainedBlobs" => new JsonObject { ["hashes"] = new JsonArray(d.Blobs.Select(h => (JsonNode)h).ToArray()) },
                _ => new JsonObject(),
            };
            var reply = new JsonObject { ["protocolVersion"] = 1, ["kind"] = "response", ["requestId"] = req["requestId"]!.GetValue<string>(), ["result"] = result };
            Task.Run(() => Router.OnMessage(Origin, reply.ToJsonString(), true));
        }
    }

    static DiagramDocument Support(string id, int revision)
    {
        var doc = (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(Docs.Make([]), ContractJson.Options))!;
        doc["id"] = id; doc["revision"] = revision;
        return doc.Deserialize<DiagramDocument>(ContractJson.Options)!;
    }

    /// <summary>A page event. <paramref name="extra"/> adds parameters, e.g. <c>active</c> or <c>replaces</c>.</summary>
    static void Event(BridgeRouter r, string method, string doc, string? session = null, object? extra = null)
    {
        var p = new JsonObject { ["documentId"] = doc };
        if (session is not null) { p["sessionId"] = session; p["revision"] = 0; }
        if (extra is not null) foreach (var (k, v) in JsonSerializer.SerializeToNode(extra)!.AsObject().ToList()) p[k] = v?.DeepClone();
        r.OnMessage(Origin, new JsonObject { ["protocolVersion"] = 1, ["kind"] = "event", ["method"] = method, ["params"] = p }.ToJsonString(), true);
    }

    static (BridgeRouter router, Page page) Make()
    {
        var page = new Page();
        var router = new BridgeRouter(page);
        page.Router = router;
        page.Docs[D1] = (S1, 0, []);
        page.Docs[D2] = (S2, 0, []);
        return (router, page);
    }

    static RequestEnvelope Req(string doc, string session, string method = "doc.snapshot") => new() { RequestId = "r", Method = method, DocumentId = doc, SessionId = session, Params = new JsonObject() };

    [Fact]
    public async Task EachOpenDocumentIsAddressedById()
    {
        var (router, _) = Make();
        Event(router, "editor.ready", D1, S1);
        Event(router, "editor.document.opened", D2, S2, new { active = false });
        Assert.Equal(D1, router.CurrentScope!.DocumentId); // the second was opened in the background
        Assert.Equal(new[] { D1, D2 }, router.Documents.Select(s => s.DocumentId));
        Assert.True((await router.SendAsync(Req(D1, S1), default)).Error is null);
        Assert.True((await router.SendAsync(Req(D2, S2), default)).Error is null);
        Assert.Equal("session_mismatch", (await router.SendAsync(Req(D2, S1), default)).Error!.Code);
        Assert.Equal("document_mismatch", (await router.SendAsync(Req(D3, S3), default)).Error!.Code);
    }

    [Fact]
    public void ActivationMovesTheCurrentDocumentAndClosingRemovesIt()
    {
        var (router, _) = Make();
        Event(router, "editor.ready", D1, S1);
        Event(router, "editor.document.opened", D2, S2);
        Assert.Equal(D2, router.CurrentScope!.DocumentId); // an opened document is shown unless the page says otherwise
        Event(router, "editor.document.activated", D1);
        Assert.Equal(D1, router.CurrentScope!.DocumentId);
        Event(router, "editor.document.closed", D1);
        Assert.Null(router.CurrentScope); // until the page names the next one
        Assert.Equal(D2, router.Documents.Single().DocumentId);
        Event(router, "editor.document.activated", D2);
        Assert.Equal(D2, router.CurrentScope!.DocumentId);
        Event(router, "editor.document.activated", D3);
        Assert.Contains("bad_scope", router.Rejected);
    }

    [Fact]
    public void ANewSessionReplacesTheDocumentItNamesAndOnlyThat()
    {
        var (router, _) = Make();
        Event(router, "editor.ready", D1, S1);
        Event(router, "editor.document.opened", D2, S2);
        Event(router, "editor.session", D3, S3, new { replaces = D1, active = false });
        Assert.Equal(new[] { D2, D3 }, router.Documents.Select(s => s.DocumentId).OrderBy(x => x));
        Assert.Null(router.ScopeOf(D1));
        Assert.Equal(S3, router.ScopeOf(D3)!.SessionId);
        Event(router, "editor.session", D1, S1); // no "replaces": the active document (D2) is the one that was restarted
        Assert.Null(router.ScopeOf(D2));
    }

    sealed class Env : IAsyncDisposable
    {
        public readonly TempDir Dir = new();
        public readonly BlobStore Blobs;
        public readonly RecoveryStore Store;
        public readonly BridgeRouter Router;
        public readonly Page Page;
        public readonly RecoveryManager Recovery;
        public Env()
        {
            (Router, Page) = Make();
            Blobs = new BlobStore(Dir.File("blobs"));
            Store = new RecoveryStore(Dir.File("recovery"), Blobs);
            Recovery = new RecoveryManager(Router, Store, Blobs, () => null, new RecoveryOptions(FlushDelay: Fast));
            Recovery.Start();
        }
        public async Task OpenBothAsync()
        {
            Event(Router, "editor.ready", D1, S1);
            Event(Router, "editor.document.opened", D2, S2, new { active = false });
            await Eventually(() => Recovery.OpenSessions.Count == 2);
        }
        public async ValueTask DisposeAsync() { await Recovery.DisposeAsync(); Dir.Dispose(); }
    }

    static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    static CommittedEvent Commit(string doc, string session, int rev) => new()
    {
        Sequence = rev, DocumentId = doc, SessionId = session, Revision = rev,
        ResolvedDiff = new ResolvedDiff
        {
            TransactionId = Guid.NewGuid().ToString(), Source = "mcp", PreviousRevision = rev - 1, Revision = rev,
            Upserts = new DiffUpserts { Elements = [], Pages = [], Layers = [], Assets = [] },
            Deletes = new DiffDeletes { ElementIds = [], PageIds = [], LayerIds = [], AssetIds = [] },
        },
    };

    static void Committed(BridgeRouter r, string doc, string session, int rev) =>
        r.OnMessage(Origin, JsonSerializer.Serialize(new { protocolVersion = 1, kind = "event", method = "editor.committed", @params = Commit(doc, session, rev) }, ContractJson.Options), true);

    [Fact]
    public async Task EveryOpenDocumentGetsItsOwnSessionAndJournal()
    {
        await using var env = new Env();
        await env.OpenBothAsync();
        Committed(env.Router, D2, S2, 1);
        Committed(env.Router, D2, S2, 2);
        Committed(env.Router, D1, S1, 1);
        await env.Recovery.DrainAsync();
        Assert.Equal(1, env.Recovery.StatusOfSession(S1).LastDurableRevision);
        Assert.Equal(2, env.Recovery.StatusOfSession(S2).LastDurableRevision);
        Assert.Equal(S1, env.Recovery.SessionId); // the status the page shows follows the active document
        Event(env.Router, "editor.document.activated", D2);
        Assert.Equal(S2, env.Recovery.SessionId);
        Assert.Equal(2, env.Recovery.Status.LastDurableRevision);
    }

    [Fact]
    public async Task ClosingOneKeepsTheOthersChangesAndDiscardingRemovesOnlyThatSession()
    {
        await using var env = new Env();
        await env.OpenBothAsync();
        Committed(env.Router, D1, S1, 1);
        Committed(env.Router, D2, S2, 1);
        await env.Recovery.DrainAsync();
        Event(env.Router, "editor.document.closed", D1); // closed with unsaved work: it stays on disk until the person decides
        await env.Recovery.DrainAsync();
        Assert.DoesNotContain(S1, env.Recovery.OpenSessions);
        Assert.Contains(S2, env.Recovery.OpenSessions);
        Assert.Contains(env.Store.ReadRecoveryAsync(null, default).Result, c => c.SessionId == S1);
        await env.Recovery.DiscardSessionAsync(S1); // Don't save
        Assert.DoesNotContain(env.Store.ReadRecoveryAsync(null, default).Result, c => c.SessionId == S1);
        Assert.Contains(env.Store.ReadRecoveryAsync(null, default).Result, c => c.SessionId == S2);
        // a document nobody touched leaves nothing behind
        env.Page.Docs[D3] = (S3, 0, []);
        Event(env.Router, "editor.document.opened", D3, S3);
        await Eventually(() => env.Recovery.OpenSessions.Contains(S3));
        Event(env.Router, "editor.document.closed", D3);
        await env.Recovery.DrainAsync();
        Assert.DoesNotContain(env.Store.ReadRecoveryAsync(null, default).Result, c => c.SessionId == S3);
    }

    [Fact]
    public async Task OpenDocumentsAreNeverOfferedAsRecoverableWork()
    {
        await using var env = new Env();
        await env.OpenBothAsync();
        Committed(env.Router, D1, S1, 1);
        Committed(env.Router, D2, S2, 1);
        await env.Recovery.DrainAsync();
        Assert.Empty(await env.Recovery.CandidatesAsync(default)); // both are open in the window, not abandoned
        Event(env.Router, "editor.document.closed", D2);
        await env.Recovery.DrainAsync();
        Assert.Equal(S2, (await env.Recovery.CandidatesAsync(default)).Single().SessionId);
    }

    [Fact]
    public async Task APictureOnlyABackgroundDocumentNeedsSurvivesCollection()
    {
        await using var env = new Env();
        var kept = await env.Blobs.PutDurableAsync("only the background tab uses this"u8.ToArray(), default);
        var active = await env.Blobs.PutDurableAsync("the active tab uses this"u8.ToArray(), default);
        var orphan = await env.Blobs.PutDurableAsync("nobody uses this"u8.ToArray(), default);
        env.Page.Docs[D1] = (S1, 0, [active]);
        env.Page.Docs[D2] = (S2, 0, [kept]);
        await env.OpenBothAsync();
        var removed = await env.Recovery.CollectGarbageAsync([], TimeSpan.Zero, default);
        Assert.Equal(1, removed.Removed);
        Assert.True(env.Blobs.Contains(kept));
        Assert.True(env.Blobs.Contains(active));
        Assert.False(env.Blobs.Contains(orphan));
        Assert.Contains($"doc.retainedBlobs:{D1}", env.Page.Seen);
        Assert.Contains($"doc.retainedBlobs:{D2}", env.Page.Seen);
    }

    [Fact]
    public async Task AMaintenancePassKeepsEveryHolderAndReclaimsTheRest()
    {
        await using var env = new Env();
        var root = env.Dir.File("blobs");
        async Task<string> Old(string content)
        {
            var sha = await env.Blobs.PutDurableAsync(System.Text.Encoding.UTF8.GetBytes(content), default);
            File.SetLastWriteTimeUtc(Path.Combine(root, sha), DateTime.UtcNow - TimeSpan.FromDays(30));
            return sha;
        }
        var inActive = await Old("the active tab uses this");
        var inBackground = await Old("a background tab (or its undo) uses this");
        var library = new AssetLibrary(env.Dir.File("library"), env.Blobs);
        var preparer = new AssetPreparer(env.Blobs);
        var v1 = await preparer.PrepareAsync(new MemoryStream(Bytes.PngHeader(3, 3)), "image/png", "logo.png", default);
        var imported = await library.ImportAsync(v1.Value, [], default);
        var v2 = await preparer.PrepareAsync(new MemoryStream(Bytes.PngHeader(5, 5)), "image/png", "logo.png", default);
        Assert.True((await library.ReplaceSourceAsync(imported.Value.Asset.Id, v2.Value, default)).IsOk);
        var libraryOld = v1.Value.Ref.Asset.Sha256; var libraryNew = v2.Value.Ref.Asset.Sha256;
        foreach (var sha in new[] { libraryOld, libraryNew }) File.SetLastWriteTimeUtc(Path.Combine(root, sha), DateTime.UtcNow - TimeSpan.FromDays(30));
        var orphan = await Old("deleted from every document long ago");
        // a picture abandoned long ago, which an agent has just uploaded again and not placed yet
        var png = Bytes.PngHeader(7, 7);
        var abandoned = await env.Blobs.PutDurableAsync(png, default);
        File.SetLastWriteTimeUtc(Path.Combine(root, abandoned), DateTime.UtcNow - TimeSpan.FromDays(30));
        var uploadedAgain = await preparer.PrepareAsync(new MemoryStream(png), "image/png", "again.png", default);
        Assert.True(uploadedAgain.IsOk, uploadedAgain.Error?.Message);
        Assert.Equal(abandoned, uploadedAgain.Value.Ref.Asset.Sha256);
        env.Page.Docs[D1] = (S1, 0, [inActive]);
        env.Page.Docs[D2] = (S2, 0, [inBackground]);
        await env.OpenBothAsync();

        using var lease = BlobStoreLease.Take(root);
        await using var maintenance = new BlobMaintenance(lease, env.Dir.File("blob-maintenance.json"),
            async ct => (await library.ListAsync(null, null, ct)).SelectMany(a => a.History.Append(a.Sha256)).Concat(preparer.LiveHashes()),
            env.Recovery.CollectGarbageAsync);
        var run = await maintenance.RunOnceAsync(default);

        Assert.Equal(BlobMaintenanceRun.Collected, run.Outcome);
        Assert.Equal(1, run.Removed);
        Assert.Equal("deleted from every document long ago".Length, run.BytesFreed);
        Assert.False(env.Blobs.Contains(orphan));
        foreach (var sha in new[] { inActive, inBackground, libraryOld, libraryNew, abandoned }) Assert.True(env.Blobs.Contains(sha), sha);
    }

    [Fact]
    public async Task IfAnyOpenDocumentCannotAnswerNothingIsCollected()
    {
        await using var env = new Env();
        var picture = await env.Blobs.PutDurableAsync("unknown owner"u8.ToArray(), default);
        await env.OpenBothAsync();
        env.Page.Docs.Remove(D2); // the second page stops answering (here: it no longer knows the document)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var removed = BlobGcResult.None;
        try { removed = await env.Recovery.CollectGarbageAsync([], TimeSpan.Zero, cts.Token); } catch (OperationCanceledException) { }
        Assert.Equal(BlobGcResult.None, removed);
        Assert.True(env.Blobs.Contains(picture));
    }
}
