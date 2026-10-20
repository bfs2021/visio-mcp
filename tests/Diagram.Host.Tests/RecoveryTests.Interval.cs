using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public partial class RecoveryTests
{
    const string TimerOrigin = "https://app.agentic-diagram.invalid/index.html";

    sealed class ManualTimeProvider : TimeProvider
    {
        readonly List<ManualTimer> timers = [];
        TimeSpan now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            var end = now + by;
            while (true)
            {
                var next = timers.Where(t => t.Due is not null && t.Due <= end).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null) break;
                now = next.Due!.Value;
                next.Fire();
            }
            now = end;
        }
        sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? Due { get; private set; }
            TimeSpan period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                this.period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                return true;
            }
            bool disposed;
            public void Fire()
            {
                Due = period == Timeout.InfiniteTimeSpan ? null : owner.now + period;
                callback(state);
            }
            public void Dispose() { disposed = true; Due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    sealed class TimerPage : IFrontendTransport
    {
        public BridgeRouter Router = null!;
        public int Revision;
        public void Post(string json)
        {
            var req = JsonNode.Parse(json)!.AsObject();
            var doc = DocJson(Revision).Deserialize<DiagramDocument>(ContractJson.Options)!;
            var snapshot = new Snapshot { DocumentId = Doc, SessionId = Sess, Revision = Revision, Document = doc };
            var response = new JsonObject
            {
                ["protocolVersion"] = 1, ["kind"] = "response", ["requestId"] = req["requestId"]!.GetValue<string>(),
                ["result"] = JsonSerializer.SerializeToNode(snapshot, ContractJson.Options),
            };
            Task.Run(() => Router.OnMessage(TimerOrigin, response.ToJsonString(), true));
        }
    }

    sealed class TimerEnv : IAsyncDisposable
    {
        public readonly TempDir Dir = new();
        public readonly ManualTimeProvider Clock = new();
        public readonly TimerPage Page = new();
        public readonly BridgeRouter Router;
        public readonly RecoveryStore Store;
        public readonly RecoveryManager Manager;
        public TimerEnv()
        {
            Router = new BridgeRouter(Page); Page.Router = Router;
            var blobs = new BlobStore(Dir.File("blobs"));
            Store = new RecoveryStore(Dir.File("recovery"), blobs);
            Manager = new RecoveryManager(Router, Store, blobs, () => null,
                new RecoveryOptions(FlushDelay: TimeSpan.Zero), timeProvider: Clock);
            Manager.Start();
        }
        public async Task OpenAsync()
        {
            Router.OnMessage(TimerOrigin, JsonSerializer.Serialize(new
            {
                protocolVersion = 1, kind = "event", method = "editor.ready",
                @params = new { documentId = Doc, sessionId = Sess, revision = 0 },
            }), true);
            await Manager.DrainAsync();
            Assert.Equal(Sess, Manager.SessionId);
        }
        public async Task CommitAsync(int revision)
        {
            Page.Revision = revision;
            Router.OnMessage(TimerOrigin, JsonSerializer.Serialize(new
            {
                protocolVersion = 1, kind = "event", method = "editor.committed",
                @params = Ev(revision),
            }, ContractJson.Options), true);
            await Manager.DrainAsync();
        }
        public async Task<int> CheckpointRevisionAsync() => Assert.Single(await Store.ReadRecoveryAsync(null, default)).CheckpointRevision;
        public async ValueTask DisposeAsync() { await Manager.DisposeAsync(); Dir.Dispose(); }
    }

    [Fact]
    public async Task PendingCommitCheckpointsAtUpdatedIntervalWithoutLosingSession()
    {
        await using var e = new TimerEnv(); await e.OpenAsync(); await e.CommitAsync(1);
        var session = e.Manager.SessionId;
        e.Clock.Advance(TimeSpan.FromSeconds(5));
        await e.Manager.UpdateCheckpointIntervalAsync(TimeSpan.FromSeconds(10));
        e.Clock.Advance(TimeSpan.FromSeconds(9)); await e.Manager.DrainAsync();
        Assert.Equal(0, await e.CheckpointRevisionAsync());
        e.Clock.Advance(TimeSpan.FromSeconds(1)); await e.Manager.DrainAsync();
        Assert.Equal(1, await e.CheckpointRevisionAsync());
        Assert.Equal(session, e.Manager.SessionId);
    }

    [Fact]
    public async Task TickWithoutNewCommitLeavesCheckpointUntouched()
    {
        await using var e = new TimerEnv(); await e.OpenAsync(); await e.CommitAsync(1);
        await e.Manager.UpdateCheckpointIntervalAsync(TimeSpan.FromSeconds(10));
        e.Clock.Advance(TimeSpan.FromSeconds(10)); await e.Manager.DrainAsync();
        var generationCount = Directory.GetFiles(Path.Combine(e.Dir.File("recovery"), Sess), "gen-*.json").Length;
        e.Clock.Advance(TimeSpan.FromSeconds(30)); await e.Manager.DrainAsync();
        Assert.Equal(1, await e.CheckpointRevisionAsync());
        Assert.Equal(generationCount, Directory.GetFiles(Path.Combine(e.Dir.File("recovery"), Sess), "gen-*.json").Length);
    }

    [Fact]
    public async Task ChangedIntervalKeepsCommitThresholdAndSaveCheckpoint()
    {
        await using var e = new TimerEnv(); await e.OpenAsync();
        for (var revision = 1; revision <= 49; revision++) await e.CommitAsync(revision);
        await e.Manager.UpdateCheckpointIntervalAsync(TimeSpan.FromSeconds(10));
        await e.CommitAsync(50);
        Assert.Equal(50, await e.CheckpointRevisionAsync());
        await e.CommitAsync(51);
        await e.Manager.OnSavedAsync(Sess, 51, "C:/saved.diagram.json");
        Assert.Equal(51, await e.CheckpointRevisionAsync());
    }

    [Theory]
    [InlineData(0)] [InlineData(9)] [InlineData(3601)] [InlineData(10.5)]
    public async Task InvalidUpdateCannotChangeTimer(double seconds)
    {
        await using var e = new TimerEnv(); await e.OpenAsync(); await e.CommitAsync(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => e.Manager.UpdateCheckpointIntervalAsync(TimeSpan.FromSeconds(seconds)));
        e.Clock.Advance(TimeSpan.FromSeconds(59)); await e.Manager.DrainAsync();
        Assert.Equal(0, await e.CheckpointRevisionAsync());
        e.Clock.Advance(TimeSpan.FromSeconds(1)); await e.Manager.DrainAsync();
        Assert.Equal(1, await e.CheckpointRevisionAsync());
    }

    [Fact]
    public async Task DisposedManagerRejectsUpdateAndTimerCannotScheduleWork()
    {
        await using var e = new TimerEnv(); await e.OpenAsync(); await e.CommitAsync(1);
        await e.Manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => e.Manager.UpdateCheckpointIntervalAsync(TimeSpan.FromSeconds(10)));
        e.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await e.CheckpointRevisionAsync());
    }

    [Fact]
    public async Task QueuedCommitFinishesBeforeIntervalUpdateAndStillCheckpoints()
    {
        await using var e = new TimerEnv(); await e.OpenAsync();
        e.Page.Revision = 1;
        e.Router.OnMessage(TimerOrigin, JsonSerializer.Serialize(new
        {
            protocolVersion = 1, kind = "event", method = "editor.committed", @params = Ev(1),
        }, ContractJson.Options), true);
        await e.Manager.UpdateCheckpointIntervalAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, e.Manager.Status.LastDurableRevision);
        e.Clock.Advance(TimeSpan.FromSeconds(10)); await e.Manager.DrainAsync();
        Assert.Equal(1, await e.CheckpointRevisionAsync());
    }
}
