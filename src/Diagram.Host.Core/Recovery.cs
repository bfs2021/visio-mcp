using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

/// <summary>The newest commit that is on disk, as opposed to merely accepted by the live editor.</summary>
public readonly record struct DurableRevision(int Sequence, int Revision);

/// <summary>What reading a journal found: the valid records in order, and what had to be discarded and why.</summary>
public sealed record JournalRead(IReadOnlyList<JournalRecord> Records, bool TornTailDiscarded, int? CorruptionAfterRevision, string? Detail);

/// <param name="Event">The committed event exactly as the editor emitted it (resolved diff, generated ids and all). The host never interprets it.</param>
public sealed record JournalRecord(int Sequence, int Revision, JsonObject Event);

/// <summary>
/// An append-only record of resolved committed changes for one editor session. A record is acknowledged only once it is on disk (batched
/// at most 250 ms), never refers to picture bytes that are not stored, and has to continue the revision chain without a gap.
/// </summary>
public sealed class RecoveryJournal
{
    public static readonly TimeSpan MaxFlushDelay = TimeSpan.FromMilliseconds(250);
    readonly string path;
    readonly BlobStore blobs;
    readonly TimeSpan flushDelay;
    readonly object gate = new();
    readonly SemaphoreSlim io = new(1, 1);
    readonly AtomicFileWriter writer = new();
    List<(string Line, int Seq, int Rev, TaskCompletionSource<Result<DurableRevision>> Done)> pending = [];
    int accepted, lastSequence, lastDurable;
    bool flusherRunning;

    public RecoveryJournal(string path, BlobStore blobs, int baseRevision, TimeSpan? flushDelay = null)
    {
        this.path = path; this.blobs = blobs;
        this.flushDelay = flushDelay is { } d && d < MaxFlushDelay ? d : MaxFlushDelay;
        accepted = lastDurable = baseRevision;
    }

    /// <summary>Test seam: runs after a batch is chosen and before it is written.</summary>
    public Action? BeforeFlush { get; set; }
    /// <summary>Set after a write failure: nothing more is accepted until a checkpoint rebases the journal.</summary>
    public bool Faulted { get; private set; }
    public int LastDurableRevision { get { lock (gate) return lastDurable; } }
    public int LastAcceptedRevision { get { lock (gate) return accepted; } }

    static string Sum(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    static IEnumerable<string> BlobsIn(JsonNode? ev) =>
        ev?["resolvedDiff"]?["upserts"]?["assets"] is JsonArray a ? a.Select(x => x?["sha256"]?.GetValue<string>()).Where(x => x is not null).Select(x => x!) : [];

    public Task<Result<DurableRevision>> AppendDurableAsync(CommittedEvent e, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(e, ContractJson.Options)!.AsObject();
        // a record may only name bytes that are already durable
        foreach (var sha in BlobsIn(node))
            if (sha.Length != 64 || !blobs.Contains(sha))
                return Task.FromResult(Result<DurableRevision>.Fail("blob_not_durable", $"revision {e.Revision} refers to a picture ({sha[..Math.Min(12, sha.Length)]}) that is not stored, so it was not journalled", retryable: true));
        var payload = node.ToJsonString();
        var line = new JsonObject { ["seq"] = e.Sequence, ["rev"] = e.Revision, ["sum"] = Sum(payload), ["event"] = JsonNode.Parse(payload) }.ToJsonString();
        var done = new TaskCompletionSource<Result<DurableRevision>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (Faulted) return Task.FromResult(Result<DurableRevision>.Fail("journal_faulted", "the journal could not be written; a checkpoint is needed first", retryable: true));
            if (e.Revision != accepted + 1)
                return Task.FromResult(Result<DurableRevision>.Fail("revision_gap", $"the journal is at revision {accepted}, so revision {e.Revision} does not continue it", retryable: true, details: new JsonObject { ["expected"] = accepted + 1, ["actual"] = e.Revision }));
            if (lastSequence != 0 && e.Sequence <= lastSequence)
                return Task.FromResult(Result<DurableRevision>.Fail("sequence_gap", $"sequence {e.Sequence} is not after {lastSequence}", retryable: true));
            accepted = e.Revision; lastSequence = e.Sequence;
            pending.Add((line, e.Sequence, e.Revision, done));
            if (!flusherRunning) { flusherRunning = true; _ = Task.Run(FlusherAsync); }
        }
        return done.Task;
    }

