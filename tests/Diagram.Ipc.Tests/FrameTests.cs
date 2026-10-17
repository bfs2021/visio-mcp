using System.Text;
using System.Text.Json;
using Diagram.Ipc;
using Xunit;

public class FrameTests
{
    static CancellationToken Ct(int seconds = 20) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

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
    public async Task PartialFrames()
    {
        using var doc = JsonDocument.Parse("""{"protocolVersion":1,"kind":"request","requestId":"r1","method":"app.hello","params":{"text":"héllo ✓"}}""");
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, doc, Ct());
        var bytes = ms.ToArray();
        Assert.Equal((uint)(bytes.Length - 4), BitConverter.ToUInt32(bytes, 0)); // 4-byte little-endian length prefix

        using var back = await FrameCodec.ReadAsync(new Dribble(bytes), Ct());
        Assert.Equal(doc.RootElement.GetRawText(), back.RootElement.GetRawText());
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(new Dribble(bytes[..^3]), Ct())); // a stream that ends mid-frame is reported
    }

    [Fact]
    public async Task ThirtyTwoMiBCap()
    {
        var header = BitConverter.GetBytes((uint)(FrameCodec.MaxFrameBytes + 1));
        var before = FrameCodec.PayloadAllocations;
        var ex = await Assert.ThrowsAsync<FrameException>(() => FrameCodec.ReadAsync(new MemoryStream(header), Ct()));
        Assert.Equal("limit_exceeded", ex.Code);
        Assert.Equal(0, FrameCodec.PayloadAllocations - before); // nothing was allocated for the oversize payload

        var atLimit = BitConverter.GetBytes((uint)FrameCodec.MaxFrameBytes);
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(new MemoryStream(atLimit), Ct())); // exactly the limit starts, then fails as truncated
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

    [Fact]
    public async Task OutgoingFramesOverTheCapAreRefused()
    {
        var big = new string('a', FrameCodec.MaxFrameBytes);
        using var doc = JsonDocument.Parse($"{{\"x\":\"{big}\"}}");
        await Assert.ThrowsAsync<FrameException>(() => FrameCodec.WriteAsync(new MemoryStream(), doc, Ct(60)));
    }
}
