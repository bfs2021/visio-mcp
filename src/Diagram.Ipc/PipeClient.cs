using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Diagram.Core.Contracts;

namespace Diagram.Ipc;

/// <summary>
/// The shim's side of the pipe. It connects lazily, never launches the application, matches replies to calls by request id so calls
/// can overlap, and never re-sends a request: after a lost connection or a deadline the outcome is reported as unknown and the
/// caller decides whether to retry (with the same transaction id, which the application deduplicates).
/// </summary>
public sealed class PipeClient(string pipeName, TimeSpan connectTimeout, string clientLabel = "Diagram.Mcp") : IAsyncDisposable
{
    sealed class Connection(NamedPipeClientStream stream)
    {
        public readonly NamedPipeClientStream Stream = stream;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseEnvelope>> Pending = new();
        public Task? Reader;
        public volatile bool Closed;
    }

    readonly SemaphoreSlim connectGate = new(1, 1);
    Connection? conn;
    long next;

    public async Task<ResponseEnvelope> CallAsync(RequestEnvelope request, CancellationToken ct)
    {
        Connection c;
        try { c = await EnsureConnectedAsync(ct); }
        catch (PipeFailure f) { return Wire.Error(request.RequestId, f.Code, f.Message, f.Retryable, outcome: "none"); }

        var wireId = "w" + Interlocked.Increment(ref next);
        var tcs = new TaskCompletionSource<ResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Pending[wireId] = tcs;
        try
        {
            using var doc = Wire.ToDocument(request with { RequestId = wireId });
            await c.WriteLock.WaitAsync(ct);
            try { await FrameCodec.WriteAsync(c.Stream, doc, ct); }
            finally { c.WriteLock.Release(); }
        }
        catch (OperationCanceledException) { c.Pending.TryRemove(wireId, out _); throw; }
        catch (Exception e) when (e is IOException or ObjectDisposedException or FrameException)
        {
            c.Pending.TryRemove(wireId, out _);
            Close(c);
            // A failed write may have delivered part of the frame: never claim the call did not happen.
            return Wire.Error(request.RequestId, e is FrameException fe ? fe.Code : "not_running", "the application closed the connection", retryable: true, outcome: "unknown");
        }

        try
        {
            var response = await tcs.Task.WaitAsync(ct);
            return response with { RequestId = request.RequestId };
        }
        catch (OperationCanceledException)
        {
            c.Pending.TryRemove(wireId, out _);
            return Wire.Error(request.RequestId, "timeout_unknown", "no reply before the deadline", outcome: "unknown");
        }
    }

    sealed class PipeFailure(string code, string message, bool retryable) : Exception(message)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }

    async Task<Connection> EnsureConnectedAsync(CancellationToken ct)
    {
        await connectGate.WaitAsync(ct);
        try
        {
            if (conn is { Closed: false, Stream.IsConnected: true } existing) return existing;
            if (conn is not null) Close(conn);
            var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(connectTimeout);
                await stream.ConnectAsync(timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                stream.Dispose();
                if (ct.IsCancellationRequested) throw;
                throw new PipeFailure("not_running", "the application is not running", retryable: true);
            }
            if (OperatingSystem.IsWindows() && !OwnerIsCurrentUser(stream))
            {
                stream.Dispose();
                throw new PipeFailure("path_not_permitted", "the pipe is owned by another user; refusing to connect", retryable: false);
            }

            try
            {
                using var hello = Wire.ToDocument(Wire.Hello(clientLabel, Wire.CleanVersion(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString())));
                await FrameCodec.WriteAsync(stream, hello, ct);
                using var reply = await FrameCodec.ReadAsync(stream, ct);
                if (reply.RootElement.TryGetProperty("error", out var err))
                    throw new PipeFailure(err.GetProperty("code").GetString() ?? "internal_error", err.GetProperty("message").GetString() ?? "", retryable: false);
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or FrameException)
            {
                stream.Dispose();
                throw new PipeFailure("not_running", "the application closed the connection during the handshake", retryable: true);
            }
            catch (PipeFailure) { stream.Dispose(); throw; }

            var c = new Connection(stream);
            c.Reader = Task.Run(() => ReadLoop(c));
            conn = c;
            return c;
        }
        finally { connectGate.Release(); }
    }

    async Task ReadLoop(Connection c)
    {
        try
        {
            while (true)
            {
                using var frame = await FrameCodec.ReadAsync(c.Stream, CancellationToken.None);
                ResponseEnvelope? response;
                try { response = frame.RootElement.Deserialize<ResponseEnvelope>(ContractJson.Options); } catch (JsonException) { continue; }
                if (response is not null && c.Pending.TryRemove(response.RequestId, out var tcs)) tcs.TrySetResult(response); // an unknown id is ignored
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or FrameException) { }
        finally
        {
            c.Closed = true;
            foreach (var (id, tcs) in c.Pending.ToArray())
            {
                if (c.Pending.TryRemove(id, out _))
                    tcs.TrySetResult(Wire.Error(id, "not_running", "the application closed the connection", retryable: true, outcome: "unknown"));
            }
        }
    }

    /// <summary>The pipe object's owner must be the current user: the server created it, so it carries the server's identity.</summary>
    static bool OwnerIsCurrentUser(NamedPipeClientStream c)
    {
        var owner = c.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        using var me = WindowsIdentity.GetCurrent();
        return owner?.Equals(me.User) == true;
    }

    static void Close(Connection c) { c.Closed = true; try { c.Stream.Dispose(); } catch { } }

    public ValueTask DisposeAsync()
    {
        if (conn is not null) Close(conn);
        conn = null;
        return ValueTask.CompletedTask;
    }
}