    async Task FlusherAsync()
    {
        await Task.Delay(flushDelay).ConfigureAwait(false); // batch whatever arrives in this window
        await io.WaitAsync().ConfigureAwait(false);
        try { await FlushPendingAsync().ConfigureAwait(false); }
        finally { io.Release(); lock (gate) { flusherRunning = false; if (pending.Count > 0) { flusherRunning = true; _ = Task.Run(FlusherAsync); } } }
    }

    /// <summary>Writes everything accepted so far. The caller holds <c>io</c>.</summary>
    async Task FlushPendingAsync()
    {
        List<(string Line, int Seq, int Rev, TaskCompletionSource<Result<DurableRevision>> Done)> batch;
        lock (gate) { batch = pending; pending = []; }
        if (batch.Count == 0) return;
        try
        {
            BeforeFlush?.Invoke();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(string.Concat(batch.Select(b => b.Line + "\n")));
                await fs.WriteAsync(bytes).ConfigureAwait(false);
                await fs.FlushAsync().ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            lock (gate) lastDurable = batch[^1].Rev;
            foreach (var b in batch) b.Done.TrySetResult(Result<DurableRevision>.Ok(new DurableRevision(b.Seq, b.Rev)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (gate) Faulted = true;
            foreach (var b in batch) b.Done.TrySetResult(Result<DurableRevision>.Fail("io_error", "the recovery journal could not be written: " + ex.Message, retryable: true));
        }
    }

    /// <summary>Waits until everything accepted so far is on disk (used before a checkpoint reads the file).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try { await FlushPendingAsync().ConfigureAwait(false); } finally { io.Release(); }
    }

    /// <summary>
    /// After a checkpoint at revision <paramref name="throughRevision"/> is published: drop records up to it and keep every later one,
    /// including any committed while the checkpoint was being written. Also the way out of a gap or a write fault.
    /// </summary>
    public async Task RebaseAsync(int throughRevision, CancellationToken ct)
    {
        await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Faulted) await FlushPendingAsync().ConfigureAwait(false);
            var read = ReadFile(path);
            var keep = read.Records.Where(r => r.Revision > throughRevision).ToList();
            var contiguous = new List<JournalRecord>();
            var expect = throughRevision + 1;
            foreach (var r in keep) { if (r.Revision != expect) break; contiguous.Add(r); expect++; }
            var text = string.Concat(contiguous.Select(Format));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await writer.WriteAsync(path, Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
            lock (gate)
            {
                // anything still queued (accepted but not yet written) continues the chain after the kept records
                var tail = contiguous.Count > 0 ? contiguous[^1].Revision : throughRevision;
                if (Faulted) { pending = []; accepted = Math.Max(tail, throughRevision); }
                else accepted = Math.Max(accepted, tail);
                lastDurable = Math.Max(tail, throughRevision);
                Faulted = false;
            }
        }
        finally { io.Release(); }
    }

    static string Format(JournalRecord r)
    {
        var payload = r.Event.ToJsonString();
        return new JsonObject { ["seq"] = r.Sequence, ["rev"] = r.Revision, ["sum"] = Sum(payload), ["event"] = JsonNode.Parse(payload) }.ToJsonString() + "\n";
    }

