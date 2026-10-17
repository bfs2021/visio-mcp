using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace IpcProbe;

/// <summary>A host or transport failure with a stable code from the design's error list.</summary>
public sealed class HostException(string code, string message, bool retryable = false, string? outcome = null) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    /// <summary>"unknown" when the request may have been delivered before the failure.</summary>
    public string? Outcome { get; } = outcome;
}

public static class Envelope
{
    public const int ProtocolVersion = 1;

    public static JsonDocument Request(string method, string id, object? @params = null) =>
        JsonSerializer.SerializeToDocument(new { protocolVersion = ProtocolVersion, kind = "request", requestId = id, method, @params = @params ?? new { } });

    public static JsonDocument Result(string? id, object result) =>
        JsonSerializer.SerializeToDocument(new { protocolVersion = ProtocolVersion, kind = "response", requestId = id, result });

    public static JsonDocument Error(string? id, string code, string message, bool retryable = false) =>
        JsonSerializer.SerializeToDocument(new { protocolVersion = ProtocolVersion, kind = "response", requestId = id, error = new { code, message, retryable } });
}

/// <summary>Pipe name scoped by user SID, logon session and protocol version.</summary>
public static class PipeNames
{
    public static string For(int protocolVersion)
    {
        using var id = WindowsIdentity.GetCurrent();
        return $"agentic-diagram.v{protocolVersion}.{id.User!.Value}.{LogonSessionId(id.AccessToken.DangerousGetHandle()):x}";
    }

    public static long LogonSessionId() { using var id = WindowsIdentity.GetCurrent(); return LogonSessionId(id.AccessToken.DangerousGetHandle()); }

    /// <summary>AuthenticationId (the logon session LUID) from TOKEN_STATISTICS.</summary>
    public static long LogonSessionId(IntPtr token)
    {
        const int TokenStatistics = 10;
        var buf = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetTokenInformation(token, TokenStatistics, buf, 256, out _)) throw new System.ComponentModel.Win32Exception();
            var low = (uint)Marshal.ReadInt32(buf, 8);
            var high = Marshal.ReadInt32(buf, 12);
            return ((long)high << 32) | low;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
}
