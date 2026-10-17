using System.Buffers.Binary;
using System.Text.Json;

namespace IpcProbe;

public sealed class FrameException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>4-byte little-endian length, then that many bytes of UTF-8 JSON. The declared size is validated before any payload allocation.</summary>
public static class FrameCodec
{
    public const int MaxFrameBytes = 32 * 1024 * 1024;

    static long payloadAllocations;
    /// <summary>Number of payload buffers allocated by <see cref="ReadAsync"/>; lets tests prove an oversized frame allocates nothing.</summary>
    public static long PayloadAllocations => Interlocked.Read(ref payloadAllocations);

    public static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken ct, int maxBytes = MaxFrameBytes)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0) throw new FrameException("invalid_request", "empty frame");
        if (length > (uint)maxBytes) throw new FrameException("limit_exceeded", $"frame of {length} bytes exceeds the {maxBytes} byte limit");

        Interlocked.Increment(ref payloadAllocations);
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, ct);
        try { return JsonDocument.Parse(payload); }
        catch (JsonException e) { throw new FrameException("invalid_request", "frame is not valid JSON: " + e.Message); }
    }

    /// <summary>Writes header and payload in one call so concurrent writers (guarded by the caller) cannot interleave partial frames.</summary>
    public static async Task WriteAsync(Stream stream, JsonDocument document, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        buffer.Write(new byte[4]);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) document.WriteTo(writer);
        var bytes = buffer.GetBuffer();
        var length = (int)buffer.Length - 4;
        if (length > MaxFrameBytes) throw new FrameException("limit_exceeded", $"outgoing frame of {length} bytes exceeds the limit");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)length);
        await stream.WriteAsync(bytes.AsMemory(0, length + 4), ct);
        await stream.FlushAsync(ct);
    }

    static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException($"stream ended after {read} of {buffer.Length} bytes");
            read += n;
        }
    }
}