    /// <summary>
    /// Reads a journal. A bad last line is a torn tail from a crash and is dropped silently; a bad line with good lines after it is real
    /// corruption, and reading stops at the last good record before it.
    /// </summary>
    public static JournalRead ReadFile(string path)
    {
        if (!File.Exists(path)) return new JournalRead([], false, null, null);
        var text = File.ReadAllText(path, Encoding.UTF8);
        var lines = text.Split('\n');
        var complete = text.EndsWith('\n');
        var records = new List<JournalRecord>();
        bool torn = false; int? corruptAfter = null; string? detail = null;
        var bad = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) { if (i == lines.Length - 1) break; bad = i; break; }
            var isLast = i == lines.Length - 1; // no newline after it: cut off mid-write
            var rec = isLast && !complete ? null : TryParse(line);
            if (rec is null) { bad = i; break; }
            if (records.Count > 0 && rec.Revision != records[^1].Revision + 1) { bad = i; detail = $"revision {rec.Revision} follows {records[^1].Revision}"; break; }
            records.Add(rec);
        }
        if (bad >= 0)
        {
            var rest = lines.Skip(bad + 1).Where(l => l.Length > 0).ToList();
            var lastRev = records.Count > 0 ? records[^1].Revision : (int?)null;
            if (rest.Count == 0 && detail is null) { torn = true; }
            else { corruptAfter = lastRev ?? 0; detail ??= "a record in the middle of the journal is damaged"; }
        }
        return new JournalRead(records, torn, corruptAfter, detail);
    }

    static JournalRecord? TryParse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o || o["event"] is not JsonObject ev || o["sum"]?.GetValue<string>() is not { } sum) return null;
            if (!string.Equals(Sum(ev.ToJsonString()), sum, StringComparison.Ordinal)) return null;
            return new JournalRecord(o["seq"]!.GetValue<int>(), o["rev"]!.GetValue<int>(), ev.DeepClone().AsObject());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

public sealed record Checkpoint(int Generation, int Revision, string DocumentId, string SessionId, JsonNode Document, IReadOnlyList<string> BlobRefs);

/// <summary>
/// Immutable checkpoint generations plus a manifest that is replaced atomically. A crash before the manifest leaves the previous
/// generation current; a crash after it leaves the new one. The previous generation is kept until the new manifest is on disk.
/// </summary>
public sealed class CheckpointStore
{
    readonly string dir;
    readonly AtomicFileWriter writer = new();
    public CheckpointStore(string dir) { this.dir = dir; Directory.CreateDirectory(dir); }

    /// <summary>Test seams for crash timing: after the generation file is durable but before the manifest names it, and just after.</summary>
    public Action? BeforeManifest { get; set; }
    public Action? AfterManifest { get; set; }
    /// <summary>Test seam: runs while the generation is being written, the window in which newer commits keep arriving.</summary>
    public Func<Task>? DuringWrite { get; set; }

