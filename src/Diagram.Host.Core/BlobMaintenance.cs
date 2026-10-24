using System.Text.Json;

namespace Diagram.Host.Core;

/// <param name="FirstRunAfter">How long after start the first pass waits, so it never competes with opening documents and recovery.</param>
/// <param name="Every">The least time between two passes, kept across restarts.</param>
/// <param name="Grace">A picture stored or touched more recently than this is never removed, whatever references it.</param>
public sealed record BlobMaintenanceOptions(TimeSpan FirstRunAfter, TimeSpan Every, TimeSpan Grace)
{
    public static readonly BlobMaintenanceOptions Default = new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(24), TimeSpan.FromDays(7));
}

/// <summary>What the last pass did. Written next to the picture folder so a restart does not repeat a pass that has just run.</summary>
/// <param name="LastCollectedUtc">When a pass last completed a collection; a skipped or failed pass carries the previous one forward.</param>
public sealed record BlobMaintenanceRun(DateTimeOffset AtUtc, string Outcome, int Removed, long BytesFreed, string? Detail = null, DateTimeOffset? LastCollectedUtc = null)
{
    public const string Collected = "collected", AnotherProcess = "skipped_another_process", Failed = "failed";
}

/// <summary>
/// Reclaims picture bytes nothing needs any more (H02). A pass runs a while after the window starts and then at most once a day; it keeps
/// whatever an open document (with its undo history), a recovery session, the library (every version) or an unexpired upload names, and any
/// picture touched within the grace period. It does nothing while another process uses the same folder, and a failed pass changes nothing
/// and is tried again at the next interval.
/// </summary>
public sealed class BlobMaintenance : IAsyncDisposable
{
    readonly BlobStoreLease lease;
    readonly string marker;
    readonly Func<CancellationToken, Task<IEnumerable<string>>> otherLive;
    readonly Func<IEnumerable<string>, TimeSpan, CancellationToken, Task<BlobGcResult>> collect;
    readonly BlobMaintenanceOptions options;
    readonly Func<DateTimeOffset> now;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim running = new(1, 1);
    Task? loop;

    /// <param name="otherLive">Pictures held outside open documents and recovery: the library and unexpired uploads.</param>
    /// <param name="collect">Asks every open document what it holds and removes the rest (<see cref="RecoveryManager.CollectGarbageAsync"/>).</param>
    public BlobMaintenance(BlobStoreLease lease, string markerFile, Func<CancellationToken, Task<IEnumerable<string>>> otherLive,
        Func<IEnumerable<string>, TimeSpan, CancellationToken, Task<BlobGcResult>> collect, BlobMaintenanceOptions? options = null, Func<DateTimeOffset>? now = null)
    {
        this.lease = lease; marker = markerFile; this.otherLive = otherLive; this.collect = collect;
        this.options = options ?? BlobMaintenanceOptions.Default; this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Start() => loop ??= Task.Run(LoopAsync);

    /// <summary>How long to wait before the next pass: never sooner than <c>FirstRunAfter</c>, and not before a day has passed since the last collection.</summary>
    public static TimeSpan NextDelay(BlobMaintenanceRun? last, DateTimeOffset at, TimeSpan minimum, TimeSpan every)
    {
        var due = last?.LastCollectedUtc is { } collected ? collected + every - at : TimeSpan.Zero;
        return due > minimum ? due : minimum;
    }

    async Task LoopAsync()
    {
        try
        {
            var wait = NextDelay(ReadLast(), now(), options.FirstRunAfter, options.Every);
            while (true)
            {
                await Task.Delay(wait, stop.Token).ConfigureAwait(false);
                await RunOnceAsync(stop.Token).ConfigureAwait(false);
                wait = NextDelay(ReadLast(), now(), options.Every, options.Every);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    /// <summary>One pass. Safe to call at any time; a pass already running is waited for, not overlapped.</summary>
    public async Task<BlobMaintenanceRun> RunOnceAsync(CancellationToken ct)
    {
        await running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BlobMaintenanceRun run;
            try
            {
                if (!lease.IsSoleHolder())
                    run = new(now(), BlobMaintenanceRun.AnotherProcess, 0, 0, "another window of this user uses the same picture folder");
                else
                {
                    var held = await otherLive(ct).ConfigureAwait(false);
                    var freed = await collect(held, options.Grace, ct).ConfigureAwait(false);
                    var at = now();
                    run = new(at, BlobMaintenanceRun.Collected, freed.Removed, freed.Bytes, LastCollectedUtc: at);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                run = new(now(), BlobMaintenanceRun.Failed, 0, 0, e.Message);
            }
            Write(run);
            return run;
        }
        finally { running.Release(); }
    }

    public BlobMaintenanceRun? ReadLast()
    {
        try { return File.Exists(marker) ? JsonSerializer.Deserialize<BlobMaintenanceRun>(File.ReadAllBytes(marker)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; } // unreadable: as if never run
    }

    void Write(BlobMaintenanceRun run)
    {
        // A pass that did not collect keeps the last collection's time, so the next start tries again instead of waiting a day.
        if (run.LastCollectedUtc is null) run = run with { LastCollectedUtc = ReadLast()?.LastCollectedUtc };
        try { new AtomicFileWriter().WriteAsync(marker, JsonSerializer.SerializeToUtf8Bytes(run), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the next start runs a pass again; nothing is lost
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null) try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await running.WaitAsync().ConfigureAwait(false); // a pass in progress finishes (it is cancelled at its next step) before the lease goes
        running.Release();
    }
}
