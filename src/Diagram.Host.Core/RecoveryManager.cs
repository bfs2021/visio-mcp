using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

public sealed record RecoveryOptions(int CheckpointEveryCommits = 50, TimeSpan? CheckpointEvery = null, TimeSpan? FlushDelay = null);

/// <summary>What the page is told about durability: the newest revision on disk, and whether the journal is struggling.</summary>
public sealed record RecoveryStatus(string? SessionId, int LastDurableRevision, int LastCommittedRevision, bool Degraded, string? Note);

/// <summary>
/// Keeps the host's recovery files in step with the editor. The editor owns the document; this only listens to its committed changes
/// (resolved diffs, in order), writes them to the journal, and takes a checkpoint every 50 commits, every 60 seconds with unsaved
/// changes, and at save. It never replays anything and never edits a document.
/// </summary>
public sealed class RecoveryManager : IAsyncDisposable
{
    readonly BridgeRouter router;
    readonly RecoveryStore store;
    readonly BlobStore blobs;
    readonly Func<string?> currentPath;
    readonly RecoveryOptions options;
    readonly Channel<Func<Task>> work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task worker;
    readonly Timer timer;
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    RecoverySession? session;
    int commitsSinceCheckpoint, lastCommitted, savedRevision = -1, startRevision;
    bool degraded;
    string? note;
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecoveryManager(BridgeRouter router, RecoveryStore store, BlobStore blobs, Func<string?> currentPath, RecoveryOptions? options = null)
    {
        this.router = router; this.store = store; this.blobs = blobs; this.currentPath = currentPath; this.options = options ?? new RecoveryOptions();
        worker = Task.Run(RunAsync);
        timer = new Timer(_ => { if (Volatile.Read(ref commitsSinceCheckpoint) > 0) Post(() => CheckpointAsync()); }, null, this.options.CheckpointEvery ?? TimeSpan.FromSeconds(60), this.options.CheckpointEvery ?? TimeSpan.FromSeconds(60));
    }

    public void Start()
    {
        router.Committed += OnCommitted;
        router.SessionChanged += OnSession;
        if (router.CurrentScope is { } s) OnSession(s, router.CurrentRevision); // the page may have announced itself before we were listening
    }

    public RecoveryStatus Status { get { lock (gate) return new RecoveryStatus(session?.SessionId, session?.LastDurableRevision ?? 0, lastCommitted, degraded, note); } }
    public string? SessionId { get { lock (gate) return session?.SessionId; } }

    /// <summary>Completes when the current session has its own first checkpoint on disk.</summary>
    public Task WhenSessionReadyAsync() { lock (gate) return session is not null ? Task.CompletedTask : ready.Task; }