    string Gen(int n) => Path.Combine(dir, $"gen-{n:D6}.json");
    string Manifest => Path.Combine(dir, "manifest.json");
    static string Sum(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public async Task<int> PublishAsync(string documentId, string sessionId, int revision, JsonNode document, IEnumerable<string> blobRefs, CancellationToken ct)
    {
        var current = ReadManifest();
        var next = (current?.Current ?? ScanGenerations().DefaultIfEmpty(0).Max()) + 1;
        var payload = new JsonObject
        {
            ["generation"] = next, ["revision"] = revision, ["documentId"] = documentId, ["sessionId"] = sessionId,
            ["blobRefs"] = new JsonArray(blobRefs.Distinct(StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray()), ["document"] = document.DeepClone(),
        };
        var text = payload.ToJsonString();
        var file = new JsonObject { ["sum"] = Sum(text), ["payload"] = JsonNode.Parse(text) }.ToJsonString();
        await writer.WriteAsync(Gen(next), Encoding.UTF8.GetBytes(file), ct).ConfigureAwait(false);
        if (DuringWrite is not null) await DuringWrite().ConfigureAwait(false);
        BeforeManifest?.Invoke();
        var manifest = new JsonObject { ["current"] = next, ["previous"] = current?.Current ?? 0, ["revision"] = revision }.ToJsonString();
        await writer.WriteAsync(Manifest, Encoding.UTF8.GetBytes(manifest), ct).ConfigureAwait(false);
        AfterManifest?.Invoke();
        // only now is the older history unneeded: keep the generation before this one
        foreach (var g in ScanGenerations().Where(g => g < (current?.Current ?? 0))) { try { File.Delete(Gen(g)); } catch (IOException) { } }
        return next;
    }

    record Man(int Current, int Previous);

    Man? ReadManifest()
    {
        try
        {
            if (!File.Exists(Manifest)) return null;
            var o = JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
            return new Man(o["current"]!.GetValue<int>(), o["previous"]!.GetValue<int>());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException) { return null; }
    }

    IEnumerable<int> ScanGenerations() =>
        Directory.EnumerateFiles(dir, "gen-*.json").Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f)[4..], out var n) ? n : 0).Where(n => n > 0);

    Checkpoint? Load(int n)
    {
        try
        {
            if (!File.Exists(Gen(n))) return null;
            var o = JsonNode.Parse(File.ReadAllText(Gen(n)))!.AsObject();
            var payload = o["payload"]!.AsObject();
            if (!string.Equals(Sum(payload.ToJsonString()), o["sum"]!.GetValue<string>(), StringComparison.Ordinal)) return null;
            return new Checkpoint(payload["generation"]!.GetValue<int>(), payload["revision"]!.GetValue<int>(), payload["documentId"]!.GetValue<string>(), payload["sessionId"]!.GetValue<string>(),
                payload["document"]!.DeepClone(), payload["blobRefs"]!.AsArray().Select(x => x!.GetValue<string>()).ToList());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException or NullReferenceException) { return null; }
    }

    /// <summary>The generation the manifest names, else the one before it, else the newest generation that checks out.</summary>
    public Checkpoint? ReadLatest()
    {
        var m = ReadManifest();
        if (m is not null)
        {
            if (Load(m.Current) is { } c) return c;
            if (m.Previous > 0 && Load(m.Previous) is { } p) return p;
        }
        foreach (var g in ScanGenerations().OrderByDescending(x => x)) if (Load(g) is { } any) return any;
        return null;
    }

    /// <summary>Picture hashes named by the generations this store still keeps.</summary>
    public IEnumerable<string> BlobRefs() => ScanGenerations().Select(Load).Where(c => c is not null).SelectMany(c => c!.BlobRefs);
}

/// <summary>One editor session's recovery state: its journal, its checkpoints and a little metadata.</summary>
public sealed class RecoverySession
{
    internal RecoverySession(string dir, string documentId, string sessionId, RecoveryJournal journal, CheckpointStore checkpoints)
    { Dir = dir; DocumentId = documentId; SessionId = sessionId; Journal = journal; Checkpoints = checkpoints; }

    public string Dir { get; }
    public string DocumentId { get; }
    public string SessionId { get; }
    public RecoveryJournal Journal { get; }
    public CheckpointStore Checkpoints { get; }
    public int LastDurableRevision => Journal.LastDurableRevision;

    public Task<Result<DurableRevision>> AppendAsync(CommittedEvent e, CancellationToken ct)
    {
        if (e.SessionId != SessionId || e.DocumentId != DocumentId)
            return Task.FromResult(Result<DurableRevision>.Fail("session_mismatch", "the event belongs to a different session"));
        return Journal.AppendDurableAsync(e, ct);
    }

    /// <summary>Publishes a checkpoint of <paramref name="revision"/>, then trims the journal to what is newer. Commits that arrive during the write are kept.</summary>
    public async Task<int> CheckpointAsync(JsonNode document, int revision, IEnumerable<string> blobRefs, CancellationToken ct)
    {
        var gen = await Checkpoints.PublishAsync(DocumentId, SessionId, revision, document, blobRefs, ct).ConfigureAwait(false);
        await Journal.RebaseAsync(revision, ct).ConfigureAwait(false);
        return gen;
    }
}

