using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

public sealed record RecoveryOptions(int CheckpointEveryCommits = 50, TimeSpan? CheckpointEvery = null, TimeSpan? FlushDelay = null);

/// <summary>What the page is told about durability: the newest revision on disk, and whether the journal is struggling.</summary>
public sealed record RecoveryStatus(string? SessionId, int LastDurableRevision, int LastCommittedRevision, bool Degraded, string? Note);

/// <summary>
/// Keeps the host's recovery files in step with the editor. The editor owns each document; this only listens to their committed changes
/// (resolved diffs, in order), writes them to one journal per open document, and takes a checkpoint every 50 commits, every 60 seconds with unsaved
/// changes, and at save. It never replays anything and never edits a document.
/// </summary>
public sealed class RecoveryManager : IAsyncDisposable
{
    /// <summary>What is followed for one open document: its recovery session and the counters that decide when to checkpoint.</summary>
    sealed class Track
    {
        public required RecoverySession Session;
        public required string DocumentId;
        public int CommitsSinceCheckpoint, LastCommitted, SavedRevision = -1, StartRevision;
        public bool Degraded, KeepUnsaved;
        public string? Note;
        public string SessionId => Session.SessionId;
        /// <summary>No changes worth keeping: nothing committed since it began, or everything committed was saved.</summary>
        public bool Untouched => Session.Journal.LastAcceptedRevision <= SavedRevision || !KeepUnsaved && Session.Journal.LastAcceptedRevision <= StartRevision;
    }

