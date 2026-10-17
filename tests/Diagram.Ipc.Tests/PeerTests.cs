using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Ipc;
using Xunit;

public class PeerTests
{
    static string NewPipe() => $"ipc-test-{Guid.NewGuid():N}";
    static CancellationToken Ct(int seconds = 20) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    static RequestEnvelope Req(string method, string id = "r1", JsonObject? p = null) => new() { RequestId = id, Method = method, Params = p ?? new JsonObject() };

    static RequestHandler Echo(List<RequestEnvelope>? seen = null) => (conn, req, ct) =>
    {
        seen?.Add(req);
        return Task.FromResult(Wire.Ok(req.RequestId, new JsonObject { ["method"] = req.Method, ["connectionId"] = req.Origin?.ConnectionId, ["source"] = req.Origin?.Source, ["label"] = req.Origin?.ClientLabel }));
    };

    static async Task<NamedPipeClientStream> RawConnect(string pipe)
    {
        var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await c.ConnectAsync(5000);
        return c;
    }

    static JsonDocument Raw(string method, string id, object? @params = null) =>
        JsonSerializer.SerializeToDocument(new { protocolVersion = 1, kind = "request", requestId = id, method, @params = @params ?? new { } });

    [Fact]
    public async Task FirstFrameHandshake()
    {
        var pipe = NewPipe();
        var seen = new List<RequestEnvelope>();
        await using var server = PipeServer.Start(pipe, Echo(seen));

        // a first frame that is not the handshake is refused, and never reaches the handler
        await using (var c = await RawConnect(pipe))
        {
            await FrameCodec.WriteAsync(c, Raw("doc.summary", "x"), Ct());
            using var reply = await FrameCodec.ReadAsync(c, Ct());
            Assert.Equal("invalid_request", reply.RootElement.GetProperty("error").GetProperty("code").GetString());
            await Assert.ThrowsAnyAsync<Exception>(() => FrameCodec.ReadAsync(c, Ct()));
        }
        Assert.Empty(seen);

        // an unsupported version is refused
        await using (var c = await RawConnect(pipe))
        {
            await FrameCodec.WriteAsync(c, Raw("app.hello", "h", new { protocolVersion = 99, clientLabel = "x" }), Ct());
            using var reply = await FrameCodec.ReadAsync(c, Ct());
            Assert.Equal("unsupported_version", reply.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // a good handshake gets a server-made identity; caller text is never echoed
        await using (var c = await RawConnect(pipe))
        {
            await FrameCodec.WriteAsync(c, Raw("app.hello", "h", new { protocolVersion = 1, clientLabel = "<script>x</script>" }), Ct());
            using var hello = await FrameCodec.ReadAsync(c, Ct());
            var r = hello.RootElement.GetProperty("result");
            Assert.False(string.IsNullOrEmpty(r.GetProperty("connectionId").GetString()));
            Assert.DoesNotContain("script", r.GetProperty("clientLabel").GetString());
            await FrameCodec.WriteAsync(c, Raw("doc.summary", "s"), Ct());
            using var sum = await FrameCodec.ReadAsync(c, Ct());
            Assert.Equal("s", sum.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("doc.summary", sum.RootElement.GetProperty("result").GetProperty("method").GetString());
        }
    }

    [Fact]
    public async Task OriginIsStampedByTheHostAndAnyCallerOriginIsDiscarded()
    {
        var pipe = NewPipe();
        var seen = new List<RequestEnvelope>();
        await using var server = PipeServer.Start(pipe, Echo(seen));
        await using var client = new PipeClient(pipe, TimeSpan.FromSeconds(2));
        var forged = Req("doc.summary") with { Origin = new Origin { Source = "mcp", ConnectionId = "forged", ClientLabel = "admin" } };
        var reply = await client.CallAsync(forged, Ct());
        Assert.Null(reply.Error);
        var result = reply.Result!.Value;
        Assert.NotEqual("forged", result.GetProperty("connectionId").GetString());
        Assert.NotEqual("admin", result.GetProperty("label").GetString());
        Assert.Equal("mcp", result.GetProperty("source").GetString());
    }

    [Fact]
    public async Task OverlappingCallsCorrelateEvenWithTheSameCallerId()
    {
        var pipe = NewPipe();
        await using var server = PipeServer.Start(pipe, async (conn, req, ct) =>
        {
            await Task.Delay(req.Params["delayMs"]!.GetValue<int>(), ct);
            return Wire.Ok(req.RequestId, new JsonObject { ["echo"] = req.Params["tag"]!.GetValue<string>() });
        });
        await using var client = new PipeClient(pipe, TimeSpan.FromSeconds(2));
        var calls = new[] { 300, 10, 150, 50, 200 }.Select((d, i) =>
            client.CallAsync(Req("doc.summary", "same", new JsonObject { ["delayMs"] = d, ["tag"] = $"t{i}" }), Ct())).ToArray();
        var replies = await Task.WhenAll(calls);
        Assert.Equal(new[] { "t0", "t1", "t2", "t3", "t4" }, replies.Select(r => r.Result!.Value.GetProperty("echo").GetString()).ToArray()); // out-of-order completion, right answers
        Assert.All(replies, r => Assert.Equal("same", r.RequestId));
    }

    [Fact]
    public async Task EightClientsSixteenPendingLimit()
    {
        var pipe = NewPipe();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        await using var server = PipeServer.Start(pipe, async (conn, req, ct) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
            return Wire.Ok(req.RequestId, new JsonObject());
        });

        // sixteen calls may be pending on one connection; the seventeenth is refused at once and was never started
        await using var one = new PipeClient(pipe, TimeSpan.FromSeconds(2));
        var pending = Enumerable.Range(0, 16).Select(i => one.CallAsync(Req("doc.summary", $"p{i}"), Ct())).ToList();
        while (Volatile.Read(ref started) < 16) await Task.Delay(10);
        var over = await one.CallAsync(Req("doc.summary", "p16"), Ct());
        Assert.Equal("busy", over.Error!.Code);
        Assert.True(over.Error.Retryable);
        Assert.Equal("none", over.Error.Outcome);
        Assert.Equal(16, Volatile.Read(ref started));
        release.SetResult();
        Assert.All(await Task.WhenAll(pending), r => Assert.Null(r.Error));

        // eight connections are served; a ninth cannot get a pipe instance
        var clients = new List<PipeClient> { one };
        for (var i = 0; i < 7; i++)
        {
            var c = new PipeClient(pipe, TimeSpan.FromSeconds(2));
            Assert.Null((await c.CallAsync(Req("doc.summary"), Ct())).Error);
            clients.Add(c);
        }
        Assert.Equal(8, server.ClientCount);
        await using var ninth = new PipeClient(pipe, TimeSpan.FromMilliseconds(400));
        var refused = await ninth.CallAsync(Req("doc.summary"), Ct());
        Assert.Equal("not_running", refused.Error!.Code);
        foreach (var c in clients.Skip(1)) await c.DisposeAsync();
    }

    [Fact]
    public async Task SameUserSessionOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipe = NewPipe();
        await using var server = PipeServer.Start(pipe, Echo());
        var me = WindowsIdentity.GetCurrent().User!;
        var rules = server.SecurityDescriptor().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToList();
        Assert.Contains(me, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), allowed);
        await using var client = new PipeClient(pipe, TimeSpan.FromSeconds(2));
        Assert.Null((await client.CallAsync(Req("doc.summary"), Ct())).Error);
        Assert.Equal(1, server.PeerChecksPassed); // user and logon session were both checked on the first frame
        Assert.Contains(PipeNames.LogonSessionId().ToString("x"), PipeNames.For());
        Assert.Contains(me.Value, PipeNames.For());
    }