public sealed record RecoveryLoss(bool TornTailDiscarded, int? CorruptionAfterRevision, IReadOnlyList<string> Messages);

/// <summary>Everything needed to rebuild the last durable state of one unfinished session. The page validates and applies it; the host never replays.</summary>
public sealed record RecoveryCandidate(
    string SessionId, string DocumentId, string? OriginalPath, int CheckpointRevision, JsonNode Document, IReadOnlyList<JsonObject> Events, int LastDurableRevision, RecoveryLoss Loss, DateTimeOffset LastWrite, int? SavedRevision = null, bool KeepUnsaved = false);

/// <summary>The folder of recovery sessions, one sub-folder per editor session.</summary>
public sealed class RecoveryStore(string root, BlobStore blobs)
{
    string SessionDir(string id) => Path.Combine(root, id);

    public async Task<RecoverySession> BeginSessionAsync(string documentId, string sessionId, JsonNode document, int revision, string? originalPath, IEnumerable<string> blobRefs, CancellationToken ct, TimeSpan? flushDelay = null)
    {
        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(dir);
        await new AtomicFileWriter().WriteAsync(Path.Combine(dir, "session.json"),
            Encoding.UTF8.GetBytes(new JsonObject { ["documentId"] = documentId, ["sessionId"] = sessionId, ["originalPath"] = originalPath, ["startedAt"] = DateTimeOffset.UtcNow.ToString("O") }.ToJsonString()), ct).ConfigureAwait(false);
        var store = new CheckpointStore(dir);
        await store.PublishAsync(documentId, sessionId, revision, document, blobRefs, ct).ConfigureAwait(false); // its own checkpoint first: it never continues another session's chain
        var journal = new RecoveryJournal(Path.Combine(dir, "journal.log"), blobs, revision, flushDelay);
        return new RecoverySession(dir, documentId, sessionId, journal, store);
    }

