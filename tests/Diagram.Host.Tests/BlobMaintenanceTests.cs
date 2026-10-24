using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

/// <summary>H02: reclaiming picture bytes nothing needs, on real files in a temporary folder.</summary>
public class BlobMaintenanceTests
{
    static readonly DateTime LongAgo = DateTime.UtcNow - TimeSpan.FromDays(30);
    static readonly TimeSpan Week = TimeSpan.FromDays(7);

    static async Task<string> Old(BlobStore blobs, string dir, string content)
    {
        var sha = await blobs.PutDurableAsync(System.Text.Encoding.UTF8.GetBytes(content), default);
        File.SetLastWriteTimeUtc(Path.Combine(dir, sha), LongAgo);
        return sha;
    }

    // ---- the store --------------------------------------------------------------------------------

    [Fact]
    public async Task StoringAPictureThatIsAlreadyThereMakesItRecentAgain()
    {
        using var dir = new TempDir();
        var blobs = new BlobStore(dir.File("blobs"));
        var sha = await Old(blobs, dir.File("blobs"), "an orphan uploaded again");
        await blobs.PutDurableAsync("an orphan uploaded again"u8.ToArray(), default);
        Assert.True(File.GetLastWriteTimeUtc(dir.File(Path.Combine("blobs", sha))) > DateTime.UtcNow - TimeSpan.FromMinutes(1));
        Assert.Equal(BlobGcResult.None, await BlobGc.CollectAsync(blobs, [], Week)); // so a collection now leaves it for its new user
        Assert.True(blobs.Contains(sha));
    }

    [Fact]
    public async Task APictureTouchedAfterItWasListedIsNotDeleted()
    {
        using var dir = new TempDir();
        var blobs = new BlobStore(dir.File("blobs"));
        var sha = await Old(blobs, dir.File("blobs"), "listed, then handed out again");
        var cutoff = DateTime.UtcNow - Week;
        await blobs.PutDurableAsync("listed, then handed out again"u8.ToArray(), default); // between the listing and the delete
        Assert.Null(await blobs.DeleteIfIdleAsync(sha, cutoff, default));
        Assert.True(blobs.Contains(sha));
    }

    [Fact]
    public async Task CollectionReportsWhatItFreedAndKeepsWhatIsLiveOrRecent()
    {
        using var dir = new TempDir();
        var root = dir.File("blobs");
        var blobs = new BlobStore(root);
        var live = await Old(blobs, root, "named by a document");
        var orphanA = await Old(blobs, root, "nobody, 18 bytes!");
        var orphanB = await Old(blobs, root, "nobody either");
        var recent = await blobs.PutDurableAsync("nobody, but stored a moment ago"u8.ToArray(), default);

        var freed = await BlobGc.CollectAsync(blobs, [live], Week);

        Assert.Equal(2, freed.Removed);
        Assert.Equal("nobody, 18 bytes!".Length + "nobody either".Length, freed.Bytes);
        Assert.True(blobs.Contains(live));
        Assert.True(blobs.Contains(recent));
        Assert.False(blobs.Contains(orphanA));
        Assert.False(blobs.Contains(orphanB));
    }

    [Fact]
    public async Task AnInterruptedCollectionDeletesNothingLiveAndARetryFinishesTheRest()
    {
        using var dir = new TempDir();
        var root = dir.File("blobs");
        var blobs = new BlobStore(root);
        var live = await Old(blobs, root, "kept");
        var orphans = new List<string>();
        for (var i = 0; i < 20; i++) orphans.Add(await Old(blobs, root, $"orphan {i}"));

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // stopped before (or while) it deletes: whatever it managed is a subset of the orphans
        try { await BlobGc.CollectAsync(blobs, [live], Week, cts.Token); } catch (OperationCanceledException) { }
        Assert.True(blobs.Contains(live));

        var retry = await BlobGc.CollectAsync(blobs, [live], Week);
        Assert.True(blobs.Contains(live));
        Assert.All(orphans, o => Assert.False(blobs.Contains(o)));
        Assert.Equal(BlobGcResult.None, await BlobGc.CollectAsync(blobs, [live], Week)); // and a third run finds nothing
        Assert.True(retry.Removed is > 0 and <= 20);
    }

    // ---- other processes ----------------------------------------------------------------------------

