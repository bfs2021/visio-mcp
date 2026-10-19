using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Ipc;

public static class Wire
{
    public const int ProtocolVersion = 1;
    public const int MaxClients = 8;
    public const int MaxPendingPerConnection = 16;
    public const int WriterQueue = 64;

    public static JsonDocument ToDocument(ResponseEnvelope response) => JsonSerializer.SerializeToDocument(response, ContractJson.Options);
    public static JsonDocument ToDocument(RequestEnvelope request) => JsonSerializer.SerializeToDocument(request, ContractJson.Options);

    public static ResponseEnvelope Error(string? id, string code, string message, bool retryable = false, string? outcome = null, JsonObject? details = null) =>
        new() { RequestId = id ?? "none", Error = new AppError { Code = code, Message = message, Retryable = retryable, Outcome = outcome, Details = details } };

    public static ResponseEnvelope Ok(string id, JsonNode result) => new() { RequestId = id, Result = JsonSerializer.SerializeToElement(result) };

    public static RequestEnvelope Hello(string clientLabel) => new()
    {
        RequestId = "hello", Method = "app.hello",
        Params = new JsonObject { ["protocolVersion"] = ProtocolVersion, ["clientLabel"] = clientLabel },
    };
}

/// <summary>Pipe name scoped by user SID, logon session and protocol version, so another user or session never shares it.</summary>
public static class PipeNames
{
    public static string For(int protocolVersion = Wire.ProtocolVersion)
    {
        using var id = WindowsIdentity.GetCurrent();
        return $"agentic-diagram.v{protocolVersion}.{id.User!.Value}.{LogonSessionId(id.AccessToken.DangerousGetHandle()):x}";
    }

    /// <summary>The pipe a second launch uses to reach the running window. Scoped like the MCP pipe: one per user and logon session.</summary>
    public static string ForLaunch() { using var id = WindowsIdentity.GetCurrent(); return ForLaunch(id.User!.Value, LogonSessionId(id.AccessToken.DangerousGetHandle())); }
    public static string ForLaunch(string sid, long logonSession) => $"agentic-diagram.launch.v1.{sid}.{logonSession:x}";

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