    /// <summary>Records that <paramref name="revision"/> reached disk at <paramref name="path"/>: a crash afterwards has nothing worth recovering up to that revision.</summary>
    public async Task SetSavedAsync(string sessionId, int revision, string? path, CancellationToken ct)
    {
        var file = Path.Combine(SessionDir(sessionId), "session.json");
        if (!File.Exists(file)) return;
        var o = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false))!.AsObject();
        o["originalPath"] = path;
        o["savedRevision"] = revision;
        o["keepUnsaved"] = false;
        await new AtomicFileWriter().WriteAsync(file, Encoding.UTF8.GetBytes(o.ToJsonString()), ct).ConfigureAwait(false);
    }

    /// <summary>An explicit New document is unsaved work even before its first edit. This is session state, not canonical drawing metadata.</summary>
    public async Task KeepUnsavedAsync(string sessionId, CancellationToken ct)
    {
        var file = Path.Combine(SessionDir(sessionId), "session.json");
        var meta = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false))!.AsObject();
        meta["keepUnsaved"] = true;
        await new AtomicFileWriter().WriteAsync(file, Encoding.UTF8.GetBytes(meta.ToJsonString()), ct).ConfigureAwait(false);
    }
    /// <summary>The unfinished sessions on disk, newest first. A session that was closed cleanly has been removed and is not listed.</summary>
    public Task<IReadOnlyList<RecoveryCandidate>> ReadRecoveryAsync(string? excludeSessionId, CancellationToken ct)
    {
        var found = new List<RecoveryCandidate>();
        if (Directory.Exists(root))
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var id = Path.GetFileName(dir);
                if (id == excludeSessionId) continue;
                if (Read(dir) is { } c) found.Add(c);
            }
        return Task.FromResult<IReadOnlyList<RecoveryCandidate>>(found.OrderByDescending(c => c.LastWrite).ToList());
    }

    RecoveryCandidate? Read(string dir)
    {
        var cp = new CheckpointStore(dir).ReadLatest();
        if (cp is null) return null; // no usable checkpoint: nothing to rebuild from
        string? original = null; int? saved = null; bool keepUnsaved = false;
        try
        {
            if (File.Exists(Path.Combine(dir, "session.json")))
            {
                var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")));
                original = meta?["originalPath"]?.GetValue<string>();
                saved = meta?["savedRevision"]?.GetValue<int>();
                keepUnsaved = meta?["keepUnsaved"]?.GetValue<bool>() ?? false;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { }
        var read = RecoveryJournal.ReadFile(Path.Combine(dir, "journal.log"));
        var events = new List<JsonObject>();
        var expect = cp.Revision + 1;
        var messages = new List<string>();
        int? corruptAfter = read.CorruptionAfterRevision;
        foreach (var r in read.Records)
        {
            if (r.Revision <= cp.Revision) continue; // already inside the checkpoint
            if (r.Revision != expect) { corruptAfter ??= expect - 1; messages.Add($"the journal does not continue the checkpoint at revision {cp.Revision}: next is {r.Revision}, expected {expect}"); break; }
            events.Add(r.Event); expect++;
        }
        if (read.TornTailDiscarded) messages.Add("the last journal record was cut off by the crash and was discarded");
        if (read.CorruptionAfterRevision is { } after) messages.Add($"the journal is damaged after revision {after}; later changes could not be recovered ({read.Detail})");
        var last = events.Count > 0 ? events[^1]["revision"]!.GetValue<int>() : cp.Revision;
        var write = new[] { "journal.log", "manifest.json" }.Select(f => Path.Combine(dir, f)).Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir)).Max();
        return new RecoveryCandidate(cp.SessionId, cp.DocumentId, original, cp.Revision, cp.Document, events, last, new RecoveryLoss(read.TornTailDiscarded, corruptAfter, messages), new DateTimeOffset(write, TimeSpan.Zero), saved, keepUnsaved);
    }

    /// <summary>Removes a finished session (after a safe save, or after the person chose to discard it).</summary>
    public Task CompleteAsync(string sessionId)
    {
        var dir = SessionDir(sessionId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        return Task.CompletedTask;
    }

    /// <summary>Picture hashes that unfinished sessions still need: their retained checkpoints and every journal record.</summary>
    public IReadOnlySet<string> ReferencedBlobs()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return set;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            foreach (var r in new CheckpointStore(dir).BlobRefs()) set.Add(r);
            foreach (var rec in RecoveryJournal.ReadFile(Path.Combine(dir, "journal.log")).Records)
                if (rec.Event["resolvedDiff"]?["upserts"]?["assets"] is JsonArray a)
                    foreach (var x in a) if (x?["sha256"]?.GetValue<string>() is { } sha) set.Add(sha);
        }
        return set;
    }
}

/// <summary>Removes picture bytes nobody needs any more. "Needs" is decided by the caller: documents, the library, undo history and recovery.</summary>
/// <summary>What one collection freed.</summary>
public readonly record struct BlobGcResult(int Removed, long Bytes)
{
    public static readonly BlobGcResult None = new(0, 0);
}

public static class BlobGc
{
    /// <summary>Deletes every picture outside <paramref name="live"/> that nothing has stored or touched within <paramref name="grace"/>.</summary>
    public static async Task<BlobGcResult> CollectAsync(BlobStore blobs, IEnumerable<string> live, TimeSpan grace, CancellationToken ct = default)
    {
        var keep = new HashSet<string>(live, StringComparer.Ordinal);
        var cutoff = DateTime.UtcNow - grace;
        int removed = 0; long bytes = 0;
        foreach (var (sha, written) in blobs.Enumerate().ToList())
        {
            if (keep.Contains(sha) || written >= cutoff) continue;
            // touched again since it was listed (handed to a new upload): kept, the store checks under its own lock
            if (await blobs.DeleteIfIdleAsync(sha, cutoff, ct).ConfigureAwait(false) is { } freed) { removed++; bytes += freed; }
        }
        return new BlobGcResult(removed, bytes);
    }
}