    [Fact]
    public async Task OnlyOneApplicationOwnsAPipeName()
    {
        var pipe = NewPipe();
        await using var first = PipeServer.Start(pipe, Echo());
        var e = Record.Exception(() => PipeServer.Start(pipe, Echo())); // a collision fails; it never attaches to the other owner
        Assert.True(e is IOException or UnauthorizedAccessException, e?.GetType().Name ?? "no exception");
    }

    [Fact]
    public async Task UnknownMutationTimeout()
    {
        var pipe = NewPipe();
        var calls = 0;
        await using var server = PipeServer.Start(pipe, async (conn, req, ct) => { Interlocked.Increment(ref calls); await Task.Delay(Timeout.Infinite, ct); return Wire.Ok(req.RequestId, new JsonObject()); });
        await using var client = new PipeClient(pipe, TimeSpan.FromSeconds(2));

        // a deadline says nothing about whether the application acted
        var slow = await client.CallAsync(Req("doc.apply"), new CancellationTokenSource(300).Token);
        Assert.Equal("timeout_unknown", slow.Error!.Code);
        Assert.Equal("unknown", slow.Error.Outcome);

        // a connection that dies mid-call is also unknown, and the call is not re-sent
        var dropped = client.CallAsync(Req("doc.apply", "d"), Ct());
        while (Volatile.Read(ref calls) < 2) await Task.Delay(10);
        server.DropConnections();
        var reply = await dropped;
        Assert.Equal("not_running", reply.Error!.Code);
        Assert.Equal("unknown", reply.Error.Outcome);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task MissingApplicationIsNotRunningAndTheClientRecovers()
    {
        var pipe = NewPipe();
        await using var client = new PipeClient(pipe, TimeSpan.FromMilliseconds(300));
        var down = await client.CallAsync(Req("doc.summary"), Ct());
        Assert.Equal("not_running", down.Error!.Code);
        Assert.True(down.Error.Retryable);
        Assert.Equal("none", down.Error.Outcome); // nothing was sent

        await using var s1 = PipeServer.Start(pipe, Echo());
        Assert.Null((await client.CallAsync(Req("doc.summary"), Ct())).Error);
        await s1.DisposeAsync();
        Assert.Equal("not_running", (await client.CallAsync(Req("doc.summary"), Ct())).Error!.Code);
        await using var s2 = PipeServer.Start(pipe, Echo());
        Assert.Null((await client.CallAsync(Req("doc.summary"), Ct())).Error);
    }

    [Fact]
    public async Task HandlerFailureIsInternalError()
    {
        var pipe = NewPipe();
        await using var server = PipeServer.Start(pipe, (c, r, ct) => throw new InvalidOperationException("boom with C:\\secret\\path"));
        await using var client = new PipeClient(pipe, TimeSpan.FromSeconds(2));
        var reply = await client.CallAsync(Req("doc.summary"), Ct());
        Assert.Equal("internal_error", reply.Error!.Code);
        Assert.DoesNotContain("secret", reply.Error.Message); // the type name, never the message or a path
    }

    [Fact]
    public async Task MalformedRequestGetsInvalidRequestAndTheConnectionSurvives()
    {
        var pipe = NewPipe();
        await using var server = PipeServer.Start(pipe, Echo());
        await using var c = await RawConnect(pipe);
        await FrameCodec.WriteAsync(c, Raw("app.hello", "h", new { protocolVersion = 1, clientLabel = "x" }), Ct());
        (await FrameCodec.ReadAsync(c, Ct())).Dispose();
        using var bad = JsonDocument.Parse("""{"protocolVersion":1,"kind":"request","requestId":"b1","method":"doc.summary","params":{},"surprise":true}""");
        await FrameCodec.WriteAsync(c, bad, Ct());
        using var err = await FrameCodec.ReadAsync(c, Ct());
        Assert.Equal("b1", err.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("invalid_request", err.RootElement.GetProperty("error").GetProperty("code").GetString());
        await FrameCodec.WriteAsync(c, Raw("doc.summary", "ok"), Ct());
        using var good = await FrameCodec.ReadAsync(c, Ct());
        Assert.Equal("ok", good.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task LargeFramesRoundTrip()
    {
        var pipe = NewPipe();
        await using var server = PipeServer.Start(pipe, (c, r, ct) => Task.FromResult(Wire.Ok(r.RequestId, new JsonObject { ["length"] = r.Params["text"]!.GetValue<string>().Length })));
        await using var client = new PipeClient(pipe, TimeSpan.FromSeconds(2));
        var big = new string('a', 8 * 1024 * 1024);
        var reply = await client.CallAsync(Req("doc.apply", "big", new JsonObject { ["text"] = big }), Ct(60));
        Assert.Equal(big.Length, reply.Result!.Value.GetProperty("length").GetInt32());
    }
}
