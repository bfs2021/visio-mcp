using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class RecoveryTests
{
    [Fact]
    public async Task ExplicitUntitledMarkerSurvivesRecoveryMetadataRoundTrip()
    {
        using var env = new Env(); var session = await env.Begin();
        var file = Path.Combine(session.Dir, "session.json");
        var meta = JsonNode.Parse(await File.ReadAllTextAsync(file))!; meta["keepUnsaved"] = true;
        await File.WriteAllTextAsync(file, meta.ToJsonString());
        var candidate = Assert.Single(await env.Store.ReadRecoveryAsync(null, default));
        Assert.True(JsonSerializer.SerializeToNode(candidate)!["KeepUnsaved"]?.GetValue<bool>() ?? false);
    }
    [Fact]
    public async Task SavingAnExplicitBlankClearsTheUnsavedMarker()
    {
        using var env = new Env(); await env.Begin();
        await env.Store.KeepUnsavedAsync(Sess, default);
        Assert.True(Assert.Single(await env.Store.ReadRecoveryAsync(null, default)).KeepUnsaved);
        await env.Store.SetSavedAsync(Sess, 0, "C:/plan.diagram.json", default);
        var saved = Assert.Single(await env.Store.ReadRecoveryAsync(null, default));
        Assert.False(saved.KeepUnsaved); Assert.Equal(0, saved.SavedRevision);
    }
    const string Doc = "00000000-0000-4000-8000-0000000000d1";
    const string Sess = "00000000-0000-4000-8000-0000000000e1";
    static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly BlobStore Blobs;
        public readonly RecoveryStore Store;
        public Env() { Blobs = new BlobStore(Dir.File("blobs")); Store = new RecoveryStore(Dir.File("recovery"), Blobs); }
        public Task<RecoverySession> Begin(string session = Sess, int revision = 0) => Store.BeginSessionAsync(Doc, session, DocJson(revision), revision, null, [], default, Fast);
        public void Dispose() => Dir.Dispose();
    }

    static JsonNode DocJson(int revision) => new JsonObject { ["schemaVersion"] = 1, ["id"] = Doc, ["title"] = "T", ["revision"] = revision, ["pages"] = new JsonArray(), ["assets"] = new JsonArray(), ["metadata"] = new JsonObject() };

    static CommittedEvent Ev(int rev, string session = Sess, IReadOnlyList<Asset>? assets = null, IReadOnlyList<string>? deletedAssets = null) => new()
    {
        Sequence = rev, DocumentId = Doc, SessionId = session, Revision = rev,
        ResolvedDiff = new ResolvedDiff
        {
            TransactionId = Guid.NewGuid().ToString(), Source = "gui", PreviousRevision = rev - 1, Revision = rev,
            Upserts = new DiffUpserts { Elements = [], Pages = [], Layers = [], Assets = assets ?? [] },
            Deletes = new DiffDeletes { ElementIds = [], PageIds = [], LayerIds = [], AssetIds = deletedAssets ?? [] },
        },
    };

    static async Task AppendAll(RecoverySession s, params int[] revisions)
    {
        foreach (var r in revisions) Assert.True((await s.AppendAsync(Ev(r), default)).IsOk);
    }

    static Asset PictureAsset(string sha) => new() { Id = "asset:logo", Name = "logo", MimeType = "image/png", Sha256 = sha, Tags = [] };

    [Fact]
    public async Task BlobBeforeRecord()
    {
        using var env = new Env();
        var s = await env.Begin();
        var sha = BlobStore.HashOf("bytes"u8);
        // the picture is not stored yet: the record is refused and nothing is written
        var refused = await s.AppendAsync(Ev(1, assets: [PictureAsset(sha)]), default);
        Assert.Equal("blob_not_durable", refused.Error!.Code);
        Assert.False(File.Exists(Path.Combine(s.Dir, "journal.log")));
        Assert.Equal(0, s.LastDurableRevision);
        // once the bytes are durable the same revision goes in
        await env.Blobs.PutDurableAsync("bytes"u8.ToArray(), default);
        var ok = await s.AppendAsync(Ev(1, assets: [PictureAsset(sha)]), default);
        Assert.True(ok.IsOk);
        Assert.Equal(1, s.LastDurableRevision);
        // a record is acknowledged only after it is on disk
        Assert.Contains(sha, File.ReadAllText(Path.Combine(s.Dir, "journal.log")));
    }

    [Fact]
    public async Task AcknowledgementWaitsForTheDiskAndIsBatchedWithinTheLimit()
    {
        using var env = new Env();
        var s = await env.Begin();
        var gate = new TaskCompletionSource();
        s.Journal.BeforeFlush = () => gate.Task.GetAwaiter().GetResult();
        var first = s.AppendAsync(Ev(1), default);
        var second = s.AppendAsync(Ev(2), default);
        await Task.Delay(100);
        Assert.False(first.IsCompleted); // not durable, so not acknowledged
        Assert.Equal(0, s.LastDurableRevision);
        gate.SetResult();
        Assert.True((await first).IsOk && (await second).IsOk);
        Assert.Equal(2, s.LastDurableRevision);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(s.Dir, "journal.log")).Length);

        // the delay never exceeds 250 ms even if asked for more
        var slow = new RecoveryJournal(env.Dir.File("slow.log"), env.Blobs, 0, TimeSpan.FromSeconds(30));
        var started = DateTime.UtcNow;
        Assert.True((await slow.AppendDurableAsync(Ev(1), default)).IsOk);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ARevisionGapIsRefusedSoTheJournalNeverHasAHole()
    {
        using var env = new Env();
        var s = await env.Begin();
        await AppendAll(s, 1, 2);
        var gap = await s.AppendAsync(Ev(4), default);
        Assert.Equal("revision_gap", gap.Error!.Code);
        Assert.Equal(2, s.LastDurableRevision);
        // a checkpoint at the current revision is the way forward
        await s.CheckpointAsync(DocJson(4), 4, [], default);
        await AppendAll(s, 5);
        var c = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        Assert.Equal(4, c.CheckpointRevision);
        Assert.Equal(5, c.LastDurableRevision);
        Assert.Empty(c.Loss.Messages);
    }

    [Fact]
    public async Task CheckpointDoesNotLoseTailRPlus1()
    {
        using var env = new Env();
        var s = await env.Begin();
        await AppendAll(s, 1, 2, 3, 4, 5);
        // while the checkpoint of revision 3 is being written, revision 6 is committed
        s.Checkpoints.DuringWrite = async () => { Assert.True((await s.AppendAsync(Ev(6), default)).IsOk); };
        await s.CheckpointAsync(DocJson(3), 3, [], default);
        var c = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        Assert.Equal(3, c.CheckpointRevision);
        Assert.Equal([4, 5, 6], c.Events.Select(e => e["revision"]!.GetValue<int>()));
        Assert.Equal(6, c.LastDurableRevision);
        Assert.Empty(c.Loss.Messages);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task CrashBeforeAfterManifest(string when)
    {
        using var env = new Env();
        var s = await env.Begin();
        await AppendAll(s, 1, 2, 3);
        if (when == "before") s.Checkpoints.BeforeManifest = () => throw new IOException("power cut");
        else s.Checkpoints.AfterManifest = () => throw new IOException("power cut");
        await Assert.ThrowsAsync<IOException>(() => s.CheckpointAsync(DocJson(2), 2, [], default));

        var c = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        // either way the journal still holds everything after revision 0 or 2, so nothing committed is lost
        Assert.Equal(3, c.LastDurableRevision);
        Assert.Equal(when == "before" ? 0 : 2, c.CheckpointRevision);
        Assert.Equal(when == "before" ? [1, 2, 3] : [3], c.Events.Select(e => e["revision"]!.GetValue<int>()));
        Assert.Empty(c.Loss.Messages);
    }

    [Fact]
    public async Task TornTailVsMiddleCorruption()
    {
        using var env = new Env();
        var s = await env.Begin();
        await AppendAll(s, 1, 2, 3, 4);
        var path = Path.Combine(s.Dir, "journal.log");
        var original = File.ReadAllText(path);

        // a crash cut the last record in half: it is dropped without alarm
        File.WriteAllText(path, original[..(original.Length - 25)]);
        var torn = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        Assert.Equal(3, torn.LastDurableRevision);
        Assert.True(torn.Loss.TornTailDiscarded);
        Assert.Null(torn.Loss.CorruptionAfterRevision);

        // a damaged record in the middle stops recovery at the last good revision and says so
        var lines = original.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[1] = lines[1].Replace("\"gui\"", "\"mcp\"");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        var damaged = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        Assert.Equal(1, damaged.LastDurableRevision);
        Assert.Equal(1, damaged.Loss.CorruptionAfterRevision);
        Assert.Contains(damaged.Loss.Messages, m => m.Contains("damaged after revision 1"));
        Assert.Single(damaged.Events);
    }

    [Fact]
    public async Task ARecordThatDoesNotContinueTheCheckpointIsReportedNotApplied()
    {
        using var env = new Env();
        var s = await env.Begin();
        await AppendAll(s, 1, 2);
        await s.CheckpointAsync(DocJson(2), 2, [], default);
        // the journal is replaced by one that skips a revision
        var other = new RecoveryJournal(Path.Combine(s.Dir, "journal.log"), env.Blobs, 3, Fast);
        Assert.True((await other.AppendDurableAsync(Ev(4), default)).IsOk);
        var c = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        Assert.Empty(c.Events);
        Assert.Equal(2, c.LastDurableRevision);
        Assert.Contains(c.Loss.Messages, m => m.Contains("does not continue the checkpoint"));
    }

    [Fact]
    public async Task NewSessionStartsOwnCheckpoint()
    {
        using var env = new Env();
        var old = await env.Begin();
        await AppendAll(old, 1, 2, 3);
        var oldCandidate = (await env.Store.ReadRecoveryAsync(null, default)).Single();

        // after restoring, the app starts a new session at revision 3 with its own checkpoint and journal
        var newId = "00000000-0000-4000-8000-0000000000e2";
        var fresh = await env.Store.BeginSessionAsync(Doc, newId, oldCandidate.Document, 3, null, [], default, Fast);
        Assert.NotEqual(old.Dir, fresh.Dir);
        Assert.NotNull(fresh.Checkpoints.ReadLatest());
        Assert.Equal(3, fresh.Checkpoints.ReadLatest()!.Revision);
        // the old session's chain is not continued
        Assert.Equal("session_mismatch", (await fresh.AppendAsync(Ev(4, session: Sess), default)).Error!.Code);
        Assert.True((await fresh.AppendAsync(Ev(4, session: newId), default)).IsOk);
        Assert.Equal(3, (await env.Store.ReadRecoveryAsync(newId, default)).Single().LastDurableRevision); // excluding the live session shows only the old one
        await env.Store.CompleteAsync(Sess);
        var left = await env.Store.ReadRecoveryAsync(null, default);
        Assert.Equal(newId, left.Single().SessionId);
        Assert.Equal(4, left.Single().LastDurableRevision);
    }

    [Fact]
    public async Task UndoAndRecoveryPreventBlobGc()
    {
        using var env = new Env();
        var kept = await env.Blobs.PutDurableAsync("kept"u8.ToArray(), default);
        var inJournal = await env.Blobs.PutDurableAsync("journal"u8.ToArray(), default);
        var inCheckpoint = await env.Blobs.PutDurableAsync("checkpoint"u8.ToArray(), default);
        var inUndo = await env.Blobs.PutDurableAsync("undo"u8.ToArray(), default);
        var orphan = await env.Blobs.PutDurableAsync("orphan"u8.ToArray(), default);
        var s = await env.Begin();
        await s.CheckpointAsync(DocJson(1), 1, [inCheckpoint], default);
        // registered at revision 2, then removed again at revision 3: the journal still names it, and undo could need it back
        Assert.True((await s.AppendAsync(Ev(2, assets: [PictureAsset(inJournal)]), default)).IsOk);
        Assert.True((await s.AppendAsync(Ev(3, deletedAssets: ["asset:logo"]), default)).IsOk);

        // documents, the library, undo and recovery each name what they need; GC keeps the union
        var live = new[] { kept }.Concat(env.Store.ReferencedBlobs()).Concat([inUndo]);
        var removed = BlobGc.Collect(env.Blobs, live, TimeSpan.Zero);
        Assert.Equal(1, removed);
        Assert.False(env.Blobs.Contains(orphan));
        foreach (var sha in new[] { kept, inJournal, inCheckpoint, inUndo }) Assert.True(env.Blobs.Contains(sha));

        // once the session is finished, its pictures can go
        await env.Store.CompleteAsync(Sess);
        Assert.Equal(2, BlobGc.Collect(env.Blobs, new[] { kept, inUndo }, TimeSpan.Zero));
        Assert.False(env.Blobs.Contains(inJournal));
        Assert.False(env.Blobs.Contains(inCheckpoint));
    }

    [Fact]
    public async Task GcLeavesRecentBlobsAloneWithinTheGracePeriod()
    {
        using var env = new Env();
        var fresh = await env.Blobs.PutDurableAsync("fresh"u8.ToArray(), default); // an upload that is prepared but not yet registered
        Assert.Equal(0, BlobGc.Collect(env.Blobs, [], TimeSpan.FromHours(1)));
        Assert.True(env.Blobs.Contains(fresh));
    }

    [Fact]
    public async Task AWriteFailureFaultsTheJournalUntilACheckpointRebasesIt()
    {
        using var env = new Env();
        var s = await env.Begin();
        await AppendAll(s, 1);
        s.Journal.BeforeFlush = () => throw new IOException("disk full");
        var failed = await s.AppendAsync(Ev(2), default);
        Assert.Equal("io_error", failed.Error!.Code);
        Assert.True(s.Journal.Faulted);
        Assert.Equal("journal_faulted", (await s.AppendAsync(Ev(3), default)).Error!.Code);
        s.Journal.BeforeFlush = null;
        await s.CheckpointAsync(DocJson(3), 3, [], default);
        Assert.False(s.Journal.Faulted);
        Assert.True((await s.AppendAsync(Ev(4), default)).IsOk);
        var c = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        Assert.Equal(3, c.CheckpointRevision);
        Assert.Equal(4, c.LastDurableRevision);
    }

    [Fact]
    public async Task ACorruptNewestGenerationFallsBackToThePreviousOne()
    {
        using var env = new Env();
        var s = await env.Begin();
        await s.CheckpointAsync(DocJson(5), 5, [], default);
        var newest = Directory.GetFiles(s.Dir, "gen-*.json").OrderBy(x => x).Last();
        File.WriteAllText(newest, "{ not json");
        var c = (await env.Store.ReadRecoveryAsync(null, default)).Single();
        Assert.Equal(0, c.CheckpointRevision); // the first checkpoint, still present because the previous generation is kept
    }
}


