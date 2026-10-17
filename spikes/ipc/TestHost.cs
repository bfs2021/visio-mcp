using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace IpcProbe;

/// <summary>Minimal stand-in for the WPF host: handshake, doc.summary and an echo used for the large-frame test.</summary>
public sealed class TestHost : IAsyncDisposable
{
    const int MaxClients = 8;
    readonly string pipeName;
    readonly bool hangSummary;
    readonly CancellationTokenSource stop = new();
    readonly List<NamedPipeServerStream> live = new();
    readonly object gate = new();
    readonly TaskCompletionSource summarySeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    NamedPipeServerStream? listening;
    Task? acceptLoop;
    int summaryCalls, peerChecksPassed, connections;

    public int SummaryCalls => Volatile.Read(ref summaryCalls);
    public int PeerChecksPassed => Volatile.Read(ref peerChecksPassed);

    TestHost(string pipeName, bool hangSummary) { this.pipeName = pipeName; this.hangSummary = hangSummary; }

    /// <summary>Creates the first pipe instance synchronously: a name collision throws instead of silently attaching to another owner.</summary>
    public static TestHost Start(string pipeName, bool hangSummary = false)
    {
        var host = new TestHost(pipeName, hangSummary);
        host.listening = host.NewInstance(first: true);
        host.acceptLoop = Task.Run(host.AcceptLoop);
        return host;
    }

    NamedPipeServerStream NewInstance(bool first)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, options);
    }

    public PipeSecurity ServerPipeSecurity()
    {
        lock (gate) return (listening ?? live.First()).GetAccessControl();
    }

    public Task WaitForSummaryAsync(CancellationToken ct) => summarySeen.Task.WaitAsync(ct);

    public void DropConnections()
    {
        lock (gate) foreach (var s in live.ToArray()) { try { s.Dispose(); } catch { } }
    }

    async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try { lock (gate) server = listening ?? NewInstance(first: false); }
            catch (IOException) { await Task.Delay(50); continue; } // all instances busy: retry
            try { await server.WaitForConnectionAsync(stop.Token); }
            catch { server.Dispose(); return; }
            lock (gate) { live.Add(server); listening = null; }
            _ = Task.Run(() => Serve(server));
            try { lock (gate) listening = NewInstance(first: false); }
            catch (IOException) { /* all eight instances busy: the next loop retries */ }
        }
    }

    bool PeerIsSameUserAndSession(NamedPipeServerStream pipe)
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

    async Task Serve(NamedPipeServerStream pipe)
    {
        var ct = stop.Token;
        try
        {
            // First frame must be the handshake. Windows only lets the server impersonate the peer after a read,
            // so the identity check runs once that frame has arrived and before anything in it is acted on.
            using (var first = await FrameCodec.ReadAsync(pipe, ct))
            {
                if (!PeerIsSameUserAndSession(pipe)) return; // refuse silently: nothing is executed for another identity
                Interlocked.Increment(ref peerChecksPassed);
                var root = first.RootElement;
                var id = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
                if (Method(root) != "app.hello")
                {
                    await FrameCodec.WriteAsync(pipe, Envelope.Error(id, "invalid_request", "first frame must be app.hello"), ct);
                    return;
                }
                var version = root.GetProperty("params").TryGetProperty("protocolVersion", out var v) ? v.GetInt32() : 0;
                if (version != Envelope.ProtocolVersion)
                {
                    await FrameCodec.WriteAsync(pipe, Envelope.Error(id, "unsupported_version", $"protocol {version} is not supported"), ct);
                    return;
                }
                var n = Interlocked.Increment(ref connections);
                await FrameCodec.WriteAsync(pipe, Envelope.Result(id, new
                {
                    protocolVersion = Envelope.ProtocolVersion,
                    connectionId = Guid.NewGuid().ToString(),
                    clientLabel = $"Client {n}", // server-assigned; caller text is never echoed
                    capabilities = new[] { "doc.summary" },
                }), ct);
            }

            while (!ct.IsCancellationRequested)
            {
                using var req = await FrameCodec.ReadAsync(pipe, ct);
                var root = req.RootElement;
                var id = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
                switch (Method(root))
                {
                    case "doc.summary":
                        Interlocked.Increment(ref summaryCalls);
                        summarySeen.TrySetResult();
                        if (hangSummary) await Task.Delay(Timeout.Infinite, ct);
                        await FrameCodec.WriteAsync(pipe, Envelope.Result(id, new { documentId = "00000000-0000-4000-8000-000000000001", revision = 7, pageCount = 1, title = "Probe" }), ct);
                        break;
                    case "test.echo":
                        var len = root.GetProperty("params").GetProperty("text").GetString()!.Length;
                        await FrameCodec.WriteAsync(pipe, Envelope.Result(id, new { length = len }), ct);
                        break;
                    default:
                        await FrameCodec.WriteAsync(pipe, Envelope.Error(id, "method_not_found", "unknown method"), ct);
                        break;
                }
            }
        }
        catch (FrameException e)
        {
            try { await FrameCodec.WriteAsync(pipe, Envelope.Error(null, e.Code, e.Message), CancellationToken.None); } catch { }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            lock (gate) live.Remove(pipe);
            try { pipe.Dispose(); } catch { }
        }
    }

    static string? Method(JsonElement root) => root.TryGetProperty("method", out var m) ? m.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        DropConnections();
        lock (gate) { listening?.Dispose(); listening = null; }
        if (acceptLoop != null) try { await acceptLoop; } catch { }
        stop.Dispose();
    }
}
