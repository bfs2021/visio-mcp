using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace WebViewProbe;

public sealed record ProbeOptions(string DistDir, string UserDataDir);

public sealed record SessionInfo(string SessionId, string DocumentId);

public sealed record RequestEnvelope(string Method, string? SessionId, string? DocumentId, JsonObject Params)
{
    public string RequestId { get; } = Guid.NewGuid().ToString();
}

public sealed record ErrorBody(string Code, string Message, bool Retryable = false, JsonObject? Details = null);

public sealed record ResponseEnvelope(string RequestId, JsonObject? Result, ErrorBody? Error, bool Duplicate = false)
{
    public int? Revision => Result?["revision"]?.GetValue<int>();
}

/// <summary>The bridge accepts messages only from the exact packaged origin: not a prefix, not another port, scheme or user-info trick.</summary>
public static class OriginPolicy
{
    public const string Host = "app.agentic-diagram.invalid";
    public static readonly Uri Origin = new($"https://{Host}");

    public static bool IsAllowed(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase)
        && u.Port == 443
        && string.IsNullOrEmpty(u.UserInfo);
}

/// <summary>Host-served, content-addressed image bytes. URLs contain only the SHA-256, never a filesystem path.</summary>
public sealed class AssetStore
{
    readonly ConcurrentDictionary<string, (byte[] Bytes, string Mime)> items = new();

    public string Add(byte[] bytes, string mime)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        items[sha] = (bytes, mime);
        return sha;
    }

    public bool TryGet(string sha, out (byte[] Bytes, string Mime) item) => items.TryGetValue(sha, out item);
}
