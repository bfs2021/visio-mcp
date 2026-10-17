using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IpcProbe;
using ModelContextProtocol.Client;
using Xunit;

public class TransportTests
{
    static string NewPipe() => $"probe-{Guid.NewGuid():N}";
    static CancellationToken Ct(int seconds = 20) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    // ---- framing -------------------------------------------------------------------------------

    /// <summary>A stream that hands back one byte per read, to prove partial reads are reassembled exactly.</summary>
    sealed class Dribble(byte[] data) : Stream
    {
        int pos;
        public override int Read(byte[] buffer, int offset, int count) => pos >= data.Length ? 0 : (buffer[offset] = data[pos++]) >= 0 ? 1 : 1;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (pos >= data.Length || buffer.Length == 0) return 0;
            await Task.Yield();
            buffer.Span[0] = data[pos++];
            return 1;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task PartialReadRoundTrips()
    {
        using var doc = JsonDocument.Parse("""{"protocolVersion":1,"kind":"request","requestId":"r1","method":"app.hello","params":{"text":"héllo ✓"}}""");
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, doc, Ct());
        var bytes = ms.ToArray();
        Assert.Equal((uint)(bytes.Length - 4), BitConverter.ToUInt32(bytes, 0)); // 4-byte little-endian length prefix

        using var back = await FrameCodec.ReadAsync(new Dribble(bytes), Ct());
        Assert.Equal(doc.RootElement.GetRawText(), back.RootElement.GetRawText());