    readonly BridgeRouter router;
    readonly RecoveryStore store;
    readonly BlobStore blobs;
    readonly Func<string?> currentPath;
    readonly Func<string, string?>? documentPath;
    readonly RecoveryOptions options;
    readonly Channel<Func<Task>> work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task worker;
    readonly ITimer timer;
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    bool stopping;
    readonly Dictionary<string, Track> tracks = new(); // by session id
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecoveryManager(BridgeRouter router, RecoveryStore store, BlobStore blobs, Func<string?> currentPath, RecoveryOptions? options = null, Func<string, string?>? documentPath = null, TimeProvider? timeProvider = null)
    {
        this.router = router; this.store = store; this.blobs = blobs; this.currentPath = currentPath; this.documentPath = documentPath; this.options = options ?? new RecoveryOptions();
        worker = Task.Run(RunAsync);
        var interval = this.options.CheckpointEvery ?? TimeSpan.FromSeconds(60);
        timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => { if (HasPending()) Post(CheckpointAllAsync); }, null, interval, interval);
    }

    bool HasPending() { lock (gate) return tracks.Values.Any(t => Volatile.Read(ref t.CommitsSinceCheckpoint) > 0); }

    public void Start()
    {
        router.Committed += OnCommitted;
        router.SessionChanged += OnSession;
        router.DocumentClosed += OnClosed;
        router.ChannelReset += OnChannelReset;
        foreach (var s in router.Documents) OnSession(s, router.CurrentRevision); // the page may have announced itself before we were listening
    }

    /// <summary>The active document's track, or null.</summary>
    Track? ActiveTrack() { lock (gate) return router.CurrentScope is { } s && tracks.TryGetValue(s.SessionId, out var t) ? t : null; }

    static RecoveryStatus StatusOf(Track? t) => new(t?.SessionId, t?.Session.LastDurableRevision ?? 0, t?.LastCommitted ?? 0, t?.Degraded ?? false, t?.Note);

    /// <summary>How far the active document is protected.</summary>
    public RecoveryStatus Status { get { lock (gate) return StatusOf(ActiveTrack()); } }

    /// <summary>The active document's recovery session.</summary>
    public string? SessionId { get { lock (gate) return ActiveTrack()?.SessionId; } }

    /// <summary>How far one document is protected.</summary>
    public RecoveryStatus StatusOfSession(string sessionId) { lock (gate) { tracks.TryGetValue(sessionId, out var t); return StatusOf(t); } }

    /// <summary>The recovery sessions being kept, one per open document.</summary>
    public IReadOnlyList<string> OpenSessions { get { lock (gate) return tracks.Keys.ToList(); } }

    /// <summary>Completes when some document has its own first checkpoint on disk.</summary>
    public Task WhenSessionReadyAsync() { lock (gate) return tracks.Count > 0 ? Task.CompletedTask : ready.Task; }

    /// <summary>Waits until everything queued so far has been handled (for tests and for a clean close).</summary>
    public Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() => { done.TrySetResult(); return Task.CompletedTask; })) done.TrySetResult();
        return done.Task;
    }

    bool Post(Func<Task> job) { lock (gate) return !stopping && work.Writer.TryWrite(job); }

    /// <summary>Changes only the periodic checkpoint cadence; queued commits and recovery sessions stay intact.</summary>
    public Task UpdateCheckpointIntervalAsync(TimeSpan interval, CancellationToken ct = default)
    {
        if (interval < TimeSpan.FromSeconds(10) || interval > TimeSpan.FromHours(1) || interval.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(interval), "Checkpoint interval must be 10–3600 whole seconds.");
        ct.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (stopping || !timer.Change(interval, interval)) throw new ObjectDisposedException(nameof(RecoveryManager));
                }
                done.TrySetResult();
            }
            catch (Exception e) { done.TrySetException(e); }
            return Task.CompletedTask;
        })) done.TrySetException(new ObjectDisposedException(nameof(RecoveryManager)));
        return done.Task;
    }

    async Task RunAsync()
    {
        await foreach (var job in work.Reader.ReadAllAsync())
        {
            try { await job().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { Mark(null, true, "recovery could not write: " + e.Message); }
        }
    }

    /// <summary>Marks one document's protection as struggling (or fine again); with no document, all of them.</summary>
    void Mark(Track? t, bool isDegraded, string? message)
    {
        lock (gate)
        {
            foreach (var x in t is null ? tracks.Values.ToList() : new List<Track> { t }) { x.Degraded = isDegraded; x.Note = isDegraded ? message : null; }
        }
    }

    // ---------------------------------------------------------------- session
    void OnSession(Scope scope, int revision) => Post(() => BeginAsync(scope));

    async Task BeginAsync(Scope scope)
    {
        lock (gate) if (tracks.ContainsKey(scope.SessionId)) return; // already followed
        var snap = await router.SnapshotAsync(scope, stop.Token).ConfigureAwait(false);
        if (!snap.IsOk) return; // the scope moved on already; the next announcement will begin its own session
        var doc = JsonSerializer.SerializeToNode(snap.Value.Document, ContractJson.Options)!;
        var restoredFrom = router.TakeRestoredFrom(snap.Value.SessionId);
        var next = await store.BeginSessionAsync(snap.Value.DocumentId, snap.Value.SessionId, doc, snap.Value.Revision, documentPath is null ? currentPath() : documentPath(snap.Value.DocumentId), Shas(snap.Value.Document), stop.Token, options.FlushDelay, restoredFrom).ConfigureAwait(false);
        var track = new Track { Session = next, DocumentId = snap.Value.DocumentId, LastCommitted = snap.Value.Revision, StartRevision = snap.Value.Revision, KeepUnsaved = restoredFrom is not null };
        List<Track> superseded;
        lock (gate)
        {
            tracks[next.SessionId] = track;
            // a session whose document was replaced by this one is no longer open; if it held nothing worth keeping it goes, otherwise it stays on disk to be offered
            superseded = tracks.Values.Where(t => t != track && router.ScopeOf(t.DocumentId)?.SessionId != t.SessionId).ToList();
            foreach (var t in superseded) tracks.Remove(t.SessionId);
            ready.TrySetResult();
        }
        foreach (var t in superseded) if (t.Untouched) await store.CompleteAsync(t.SessionId).ConfigureAwait(false);
    }

    /// <summary>The page closed a document. Its session goes if there is nothing to keep; unsaved work stays on disk until the person decides (Don't save discards it).</summary>
    /// <summary>A page's renderer died and its documents are gone from the registry. Their sessions stop being followed but stay on disk, to be offered as unfinished work.</summary>
    void OnChannelReset(string channel)
    {
        lock (gate) foreach (var t in tracks.Values.Where(t => router.ScopeOf(t.DocumentId)?.SessionId != t.SessionId).ToList()) tracks.Remove(t.SessionId);
    }

    void OnClosed(Scope scope)
    {
        Track? t;
        lock (gate) tracks.Remove(scope.SessionId, out t); // at once, so a Don't save that follows is not refused as "still open"
        if (t is not null) Post(async () => { if (t.Untouched) await store.CompleteAsync(t.SessionId).ConfigureAwait(false); });
    }

    static IEnumerable<string> Shas(DiagramDocument d) => d.Assets.Select(a => a.Sha256);

    // ---------------------------------------------------------------- commits
    void OnCommitted(CommittedEvent e) => Post(() => AppendAsync(e));

    async Task AppendAsync(CommittedEvent e)
    {
        Track? t;
        lock (gate) { tracks.TryGetValue(e.SessionId, out t); if (t is not null) t.LastCommitted = Math.Max(t.LastCommitted, e.Revision); }
        if (t is null) return; // an event from a session we are not following
        if (e.Revision <= t.Session.Journal.LastAcceptedRevision) return; // already inside the session's checkpoint
        var r = await t.Session.AppendAsync(e, stop.Token).ConfigureAwait(false);
        if (!r.IsOk)
        {
            Mark(t, true, r.Error!.Message);
            // a gap, a write fault or a missing picture: the way back to a sound journal is a fresh checkpoint of the committed state
            await CheckpointAsync(t).ConfigureAwait(false);
            return;
        }
        if (Interlocked.Increment(ref t.CommitsSinceCheckpoint) >= options.CheckpointEveryCommits) await CheckpointAsync(t).ConfigureAwait(false);
    }

    /// <summary>Takes a checkpoint of one document's committed state at one revision (a queue barrier in the editor) and trims its journal behind it.</summary>
    async Task CheckpointAsync(Track t)
    {
        if (router.ScopeOf(t.DocumentId) is not { } scope || scope.SessionId != t.SessionId) return;
        var snap = await router.SnapshotAsync(scope, stop.Token).ConfigureAwait(false);
        if (!snap.IsOk || snap.Value.SessionId != t.SessionId) return;
        var doc = JsonSerializer.SerializeToNode(snap.Value.Document, ContractJson.Options)!;
        await t.Session.CheckpointAsync(doc, snap.Value.Revision, Shas(snap.Value.Document), stop.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref t.CommitsSinceCheckpoint, 0);
        lock (gate) t.LastCommitted = Math.Max(t.LastCommitted, snap.Value.Revision);
        Mark(t, false, null);
    }

    async Task CheckpointAllAsync(bool onlyChanged)
    {
        List<Track> all; lock (gate) all = tracks.Values.ToList();
        foreach (var t in all) if (!onlyChanged || Volatile.Read(ref t.CommitsSinceCheckpoint) > 0) await CheckpointAsync(t).ConfigureAwait(false);
    }

    Task CheckpointAllAsync() => CheckpointAllAsync(true);

    public Task CheckpointNowAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(async () => { try { await CheckpointAllAsync(false).ConfigureAwait(false); } finally { done.TrySetResult(); } })) done.TrySetResult();
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
                Track? t; lock (gate) tracks.TryGetValue(sessionId, out t);
                if (t is null) return;
                lock (gate) { t.SavedRevision = Math.Max(t.SavedRevision, revision); t.KeepUnsaved = false; }
                await store.SetSavedAsync(sessionId, revision, path, stop.Token).ConfigureAwait(false);
                await CheckpointAsync(t).ConfigureAwait(false);
            }
            finally { done.TrySetResult(); }
        })) done.TrySetResult();
        return done.Task;
    }

    // ---------------------------------------------------------------- finding and finishing work
    /// <summary>
    /// Unfinished sessions other than the ones open now that hold changes nobody saved. Clean or empty ones are removed on the way.
    /// The editor's open sessions are the ones the page announced, which after a crash can be ahead of the sessions this manager is still following.
    /// </summary>
    public async Task<IReadOnlyList<RecoveryCandidate>> CandidatesAsync(CancellationToken ct)
    {
        var open = router.Documents.Select(d => d.SessionId).ToHashSet(StringComparer.Ordinal);
        var onDisk = await store.ReadRecoveryAsync(null, ct).ConfigureAwait(false);
        var hidden = await RemoveSupersededAsync(onDisk).ConfigureAwait(false);
        var all = onDisk.Where(c => !open.Contains(c.SessionId) && !hidden.Contains(c.SessionId)).ToList();
        var offer = new List<RecoveryCandidate>();
        foreach (var c in all)
        {
            var nothingToKeep = (c.LastDurableRevision == 0 && c.CheckpointRevision == 0 && !c.KeepUnsaved) || c.LastDurableRevision <= (c.SavedRevision ?? -1); // a blank page nobody touched, or work that was saved
            if (nothingToKeep) { await store.CompleteAsync(c.SessionId).ConfigureAwait(false); continue; }
            offer.Add(c);
        }
        return offer;
    }

    /// <summary>
    /// Told, before a superseded session is removed, which session replaces it. The shell uses it to point a remembered tab at the new session, so the tab
    /// is restored from the work that was recovered and not reopened from its file.
    /// </summary>
    public Func<string, string, Task>? OnSuperseded { get; set; }

    /// <summary>
    /// A session that a listed session was rebuilt from is already inside that session, which has a durable checkpoint of its own. A crash between the
    /// rebuild and the end of the restore leaves both on disk; the old one is removed here (after the shell has been told) so it is never offered, or restored, a second time.
    /// </summary>
    /// <summary>Does that clean-up on its own, before anything else reads the remembered tabs.</summary>
    public async Task ReconcileAsync(CancellationToken ct) => await RemoveSupersededAsync(await store.ReadRecoveryAsync(null, ct).ConfigureAwait(false)).ConfigureAwait(false);

    /// <returns>The superseded sessions: removed from disk, or, when the shell could not be told, left there but not to be offered.</returns>
    async Task<HashSet<string>> RemoveSupersededAsync(IReadOnlyList<RecoveryCandidate> onDisk)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (old, successor) in RecoveryLineage.Superseded(onDisk))
        {
            lock (gate) if (tracks.ContainsKey(old)) continue; // still an open document's own session
            try { if (OnSuperseded is { } tell) await tell(old, successor).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { removed.Add(old); continue; } // never offered again, but kept on disk until the tab can be pointed at its replacement
            await store.CompleteAsync(old).ConfigureAwait(false);
            removed.Add(old);
        }
        return removed;
    }

    /// <summary>Preserves one explicit unsaved document even at revision zero, after its first checkpoint is durable.</summary>
    public async Task KeepCurrentUnsavedAsync(string sessionId, CancellationToken ct)
    {
        await DrainAsync().ConfigureAwait(false);
        Track? track; lock (gate) tracks.TryGetValue(sessionId, out track);
        if (track is null) throw new InvalidOperationException("the new recovery session is not ready");
        try
        {
            await store.KeepUnsavedAsync(sessionId, ct).ConfigureAwait(false);
            lock (gate) track.KeepUnsaved = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Mark(track, true, "recovery could not preserve the new document: " + e.Message); throw; }
    }
    public Task DiscardAsync(string sessionId) => store.CompleteAsync(sessionId);

    /// <summary>The restored document now has its own session and checkpoint, so the old session can go.</summary>
    public async Task FinishRestoreAsync(string oldSessionId)
    {
        await WhenSessionReadyAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        lock (gate) if (tracks.ContainsKey(oldSessionId)) return; // still an open document's session
        var source = (await store.ReadRecoveryAsync(null, stop.Token).ConfigureAwait(false)).FirstOrDefault(c => c.SessionId == oldSessionId);
        if (source?.KeepUnsaved == true)
        {
            var target = router.ScopeOf(source.DocumentId);
            if (target is null || target.SessionId == oldSessionId) throw new InvalidOperationException("the restored document is not ready");
            await KeepCurrentUnsavedAsync(target.SessionId, stop.Token).ConfigureAwait(false);
        }
        await store.CompleteAsync(oldSessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// The person chose not to save one document's changes: its session is thrown away, so the next start does not offer them.
    /// Anything still queued is written first and then removed with the rest, and later commits from this session are ignored.
    /// </summary>
    public async Task DiscardSessionAsync(string sessionId)
    {
        await DrainAsync().ConfigureAwait(false);
        Track? t;
        lock (gate) { tracks.Remove(sessionId, out t); if (tracks.Count == 0) ready = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        await store.CompleteAsync(sessionId).ConfigureAwait(false); // also for a document already closed: its session stayed on disk waiting for this answer
    }

    /// <summary>Discards every open document's session: the person closed the window and chose to save nothing.</summary>
    public async Task DiscardAllAsync()
    {
        foreach (var id in OpenSessions) await DiscardSessionAsync(id).ConfigureAwait(false);
    }

    /// <summary>Discards the active document's session.</summary>
    public async Task DiscardCurrentAsync()
    {
        var id = router.CurrentScope?.SessionId ?? SessionId;
        if (id is not null) await DiscardSessionAsync(id).ConfigureAwait(false); else await DrainAsync().ConfigureAwait(false);
    }

    /// <summary>Clean close: each session is removed only if there is nothing unsaved in it.</summary>
    public async Task CloseCleanlyAsync()
    {
        await DrainAsync().ConfigureAwait(false);
        List<Track> all; lock (gate) all = tracks.Values.ToList();
        foreach (var t in all) if (t.Untouched) await store.CompleteAsync(t.SessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes picture bytes that no document, library entry, undo history or recovery session needs. Recent uploads are left alone.
    /// Every open document is asked what it still holds; a document that cannot answer means nothing can be proven unused.
    /// </summary>
    public async Task<BlobGcResult> CollectGarbageAsync(IEnumerable<string> libraryHashes, TimeSpan grace, CancellationToken ct)
    {
        var live = new HashSet<string>(store.ReferencedBlobs(), StringComparer.Ordinal);
        foreach (var h in libraryHashes) live.Add(h);
        var open = router.Documents;
        if (open.Count == 0) return BlobGcResult.None;
        foreach (var scope in open)
        {
            var r = await router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "doc.retainedBlobs", DocumentId = scope.DocumentId, SessionId = scope.SessionId, Params = new JsonObject() }, ct).ConfigureAwait(false);
            if (r.Error is not null || r.Result is not { } res) return BlobGcResult.None; // without the editor's answer nothing can be proven unused
            foreach (var h in res.GetProperty("hashes").EnumerateArray()) live.Add(h.GetString()!);
        }
        return await BlobGc.CollectAsync(blobs, live, grace, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) stopping = true;
        router.Committed -= OnCommitted;
        router.SessionChanged -= OnSession;
        router.DocumentClosed -= OnClosed;
        router.ChannelReset -= OnChannelReset;
        await timer.DisposeAsync().ConfigureAwait(false);
        work.Writer.TryComplete();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
        stop.Cancel();
    }
}