    /// <summary>Waits until everything queued so far has been handled (for tests and for a clean close).</summary>
    public Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() => { done.TrySetResult(); return Task.CompletedTask; })) done.TrySetResult();
        return done.Task;
    }

    bool Post(Func<Task> job) => work.Writer.TryWrite(job); // false once disposed: nothing more will be written

    async Task RunAsync()
    {
        await foreach (var job in work.Reader.ReadAllAsync())
        {
            try { await job().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { Mark(true, "recovery could not write: " + e.Message); }
        }
    }

    void Mark(bool isDegraded, string? message) { lock (gate) { degraded = isDegraded; note = isDegraded ? message : null; } }

    // ---------------------------------------------------------------- session
    void OnSession(Scope scope, int revision) => Post(() => BeginAsync(scope));

    async Task BeginAsync(Scope scope)
    {
        var snap = await router.SnapshotAsync(scope, stop.Token).ConfigureAwait(false);
        if (!snap.IsOk) return; // the scope moved on already; the next announcement will begin its own session
        var previous = session;
        var previousWasUntouched = previous is not null && UntouchedNow();
        var doc = JsonSerializer.SerializeToNode(snap.Value.Document, ContractJson.Options)!;
        var next = await store.BeginSessionAsync(snap.Value.DocumentId, snap.Value.SessionId, doc, snap.Value.Revision, currentPath(), Shas(snap.Value.Document), stop.Token, options.FlushDelay).ConfigureAwait(false);
        lock (gate)
        {
            session = next; commitsSinceCheckpoint = 0; lastCommitted = startRevision = snap.Value.Revision; savedRevision = -1; degraded = false; note = null;
            ready.TrySetResult();
        }
        if (previous is not null && previous.SessionId != next.SessionId && previousWasUntouched) await store.CompleteAsync(previous.SessionId).ConfigureAwait(false);
    }

    /// <summary>The current session holds no changes worth keeping: nothing committed since it began, or everything committed was saved.</summary>
    bool UntouchedNow()
    {
        lock (gate) return session is not null && session.Journal.LastAcceptedRevision <= Math.Max(startRevision, savedRevision);
    }

    static IEnumerable<string> Shas(DiagramDocument d) => d.Assets.Select(a => a.Sha256);

    // ---------------------------------------------------------------- commits
    void OnCommitted(CommittedEvent e) => Post(() => AppendAsync(e));

    async Task AppendAsync(CommittedEvent e)
    {
        RecoverySession? s;
        lock (gate) { s = session; if (s is not null && s.SessionId == e.SessionId) lastCommitted = Math.Max(lastCommitted, e.Revision); }
        if (s is null || s.SessionId != e.SessionId) return; // an event from a session we are not following
        if (e.Revision <= s.Journal.LastAcceptedRevision) return; // already inside the session's checkpoint
        var r = await s.AppendAsync(e, stop.Token).ConfigureAwait(false);
        if (!r.IsOk)
        {
            Mark(true, r.Error!.Message);
            // a gap, a write fault or a missing picture: the way back to a sound journal is a fresh checkpoint of the committed state
            await CheckpointAsync().ConfigureAwait(false);
            return;
        }
        if (Interlocked.Increment(ref commitsSinceCheckpoint) >= options.CheckpointEveryCommits) await CheckpointAsync().ConfigureAwait(false);
    }

    /// <summary>Takes a checkpoint of the committed state at one revision (a queue barrier in the editor) and trims the journal behind it.</summary>
    async Task CheckpointAsync()
    {
        RecoverySession? s;
        lock (gate) s = session;
        if (s is null || router.CurrentScope is not { } scope || scope.SessionId != s.SessionId) return;
        var snap = await router.SnapshotAsync(scope, stop.Token).ConfigureAwait(false);
        if (!snap.IsOk || snap.Value.SessionId != s.SessionId) return;
        var doc = JsonSerializer.SerializeToNode(snap.Value.Document, ContractJson.Options)!;
        await s.CheckpointAsync(doc, snap.Value.Revision, Shas(snap.Value.Document), stop.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref commitsSinceCheckpoint, 0);
        lock (gate) { lastCommitted = Math.Max(lastCommitted, snap.Value.Revision); }
        Mark(false, null);
    }

    public Task CheckpointNowAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(async () => { try { await CheckpointAsync().ConfigureAwait(false); } finally { done.TrySetResult(); } })) done.TrySetResult();
        return done.Task;
    }

    /// <summary>The document reached disk at <paramref name="path"/> at <paramref name="revision"/>: checkpoint, and remember that nothing up to there needs recovering.</summary>
    public Task OnSavedAsync(string sessionId, int revision, string path)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(async () =>
        {
            try
            {
                RecoverySession? s; lock (gate) s = session;
                if (s is null || s.SessionId != sessionId) return;
                lock (gate) savedRevision = Math.Max(savedRevision, revision);
                await store.SetSavedAsync(sessionId, revision, path, stop.Token).ConfigureAwait(false);
                await CheckpointAsync().ConfigureAwait(false);
            }
            finally { done.TrySetResult(); }
        })) done.TrySetResult();
        return done.Task;
    }

    // ---------------------------------------------------------------- finding and finishing work
    /// <summary>
    /// Unfinished sessions other than the editor's current one that hold changes nobody saved. Clean or empty ones are removed on the way.
    /// The editor's current session is the one the page announced, which after a crash can be ahead of the session this manager is still following.
    /// </summary>
    public async Task<IReadOnlyList<RecoveryCandidate>> CandidatesAsync(CancellationToken ct)
    {
        var current = router.CurrentScope?.SessionId;
        var all = (await store.ReadRecoveryAsync(null, ct).ConfigureAwait(false)).Where(c => c.SessionId != current).ToList();
        var offer = new List<RecoveryCandidate>();
        foreach (var c in all)
        {
            var nothingToKeep = (c.LastDurableRevision == 0 && c.CheckpointRevision == 0) || c.LastDurableRevision <= (c.SavedRevision ?? -1); // a blank page nobody touched, or work that was saved
            if (nothingToKeep) { await store.CompleteAsync(c.SessionId).ConfigureAwait(false); continue; }
            offer.Add(c);
        }
        return offer;
    }

    public Task DiscardAsync(string sessionId) => store.CompleteAsync(sessionId);

    /// <summary>The restored document now has its own session and checkpoint, so the old session can go.</summary>
    public async Task FinishRestoreAsync(string oldSessionId)
    {
        await WhenSessionReadyAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        if (SessionId == oldSessionId) return;
        await store.CompleteAsync(oldSessionId).ConfigureAwait(false);
    }

    /// <summary>Clean close: the current session is removed only if there is nothing unsaved in it.</summary>
    public async Task CloseCleanlyAsync()
    {
        await DrainAsync().ConfigureAwait(false);
        RecoverySession? s; lock (gate) s = session;
        if (s is not null && UntouchedNow()) await store.CompleteAsync(s.SessionId).ConfigureAwait(false);
    }

    /// <summary>Removes picture bytes that no document, library entry, undo history or recovery session needs. Recent uploads are left alone.</summary>
    public async Task<int> CollectGarbageAsync(IEnumerable<string> libraryHashes, TimeSpan grace, CancellationToken ct)
    {
        var live = new HashSet<string>(store.ReferencedBlobs(), StringComparer.Ordinal);
        foreach (var h in libraryHashes) live.Add(h);
        if (router.CurrentScope is { } scope)
        {
            var r = await router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "doc.retainedBlobs", DocumentId = scope.DocumentId, SessionId = scope.SessionId, Params = new JsonObject() }, ct).ConfigureAwait(false);
            if (r.Error is not null || r.Result is not { } res) return 0; // without the editor's answer nothing can be proven unused
            foreach (var h in res.GetProperty("hashes").EnumerateArray()) live.Add(h.GetString()!);
        }
        else return 0;
        return BlobGc.Collect(blobs, live, grace);
    }

    public async ValueTask DisposeAsync()
    {
        router.Committed -= OnCommitted;
        router.SessionChanged -= OnSession;
        await timer.DisposeAsync().ConfigureAwait(false);
        work.Writer.TryComplete();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
        stop.Cancel();
    }
}
