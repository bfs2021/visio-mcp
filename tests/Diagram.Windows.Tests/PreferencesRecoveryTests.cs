using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Xunit;

namespace Diagram.Windows.Tests;

public class PreferencesRecoveryTests
{
    sealed class ManualTimeProvider : TimeProvider
    {
        TimeSpan now;
        readonly List<ManualTimer> timers = [];
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
            TimeSpan period;
            bool disposed;
            public TimeSpan? Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                this.period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                return true;
            }
            public void Fire() { Due = period == Timeout.InfiniteTimeSpan ? null : owner.now + period; callback(state); }
            public void Dispose() { disposed = true; Due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task SavedIntervalCheckpointsNativeDrawingAndSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-pref-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new ManualTimeProvider();
            await using (var h = await Harness.StartAsync(existingRoot: root, recoveryTimeProvider: clock))
            {
                await h.Window.RecoveryMgr.WhenSessionReadyAsync();
                var get = await h.Window.Services.HandleAsync(new() { RequestId = "get", Method = "host.preferences.get", Params = new() }, default);
                var prefs = JsonNode.Parse(get.Result!.Value.GetProperty("preferences").GetRawText())!;
                prefs["recovery"]!["checkpointSeconds"] = 10;
                var saved = await h.Window.Services.HandleAsync(new()
                {
                    RequestId = "save", Method = "host.preferences.save",
                    Params = new() { ["preferences"] = prefs, ["expectedVersionTag"] = get.Result.Value.GetProperty("versionTag").GetString() },
                }, default);
                Assert.Null(saved.Error);
                var conflictDraft = prefs.DeepClone();
                conflictDraft["recovery"]!["checkpointSeconds"] = 20;
                var conflict = await h.Window.Services.HandleAsync(new()
                {
                    RequestId = "conflict", Method = "host.preferences.save",
                    Params = new() { ["preferences"] = conflictDraft, ["expectedVersionTag"] = get.Result.Value.GetProperty("versionTag").GetString() },
                }, default);
                Assert.Equal("revision_conflict", conflict.Error?.Code);
                var session = h.Router.CurrentScope!.SessionId;
                Assert.Null((await h.Apply(0, null, Harness.Shape("after-settings"))).Error);
                await h.Window.RecoveryMgr.DrainAsync();
                clock.Advance(TimeSpan.FromSeconds(9)); await h.Window.RecoveryMgr.DrainAsync();
                var before = Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == session);
                Assert.Equal(0, before.CheckpointRevision);
                clock.Advance(TimeSpan.FromSeconds(1)); await h.Window.RecoveryMgr.DrainAsync();
                var after = Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == session);
                Assert.Equal(1, after.CheckpointRevision);
                Assert.Equal(1, after.LastDurableRevision);
                await h.Window.RecoveryMgr.DisposeAsync(); // simulate a crash before clean close
            }
            var restartedClock = new ManualTimeProvider();
            await using var restarted = await Harness.StartAsync(existingRoot: root, navigate: false, recoveryTimeProvider: restartedClock);
            var stored = await restarted.Window.Preferences.ReadAsync();
            Assert.Equal(10, stored.Preferences.Recovery.CheckpointSeconds);
            var candidate = Assert.Single(await restarted.Window.Recovery.ReadRecoveryAsync(null, default), c => c.CheckpointRevision == 1);
            Assert.Equal(1, candidate.LastDurableRevision);
            Assert.Single(candidate.Document["pages"]![0]!["elements"]!.AsArray());
        }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task PersistedIntervalControlsFirstRecoveryTimerOnStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-pref-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var prefs = JsonSerializer.SerializeToNode(PreferencesValidation.DefaultPreferences(), ContractJson.Options)!;
            prefs["recovery"]!["checkpointSeconds"] = 10;
            await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), prefs.ToJsonString());
            var clock = new ManualTimeProvider();
            await using var h = await Harness.StartAsync(existingRoot: root, recoveryTimeProvider: clock);
            await h.Window.RecoveryMgr.WhenSessionReadyAsync();
            var session = h.Router.CurrentScope!.SessionId;
            Assert.Null((await h.Apply(0, null, Harness.Shape("startup-timer"))).Error);
            await h.Window.RecoveryMgr.DrainAsync();
            clock.Advance(TimeSpan.FromSeconds(10)); await h.Window.RecoveryMgr.DrainAsync();
            Assert.Equal(1, Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == session).CheckpointRevision);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task FailedPreferencesWriteLeavesExistingRecoveryCadence()
    {
        var clock = new ManualTimeProvider();
        await using var h = await Harness.StartAsync(recoveryTimeProvider: clock);
        await h.Window.RecoveryMgr.WhenSessionReadyAsync();
        var session = h.Router.CurrentScope!.SessionId;
        var get = await h.Window.Services.HandleAsync(new() { RequestId = "get", Method = "host.preferences.get", Params = new() }, default);
        var prefs = JsonNode.Parse(get.Result!.Value.GetProperty("preferences").GetRawText())!;
        prefs["recovery"]!["checkpointSeconds"] = 10;
        Directory.CreateDirectory(Path.Combine(h.Root, "preferences.json")); // atomic replacement cannot place a file here
        var saved = await h.Window.Services.HandleAsync(new()
        {
            RequestId = "save", Method = "host.preferences.save",
            Params = new() { ["preferences"] = prefs, ["expectedVersionTag"] = get.Result.Value.GetProperty("versionTag").GetString() },
        }, default);
        Assert.Equal("io_error", saved.Error?.Code);
        Assert.Null((await h.Apply(0, null, Harness.Shape("still-sixty"))).Error);
        await h.Window.RecoveryMgr.DrainAsync();
        clock.Advance(TimeSpan.FromSeconds(10)); await h.Window.RecoveryMgr.DrainAsync();
        Assert.Equal(0, Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == session).CheckpointRevision);
        clock.Advance(TimeSpan.FromSeconds(50)); await h.Window.RecoveryMgr.DrainAsync();
        Assert.Equal(1, Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == session).CheckpointRevision);
    }
}