    [Fact]
    public void ALeaseHeldByAnotherProcessStopsCollectionAndAnAbandonedOneDoesNot()
    {
        using var dir = new TempDir();
        using var mine = BlobStoreLease.Take(dir.File("blobs"));
        Assert.True(mine.IsSoleHolder());

        var other = BlobStoreLease.Take(dir.File("blobs")); // a second window of the same user, in another logon session
        Assert.False(mine.IsSoleHolder());
        Assert.False(other.IsSoleHolder());
        other.Dispose();
        Assert.True(mine.IsSoleHolder());

        // a process that crashed leaves its lease file behind, unheld: it is cleared and does not block
        var stale = Path.Combine(dir.File("blobs"), ".holders", "4242-crashed.lease");
        File.WriteAllText(stale, "");
        Assert.True(mine.IsSoleHolder());
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void LeaseFilesAreNotMistakenForPictures()
    {
        using var dir = new TempDir();
        var blobs = new BlobStore(dir.File("blobs"));
        using var lease = BlobStoreLease.Take(dir.File("blobs"));
        Assert.Empty(blobs.Enumerate());
    }

    // ---- the schedule ----------------------------------------------------------------------------

    [Fact]
    public void ThePassWaitsAfterStartAndRunsAtMostOnceADayAcrossRestarts()
    {
        var at = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        TimeSpan min = TimeSpan.FromMinutes(10), day = TimeSpan.FromDays(1);
        BlobMaintenanceRun Collected(TimeSpan ago) => new(at - ago, BlobMaintenanceRun.Collected, 0, 0, LastCollectedUtc: at - ago);

        Assert.Equal(min, BlobMaintenance.NextDelay(null, at, min, day));                                  // never run: soon after start
        Assert.Equal(TimeSpan.FromHours(23), BlobMaintenance.NextDelay(Collected(TimeSpan.FromHours(1)), at, min, day)); // ran an hour ago
        Assert.Equal(min, BlobMaintenance.NextDelay(Collected(TimeSpan.FromDays(3)), at, min, day));       // overdue: still not at once
        var failedAfterOld = new BlobMaintenanceRun(at, BlobMaintenanceRun.Failed, 0, 0, "disk", LastCollectedUtc: at - TimeSpan.FromDays(2));
        Assert.Equal(min, BlobMaintenance.NextDelay(failedAfterOld, at, min, day));                        // a failure is retried
    }

    [Fact]
    public async Task APassRecordsWhatItFreedAndASkippedOrFailedPassKeepsTheLastCollection()
    {
        using var dir = new TempDir();
        using var lease = BlobStoreLease.Take(dir.File("blobs"));
        var marker = dir.File("blob-maintenance.json");
        IEnumerable<string>? passed = null;
        var result = new BlobGcResult(3, 1234);
        Exception? fail = null;
        await using var m = new BlobMaintenance(lease, marker, _ => Task.FromResult<IEnumerable<string>>(["library-v1", "upload"]),
            (held, grace, _) => { passed = held; Assert.Equal(Week, grace); return fail is null ? Task.FromResult(result) : Task.FromException<BlobGcResult>(fail); },
            new BlobMaintenanceOptions(TimeSpan.FromHours(1), TimeSpan.FromDays(1), Week));

        var first = await m.RunOnceAsync(default);
        Assert.Equal(BlobMaintenanceRun.Collected, first.Outcome);
        Assert.Equal((3, 1234L), (first.Removed, first.BytesFreed));
        Assert.Equal(["library-v1", "upload"], passed!);
        Assert.Equal(first.LastCollectedUtc, m.ReadLast()!.LastCollectedUtc);

        fail = new IOException("the disk went away");
        var failed = await m.RunOnceAsync(default);
        Assert.Equal(BlobMaintenanceRun.Failed, failed.Outcome);
        Assert.Equal(first.LastCollectedUtc, m.ReadLast()!.LastCollectedUtc);

        fail = null;
        using var other = BlobStoreLease.Take(dir.File("blobs"));
        passed = null;
        var skipped = await m.RunOnceAsync(default);
        Assert.Equal(BlobMaintenanceRun.AnotherProcess, skipped.Outcome);
        Assert.Null(passed); // nobody was even asked
        Assert.Equal(first.LastCollectedUtc, m.ReadLast()!.LastCollectedUtc);
    }

    [Fact]
    public async Task ThePassRunsByItselfAfterTheFirstDelay()
    {
        using var dir = new TempDir();
        using var lease = BlobStoreLease.Take(dir.File("blobs"));
        var ran = new TaskCompletionSource();
        await using var m = new BlobMaintenance(lease, dir.File("blob-maintenance.json"), _ => Task.FromResult(Enumerable.Empty<string>()),
            (_, _, _) => { ran.TrySetResult(); return Task.FromResult(BlobGcResult.None); },
            new BlobMaintenanceOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromDays(1), Week));
        m.Start();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---- uploads not yet placed ----------------------------------------------------------------------

    [Fact]
    public async Task AnUnexpiredUploadIsLiveAndAnExpiredOneIsNot()
    {
        using var dir = new TempDir();
        var clock = DateTimeOffset.UtcNow;
        var preparer = new AssetPreparer(new BlobStore(dir.File("blobs")), () => clock);
        var prepared = await preparer.PrepareAsync(new MemoryStream(Bytes.PngHeader(4, 4)), "image/png", "p.png", default);
        Assert.True(prepared.IsOk, prepared.Error?.Message);
        Assert.Contains(prepared.Value.Ref.Asset.Sha256, preparer.LiveHashes());
        clock += TimeSpan.FromMinutes(11);
        Assert.Empty(preparer.LiveHashes());
    }
}
