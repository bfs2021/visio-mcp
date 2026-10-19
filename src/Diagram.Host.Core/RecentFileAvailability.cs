using System.Collections.Concurrent;
namespace Diagram.Host.Core;

/// <summary>A slow network drive cannot hold up the Recent list. Late probes are shared and concurrent workers bounded.</summary>
public sealed class RecentFileAvailability(Func<string, string>? probe = null, TimeSpan? timeout = null)
{
    readonly ConcurrentDictionary<string, Lazy<Task<string>>> probes = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim workers = new(16, 16);
    public async Task<string> CheckAsync(string path, CancellationToken ct = default)
    { var result = await CheckCoreAsync(path, ct).ConfigureAwait(false); return result == "directory" ? "unavailable" : result; }
    public async Task<bool> IsDirectoryAsync(string path, CancellationToken ct = default) => await CheckCoreAsync(path, ct).ConfigureAwait(false) == "directory";
    async Task<string> CheckCoreAsync(string path, CancellationToken ct)
    {
        var pending = probes.GetOrAdd(path, p => new Lazy<Task<string>>(() =>
        {
            if (!workers.Wait(0)) return Task.FromResult("unavailable");
            // Synchronous network filesystem calls can outlive cancellation; keep them off the shared thread pool.
            return Task.Factory.StartNew(() => { try { return (probe ?? Probe)(p); } finally { workers.Release(); } }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }));
        var task = pending.Value;
        _ = task.ContinueWith(_ => probes.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(path, pending)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await task.WaitAsync(timeout ?? TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false); }
        catch (TimeoutException) { return "unavailable"; }
    }
    static string Probe(string path)
    {
        try { return (System.IO.File.GetAttributes(path) & FileAttributes.Directory) != 0 ? "directory" : "available"; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return "missing"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }
}
