using System.IO.Pipes;
using System.Text.Json;

namespace Diagram.Ipc;

/// <summary>
/// One running window per Windows user and logon session. The first launch owns a pipe that only that user can open; a later launch finds the
/// pipe taken, sends it the files it was given and exits. Another user, or the same user in another logon session, has a different pipe name and
/// so gets a window of its own: a launch is never forwarded across users.
/// </summary>
public sealed class InstanceGuard : IAsyncDisposable
{
    public const int MaxPaths = 64, MaxPathLength = 32767;
    readonly string name;
    readonly CancellationTokenSource stop = new();
    readonly List<NamedPipeServerStream> live = new();
    readonly object gate = new();
    readonly List<string> waiting = new();
    Func<IReadOnlyList<string>, Task>? handler;
    NamedPipeServerStream? listening;
    Task? loop;

    InstanceGuard(string name, NamedPipeServerStream first) { this.name = name; listening = first; loop = Task.Run(AcceptLoop); }

    /// <summary>Takes the instance, or returns null when another window already holds it.</summary>
    public static InstanceGuard? TryAcquire(string name)
    {
        try { return new InstanceGuard(name, New(name, first: true)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static NamedPipeServerStream New(string name, bool first) =>
        new(name, PipeDirection.InOut, Wire.MaxClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    /// <summary>Starts delivering forwarded paths. Anything that arrived while the window was still starting is delivered now.</summary>
    public void Attach(Func<IReadOnlyList<string>, Task> onPaths)
    {
        List<string> early;
        lock (gate) { handler = onPaths; early = [.. waiting]; waiting.Clear(); }
        if (early.Count > 0) _ = onPaths(early);
    }

    /// <summary>Hands paths to the running window. False when nothing answered in time (the caller says so; it must not start a second window).</summary>
    public static async Task<bool> ForwardAsync(string name, IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await c.ConnectAsync(cts.Token);
            using var msg = JsonSerializer.SerializeToDocument(new { kind = "open", paths });
            await FrameCodec.WriteAsync(c, msg, cts.Token);
            using var reply = await FrameCodec.ReadAsync(c, cts.Token);
            return reply.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException or FrameException or TimeoutException) { return false; }
    }

    async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            lock (gate) server = listening!;
            try { await server.WaitForConnectionAsync(stop.Token); }
            catch { server.Dispose(); return; }
            lock (gate) { live.Add(server); }
            // the next listener exists before this connection is served, so the name is never free while the window runs
            try { var next = New(name, first: false); lock (gate) listening = next; }
            catch (IOException) { await Task.Delay(50); lock (gate) listening = New(name, first: false); }
            _ = Task.Run(() => Serve(server));
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5)); // a client that connects and says nothing must not hold a slot
            using var frame = await FrameCodec.ReadAsync(pipe, timeout.Token);
            var root = frame.RootElement;
            var ok = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var k) && k.GetString() == "open"
                && root.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Array && ps.GetArrayLength() <= MaxPaths;
            var paths = new List<string>();
            if (ok)
                foreach (var p in root.GetProperty("paths").EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 and <= MaxPathLength } s) { ok = false; break; }
                    paths.Add(s);
                }
            if (ok)
            {
                Func<IReadOnlyList<string>, Task>? h;
                lock (gate) { h = handler; if (h is null) waiting.AddRange(paths); }
                if (h is not null) _ = h(paths);
            }
            using var reply = JsonSerializer.SerializeToDocument(new { ok });
            await FrameCodec.WriteAsync(pipe, reply, timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or FrameException or ObjectDisposedException) { }
        finally { lock (gate) live.Remove(pipe); try { pipe.Dispose(); } catch { } }
    }

    int disposed;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        stop.Cancel();
        lock (gate) { foreach (var s in live.ToArray()) { try { s.Dispose(); } catch { } } listening?.Dispose(); listening = null; }
        if (loop is not null) try { await loop; } catch { }
        stop.Dispose();
    }
}
