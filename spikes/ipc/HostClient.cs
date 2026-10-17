using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IpcProbe;

/// <summary>
/// Lazy, single-outstanding-call client for the host pipe. Never launches the host, never retries a request
/// after delivery may have happened, and reconnects on the next call.
/// </summary>
public sealed class HostClient(string pipeName, TimeSpan connectTimeout) : IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    NamedPipeClientStream? conn;
    int nextId;

    public async Task<JsonDocument> CallAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            var id = "c" + Interlocked.Increment(ref nextId);
            var sent = false;
            try
            {
                using var request = Envelope.Request(method, id, @params);
                await FrameCodec.WriteAsync(conn!, request, ct);
                sent = true;
                using var reply = await FrameCodec.ReadAsync(conn!, ct);
                var root = reply.RootElement;
                if (root.TryGetProperty("error", out var err))
                    throw new HostException(err.GetProperty("code").GetString()!, err.GetProperty("message").GetString() ?? "", err.TryGetProperty("retryable", out var r) && r.GetBoolean());
                return JsonDocument.Parse(root.GetProperty("result").GetRawText());
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
            {
                Reset();
                throw new HostException("not_running", "the application closed the connection", retryable: true, outcome: sent ? "unknown" : null);
            }
            catch (FrameException e)
            {
                Reset();
                throw new HostException(e.Code, e.Message);
            }
        }
        finally { gate.Release(); }
    }

    async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (conn is { IsConnected: true }) return;
        Reset();
        var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(connectTimeout);
            await c.ConnectAsync(timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            c.Dispose();
            if (ct.IsCancellationRequested) throw;
            throw new HostException("not_running", "the application is not running", retryable: true);
        }

        if (OperatingSystem.IsWindows() && !OwnerIsCurrentUser(c))
        {
            c.Dispose();
            throw new HostException("path_not_permitted", "the pipe is owned by another user; refusing to connect");
        }

        conn = c;
        try
        {
            using var hello = Envelope.Request("app.hello", "hello", new { protocolVersion = Envelope.ProtocolVersion, clientLabel = "McpProbe" });
            await FrameCodec.WriteAsync(c, hello, ct);
            using var reply = await FrameCodec.ReadAsync(c, ct);
            if (reply.RootElement.TryGetProperty("error", out var err))
                throw new HostException(err.GetProperty("code").GetString()!, err.GetProperty("message").GetString() ?? "");
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            Reset();
            throw new HostException("not_running", "the application closed the connection during handshake", retryable: true);
        }
    }

    /// <summary>The pipe object's owner must be the current user: the server created it, so it carries the server's identity.</summary>
    static bool OwnerIsCurrentUser(NamedPipeClientStream c)
    {
        var owner = c.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        using var me = WindowsIdentity.GetCurrent();
        return owner?.Equals(me.User) == true;
    }

    void Reset() { try { conn?.Dispose(); } catch { } conn = null; }

    public ValueTask DisposeAsync() { Reset(); gate.Dispose(); return ValueTask.CompletedTask; }
}
