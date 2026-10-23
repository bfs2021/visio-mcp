using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Diagram.Windows.Tests;

/// <summary>
/// Watches the process it runs in from a thread of its own (not a pool thread): garbage-collection pause time, thread-pool queue and size, process
/// CPU time and machine-wide CPU use, every 20 ms. A gap in its own samples means the whole process was suspended; work waiting in the pool for a
/// stretch means the pool was starved. Used by the A05 probes and by the performance benchmark, so a timing taken during a starved stretch can be seen.
/// </summary>
public sealed class PoolSampler : IDisposable
{
    public const double GapMs = 300;

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public sealed record Sample(double T, double GcPauseMs, int Gc0, int Gc1, int Gc2, int Pool, long Pending, double CpuMs, long Idle, long Busy);
    public sealed record Window(double StartMs, double EndMs, int PoolAtStart, int PoolAtEnd, long MaxQueued, double MachineBusyPercent)
    {
        public double LengthMs => EndMs - StartMs;
    }

    public readonly Stopwatch Clock = Stopwatch.StartNew();
    readonly List<Sample> samples = [];
    readonly Thread thread;
    volatile bool stop;

    public PoolSampler() { thread = new Thread(Run) { IsBackground = true, Name = "pool-sampler" }; thread.Start(); }

    void Run()
    {
        while (!stop)
        {
            GetSystemTimes(out var idle, out var kernel, out var user); // kernel time includes idle time
            var s = new Sample(Clock.Elapsed.TotalMilliseconds, GC.GetTotalPauseDuration().TotalMilliseconds, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, Environment.CpuUsage.TotalTime.TotalMilliseconds, idle, kernel + user - idle);
            lock (samples) samples.Add(s);
            Thread.Sleep(20);
        }
    }

    public void Dispose() { stop = true; thread.Join(); }

    Sample[] Snapshot() { lock (samples) return [.. samples]; }

    static double Busy(Sample a, Sample b) { var busy = b.Busy - a.Busy; var idle = b.Idle - a.Idle; return busy + idle <= 0 ? 0 : Math.Round(100.0 * busy / (busy + idle)); }

    /// <summary>Machine-wide CPU use over the whole run.</summary>
    public double OverallBusyPercent() { var all = Snapshot(); return all.Length < 2 ? 0 : Busy(all[0], all[^1]); }

    /// <summary>Stretches of at least <paramref name="minMs"/> in which work was queued for the pool the whole time: the pool was starved, whether or not anything else stopped.</summary>
    public IReadOnlyList<Window> StarvedWindows(double minMs = GapMs)
    {
        var all = Snapshot();
        var windows = new List<Window>();
        int? from = null;
        for (var i = 0; i <= all.Length; i++)
        {
            var queued = i < all.Length && all[i].Pending > 0;
            if (queued && from is null) from = i;
            if (!queued && from is not null)
            {
                var a = all[from.Value]; var b = all[i - 1];
                if (b.T - a.T >= minMs) windows.Add(new Window(a.T, b.T, a.Pool, b.Pool, all[from.Value..i].Max(x => x.Pending), Busy(a, b)));
                from = null;
            }
        }
        return windows;
    }

    /// <summary>True when a starved stretch overlaps the given interval of this sampler's clock.</summary>
    public bool StarvedBetween(double fromMs, double toMs, double minMs = 100) => StarvedWindows(minMs).Any(w => w.StartMs <= toMs && w.EndMs >= fromMs);

    /// <summary>Waits until the pool has had nothing queued for <paramref name="quiet"/>. Returns how long it waited, or null if it never became quiet within <paramref name="max"/>.</summary>
    public async Task<TimeSpan?> WaitQuietAsync(TimeSpan quiet, TimeSpan max)
    {
        var started = Clock.Elapsed;
        while (Clock.Elapsed - started < max)
        {
            await Task.Delay(100);
            var now = Clock.Elapsed.TotalMilliseconds;
            var recent = Snapshot().Where(s => s.T >= now - quiet.TotalMilliseconds).ToArray();
            if (recent.Length > 0 && recent[0].T <= now - quiet.TotalMilliseconds + 60 && recent.All(s => s.Pending == 0)) return Clock.Elapsed - started;
        }
        return null;
    }

    public JsonArray StarvationJson()
    {
        var all = Snapshot();
        var json = new JsonArray();
        foreach (var w in StarvedWindows())
        {
            var before = all.LastOrDefault(x => x.T <= w.StartMs - 2000) ?? all[0];
            var at = all.First(x => x.T >= w.StartMs);
            json.Add(new JsonObject
            {
                ["startMs"] = Math.Round(w.StartMs), ["lengthMs"] = Math.Round(w.LengthMs), ["poolThreadsAtStart"] = w.PoolAtStart, ["poolThreadsAtEnd"] = w.PoolAtEnd,
                ["maxQueued"] = w.MaxQueued, ["machineCpuBusyPercent"] = w.MachineBusyPercent, ["machineCpuBusyPercentFor2sBefore"] = Busy(before, at),
            });
        }
        return json;
    }

    /// <summary>Gaps in the sampler's own samples: the whole process was suspended.</summary>
    public JsonArray StopsJson()
    {
        var all = Snapshot();
        var stops = new JsonArray();
        for (var i = 1; i < all.Length; i++)
        {
            var a = all[i - 1]; var b = all[i];
            if (b.T - a.T < GapMs) continue;
            stops.Add(new JsonObject
            {
                ["startMs"] = Math.Round(a.T), ["lengthMs"] = Math.Round(b.T - a.T),
                ["gcPauseDeltaMs"] = Math.Round(b.GcPauseMs - a.GcPauseMs, 1), ["gen0"] = b.Gc0 - a.Gc0, ["gen1"] = b.Gc1 - a.Gc1, ["gen2"] = b.Gc2 - a.Gc2,
                ["poolThreadsBefore"] = a.Pool, ["poolThreadsAfter"] = b.Pool, ["pendingAfter"] = b.Pending, ["cpuDeltaMs"] = Math.Round(b.CpuMs - a.CpuMs),
            });
        }
        return stops;
    }
}