        // A stream that ends mid-frame is reported, not hung on or half-parsed.
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(new Dribble(bytes[..^3]), Ct()));
    }

    [Fact]
    public async Task FrameLengthAbove32MiBRejectedBeforeAllocation()
    {
        var header = BitConverter.GetBytes((uint)(FrameCodec.MaxFrameBytes + 1)); // little-endian on every supported platform
        var before = FrameCodec.PayloadAllocations;
        var ex = await Assert.ThrowsAsync<FrameException>(() => FrameCodec.ReadAsync(new MemoryStream(header), Ct()));
        Assert.Equal("limit_exceeded", ex.Code);
        long oversizedFramePayloadAllocations = FrameCodec.PayloadAllocations - before;
        Assert.Equal(0, oversizedFramePayloadAllocations);

        // Exactly the limit is allowed to start (it then fails as truncated, but only after validation passed).
        var ok = BitConverter.GetBytes((uint)FrameCodec.MaxFrameBytes);
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(new MemoryStream(ok), Ct()));
        // Zero-length frames carry no JSON and are malformed.
        var zero = await Assert.ThrowsAsync<FrameException>(() => FrameCodec.ReadAsync(new MemoryStream(new byte[4]), Ct()));
        Assert.Equal("invalid_request", zero.Code);
    }

    [Fact]
    public async Task MalformedJsonIsInvalidRequest()
    {
        var body = Encoding.UTF8.GetBytes("{not json");
        var frame = BitConverter.GetBytes((uint)body.Length).Concat(body).ToArray();
        var ex = await Assert.ThrowsAsync<FrameException>(() => FrameCodec.ReadAsync(new MemoryStream(frame), Ct()));
        Assert.Equal("invalid_request", ex.Code);
    }

    // ---- host stub -----------------------------------------------------------------------------

    static async Task<NamedPipeClientStream> RawConnect(string pipe)
    {
        var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await c.ConnectAsync(5000);
        return c;
    }

    static JsonDocument Req(string method, string id = "r1", object? @params = null) =>
        JsonSerializer.SerializeToDocument(new { protocolVersion = 1, kind = "request", requestId = id, method, @params = @params ?? new { } });

    [Fact]
    public async Task NonHandshakeFirstFrameRejected()
    {
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe);
        await using var c = await RawConnect(pipe);
        await FrameCodec.WriteAsync(c, Req("doc.summary"), Ct());
        using var reply = await FrameCodec.ReadAsync(c, Ct());
        Assert.Equal("response", reply.RootElement.GetProperty("kind").GetString());
        Assert.Equal("invalid_request", reply.RootElement.GetProperty("error").GetProperty("code").GetString());
        // The host then closes the connection: a second read sees end of stream.
        await Assert.ThrowsAnyAsync<Exception>(() => FrameCodec.ReadAsync(c, Ct()));
        Assert.Equal(0, host.SummaryCalls); // the request was never executed
    }

    [Fact]
    public async Task HandshakeThenSummaryWorks()
    {
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe);
        await using var c = await RawConnect(pipe);
        await FrameCodec.WriteAsync(c, Req("app.hello", "h", new { protocolVersion = 1, clientLabel = "<script>x</script>" }), Ct());
        using var hello = await FrameCodec.ReadAsync(c, Ct());
        var r = hello.RootElement.GetProperty("result");
        Assert.Equal(1, r.GetProperty("protocolVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(r.GetProperty("connectionId").GetString()));
        // The server assigns the display label; it does not echo caller-supplied text.
        Assert.DoesNotContain("script", r.GetProperty("clientLabel").GetString());

        await FrameCodec.WriteAsync(c, Req("doc.summary", "s"), Ct());
        using var sum = await FrameCodec.ReadAsync(c, Ct());
        Assert.Equal("s", sum.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(7, sum.RootElement.GetProperty("result").GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task UnsupportedProtocolVersionRejected()
    {
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe);
        await using var c = await RawConnect(pipe);
        await FrameCodec.WriteAsync(c, Req("app.hello", "h", new { protocolVersion = 99, clientLabel = "x" }), Ct());
        using var reply = await FrameCodec.ReadAsync(c, Ct());
        Assert.Equal("unsupported_version", reply.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingHostIsNotRunning()
    {
        await using var client = new HostClient(NewPipe(), TimeSpan.FromMilliseconds(300));
        var error = await Assert.ThrowsAsync<HostException>(() => client.CallAsync("doc.summary", null, Ct()));
        Assert.Equal("not_running", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task ClientReconnectsAfterHostRestart()
    {
        var pipe = NewPipe();
        await using var client = new HostClient(pipe, TimeSpan.FromSeconds(2));
        var host1 = TestHost.Start(pipe);
        Assert.Equal(7, (await client.CallAsync("doc.summary", null, Ct())).RootElement.GetProperty("revision").GetInt32());
        await host1.DisposeAsync();
        var gone = await Assert.ThrowsAsync<HostException>(() => client.CallAsync("doc.summary", null, Ct()));
        Assert.Equal("not_running", gone.Code);
        await using var host2 = TestHost.Start(pipe);
        Assert.Equal(7, (await client.CallAsync("doc.summary", null, Ct())).RootElement.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task DisconnectMidCallSettlesAsUnknownAndDoesNotRetry()
    {
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe, hangSummary: true);
        await using var client = new HostClient(pipe, TimeSpan.FromSeconds(2));
        var call = client.CallAsync("doc.summary", null, Ct());
        await host.WaitForSummaryAsync(Ct());
        host.DropConnections();
        var error = await Assert.ThrowsAsync<HostException>(() => call);
        Assert.Equal("not_running", error.Code);
        Assert.Equal("unknown", error.Outcome);
        Assert.Equal(1, host.SummaryCalls); // the shim did not silently re-send the request
    }

    [Fact]
    public async Task Frame8MiBRoundTrips()
    {
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe);
        await using var c = await RawConnect(pipe);
        await FrameCodec.WriteAsync(c, Req("app.hello", "h", new { protocolVersion = 1, clientLabel = "x" }), Ct());
        (await FrameCodec.ReadAsync(c, Ct())).Dispose();
        var big = new string('a', 8 * 1024 * 1024);
        var sw = Stopwatch.StartNew();
        await FrameCodec.WriteAsync(c, Req("test.echo", "e", new { text = big }), Ct(60));
        using var echo = await FrameCodec.ReadAsync(c, Ct(60));
        sw.Stop();
        Assert.Equal(big.Length, echo.RootElement.GetProperty("result").GetProperty("length").GetInt32());
        Evidence.Record("frame8MiBRoundTripMs", sw.Elapsed.TotalMilliseconds);
    }

    // ---- Windows access control ----------------------------------------------------------------

    [Fact]
    public async Task PipeIsRestrictedToCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return; // Unix fake transport proves nothing about Windows ACLs.
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe);
        await using var c = await RawConnect(pipe);
        var me = WindowsIdentity.GetCurrent().User!;
        var rules = host.ServerPipeSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var allowed = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToList();
        Assert.Contains(me, allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), allowed);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), allowed);
        Evidence.Record("pipeAllowedSids", string.Join(",", allowed.Select(s => s.Value)));
        // Both ends confirm the peer is the same user and logon session (the server checks once the first frame arrives).
        await FrameCodec.WriteAsync(c, Req("app.hello", "h", new { protocolVersion = 1, clientLabel = "x" }), Ct());
        (await FrameCodec.ReadAsync(c, Ct())).Dispose();
        Assert.Equal(1, host.PeerChecksPassed);
        Assert.Contains(PipeNames.LogonSessionId().ToString("x"), PipeNames.For(1));
        Assert.Contains(me.Value, PipeNames.For(1));
    }

    // ---- real MCP over stdio -------------------------------------------------------------------

    static string ShimDll()
    {
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "mcp", "bin", cfg, "net10.0"));
        return Path.Combine(root, "McpProbe.dll");
    }

    sealed class Shim : IDisposable
    {
        public readonly Process P;
        public readonly List<string> Transcript = new();
        public readonly StringBuilder Stderr = new();
        public Shim(string pipe)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{ShimDll()}\"")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), UseShellExecute = false,
            };
            psi.Environment["DIAGRAM_PIPE"] = pipe;
            P = Process.Start(psi)!;
            P.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (Stderr) Stderr.AppendLine(e.Data); };
            P.BeginErrorReadLine();
        }
        public async Task<JsonDocument> Rpc(string line, CancellationToken ct, bool expectReply = true)
        {
            Transcript.Add("-> " + line);
            await P.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await P.StandardInput.FlushAsync(ct);
            if (!expectReply) return JsonDocument.Parse("{}");
            var reply = await P.StandardOutput.ReadLineAsync(ct) ?? throw new EndOfStreamException("shim closed stdout");
            Transcript.Add("<- " + reply);
            return JsonDocument.Parse(reply); // a non-JSON line on stdout throws here
        }
        public async Task Handshake(CancellationToken ct)
        {
            using var init = await Rpc("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe-client","version":"0"}}}""", ct);
            Assert.True(init.RootElement.TryGetProperty("result", out _));
            await Rpc("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", ct, expectReply: false);
        }
        public void Dispose() { try { if (!P.HasExited) P.Kill(true); } catch { } P.Dispose(); }
    }

    [Fact]
    public async Task StdoutContainsOnlyMcp()
    {
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe);
        using var shim = new Shim(pipe);
        var ct = Ct(60);
        await shim.Handshake(ct);

        using var list = await shim.Rpc("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", ct);
        var names = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "get_document_summary" }, names);

        var sw = Stopwatch.StartNew();
        using var call = await shim.Rpc("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_document_summary","arguments":{}}}""", ct);
        sw.Stop();
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var ie) && ie.GetBoolean());
        Assert.Equal(7, result.GetProperty("structuredContent").GetProperty("revision").GetInt32());

        // Every stdout line was valid JSON-RPC (Rpc would have thrown otherwise); diagnostics went to stderr.
        Assert.All(shim.Transcript.Where(l => l.StartsWith("<- ")), l => Assert.Contains("\"jsonrpc\":\"2.0\"", l));
        string err; lock (shim.Stderr) err = shim.Stderr.ToString();
        Assert.Contains("McpProbe starting", err);
        Evidence.Record("mcpToolCallMs", sw.Elapsed.TotalMilliseconds);
        Evidence.SaveTranscript(shim.Transcript);
    }

    [Fact]
    public async Task OfficialSdkClientListsAndCallsTool()
    {
        var pipe = NewPipe();
        await using var host = TestHost.Start(pipe);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "probe",
            Command = "dotnet",
            Arguments = [ShimDll()],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct(60));
        var tools = await client.ListToolsAsync(cancellationToken: Ct());
        Assert.Equal(new[] { "get_document_summary" }, tools.Select(t => t.Name).ToArray());
        var result = await client.CallToolAsync("get_document_summary", cancellationToken: Ct());
        Assert.True(result.IsError != true);
        Assert.Equal(7, result.StructuredContent!.Value.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task ShimReportsNotRunningAndRecovers()
    {
        var pipe = NewPipe();
        using var shim = new Shim(pipe); // no host yet
        var ct = Ct(60);
        await shim.Handshake(ct);
        const string call = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_document_summary","arguments":{}}}""";
        using var down = await shim.Rpc(call, ct);
        var r = down.RootElement.GetProperty("result");
        Assert.True(r.GetProperty("isError").GetBoolean());
        Assert.Equal("not_running", r.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());

        await using var host = TestHost.Start(pipe); // host appears later; the shim connects lazily
        using var up = await shim.Rpc(call.Replace("\"id\":9", "\"id\":10"), ct);
        Assert.Equal(7, up.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("revision").GetInt32());
    }
}

static class Evidence
{
    static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "m0", "evidence", "G3"));
    static readonly object Gate = new();
    public static void Record(string key, object value)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, "measurements.json");
            var obj = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            obj[key] = value is double d ? JsonValue.Create(Math.Round(d, 1)) : JsonValue.Create(value.ToString());
            File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    public static void SaveTranscript(IEnumerable<string> lines)
    {
        lock (Gate) { Directory.CreateDirectory(Dir); File.WriteAllLines(Path.Combine(Dir, "mcp-transcript.txt"), lines); }
    }
}
