using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Diagram.Core.Contracts;

namespace Diagram.Ipc;

/// <summary>Who is calling. The label is assigned by the server and is never text a client supplied.</summary>
public sealed record ConnectionInfo(string ConnectionId, string ClientLabel);

public delegate Task<ResponseEnvelope> RequestHandler(ConnectionInfo connection, RequestEnvelope request, CancellationToken ct);

/// <summary>
/// The application side of the pipe: current-user only, one version handshake per connection, bounded clients (eight), bounded
/// pending calls per connection (sixteen), one writer per connection. Every request reaches the handler stamped with a host-made
/// origin; whatever origin a client wrote is discarded. Responses may complete out of order and are matched by request id.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    readonly string pipeName;
    readonly RequestHandler handler;
    readonly CancellationTokenSource stop = new();
    readonly List<NamedPipeServerStream> live = new();
    readonly object gate = new();
    NamedPipeServerStream? listening;
    Task? acceptLoop;
    int connections, peerChecksPassed;

    PipeServer(string pipeName, RequestHandler handler) { this.pipeName = pipeName; this.handler = handler; }

    public int PeerChecksPassed => Volatile.Read(ref peerChecksPassed);
    public int ClientCount { get { lock (gate) return live.Count; } }

    /// <summary>Creates the first pipe instance synchronously: a name collision throws instead of attaching to another owner.</summary>
    public static PipeServer Start(string pipeName, RequestHandler handler)
    {
        var server = new PipeServer(pipeName, handler);
        server.listening = server.NewInstance(first: true);
        server.acceptLoop = Task.Run(server.AcceptLoop);
        return server;
    }

    NamedPipeServerStream NewInstance(bool first)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, Wire.MaxClients, PipeTransmissionMode.Byte, options);
    }

    public PipeSecurity SecurityDescriptor() { lock (gate) return (listening ?? live.First()).GetAccessControl(); }

    /// <summary>Closes every client connection (used by tests and on shutdown). Pending calls on those connections are never answered.</summary>
    public void DropConnections() { lock (gate) foreach (var s in live.ToArray()) { try { s.Dispose(); } catch { } } }

    async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try { lock (gate) server = listening ?? NewInstance(first: false); }
            catch (IOException) { await Task.Delay(50); continue; } // all instances busy
            try { await server.WaitForConnectionAsync(stop.Token); }
            catch { server.Dispose(); return; }
            lock (gate) { live.Add(server); listening = null; }
            _ = Task.Run(() => Serve(server));
            try { lock (gate) listening = NewInstance(first: false); }
            catch (IOException) { /* eight clients: the next loop iteration retries */ }
        }
    }

    static bool PeerIsSameUserAndSession(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var ok = false;
        using var me = WindowsIdentity.GetCurrent();
        var mySid = me.User;
        var mySession = PipeNames.LogonSessionId(me.AccessToken.DangerousGetHandle());
        pipe.RunAsClient(() =>
        {
            using var peer = WindowsIdentity.GetCurrent();
            ok = peer.User == mySid && PipeNames.LogonSessionId(peer.AccessToken.DangerousGetHandle()) == mySession;
        });
        return ok;
    }

    static string? IdOf(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

    async Task Serve(NamedPipeServerStream pipe)
    {
        var ct = stop.Token;
        var outbound = Channel.CreateBounded<ResponseEnvelope>(new BoundedChannelOptions(Wire.WriterQueue) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        Task? writer = null;
        var pending = 0;
        try
        {
            ConnectionInfo info;
            // The first frame is the handshake. The peer identity can be read only after a read on Windows, so the check runs here,
            // before anything the client sent is acted on.
            using (var first = await FrameCodec.ReadAsync(pipe, ct))
            {
                if (!PeerIsSameUserAndSession(pipe)) return;
                Interlocked.Increment(ref peerChecksPassed);
                var root = first.RootElement;
                var id = IdOf(root);
                var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                if (method != "app.hello")
                {
                    await FrameCodec.WriteAsync(pipe, Wire.ToDocument(Wire.Error(id, "invalid_request", "the first frame must be app.hello")), ct);
                    return;
                }
                var version = root.TryGetProperty("params", out var p) && p.TryGetProperty("protocolVersion", out var v) && v.TryGetInt32(out var n) ? n : 0;
                if (version != Wire.ProtocolVersion)
                {
                    await FrameCodec.WriteAsync(pipe, Wire.ToDocument(Wire.Error(id, "unsupported_version", $"protocol {version} is not supported")), ct);
                    return;
                }
                var ordinal = Interlocked.Increment(ref connections);
                info = new ConnectionInfo(Guid.NewGuid().ToString(), $"Client {ordinal}"); // server-assigned; caller text is never echoed
                await FrameCodec.WriteAsync(pipe, Wire.ToDocument(Wire.Ok(id ?? "hello", new JsonObject
                {
                    ["protocolVersion"] = Wire.ProtocolVersion, ["connectionId"] = info.ConnectionId, ["clientLabel"] = info.ClientLabel,
                    ["capabilities"] = new JsonArray("doc.requests"),
                })), ct);
            }

            writer = Task.Run(async () =>
            {
                await foreach (var r in outbound.Reader.ReadAllAsync(CancellationToken.None))
                {
                    try { using var doc = Wire.ToDocument(r); await FrameCodec.WriteAsync(pipe, doc, ct); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or FrameException) { return; }
                }
            });

            while (!ct.IsCancellationRequested)
            {
                using var frame = await FrameCodec.ReadAsync(pipe, ct);
                var id = IdOf(frame.RootElement);
                RequestEnvelope? request;
                try { request = frame.RootElement.Deserialize<RequestEnvelope>(ContractJson.Options); }
                catch (JsonException e) { request = null; await outbound.Writer.WriteAsync(Wire.Error(id, "invalid_request", "the request does not match the envelope schema: " + e.Message), ct); }
                if (request is null) continue;
                if (Interlocked.Increment(ref pending) > Wire.MaxPendingPerConnection)
                {
                    Interlocked.Decrement(ref pending);
                    await outbound.Writer.WriteAsync(Wire.Error(request.RequestId, "busy", $"more than {Wire.MaxPendingPerConnection} calls are already pending on this connection", retryable: true, outcome: "none"), ct);
                    continue;
                }
                var stamped = request with { Origin = new Origin { Source = "mcp", ClientLabel = info.ClientLabel, ConnectionId = info.ConnectionId } };
                _ = Task.Run(async () =>
                {
                    ResponseEnvelope response;
                    try { response = await handler(info, stamped, ct) with { RequestId = request.RequestId }; }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { response = Wire.Error(request.RequestId, "internal_error", e.GetType().Name); }
                    finally { Interlocked.Decrement(ref pending); }
                    try { await outbound.Writer.WriteAsync(response, ct); } catch (Exception e) when (e is OperationCanceledException or ChannelClosedException) { }
                });
            }
        }
        catch (FrameException e)
        {
            try { await FrameCodec.WriteAsync(pipe, Wire.ToDocument(Wire.Error(null, e.Code, e.Message)), CancellationToken.None); } catch { }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            outbound.Writer.TryComplete();
            if (writer is not null) { try { await writer.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            lock (gate) live.Remove(pipe);
            try { pipe.Dispose(); } catch { }
        }
    }

    int disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        stop.Cancel();
        DropConnections();
        lock (gate) { listening?.Dispose(); listening = null; }
        if (acceptLoop != null) try { await acceptLoop; } catch { }
        stop.Dispose();
    }
}
