using System.Text;
using System.Text.Json;

namespace Diagram.Host.Core;

/// <summary>Proposed POC limits from design D7. Conservative defaults, not claims of tested maxima.</summary>
public static class InputLimits
{
    public const long NativeJsonBytes = 64L * 1024 * 1024;
    public const int NativeJsonDepth = 128;
    public const int OrdinaryStringBytes = 1024 * 1024;

    public const long RasterBytes = 32L * 1024 * 1024;
    public const int RasterSide = 16_384;
    public const long RasterPixels = 64L * 1024 * 1024;

    public const long SvgBytes = 5L * 1024 * 1024;
    public const int SvgElements = 50_000;
    public const int SvgDepth = 256;
}

/// <summary>
/// Walks the raw JSON tokens and enforces size, depth and string budgets before any tree is materialised.
/// Strings under the top-level "blobs" object are base64 asset payloads and get the raster budget instead.
/// </summary>
public static class JsonBudget
{
    public static Result<Unit> Check(ReadOnlySpan<byte> json)
    {
        if (json.Length > InputLimits.NativeJsonBytes)
            return Result<Unit>.Fail("limit_exceeded", $"file is {json.Length} bytes; the limit is {InputLimits.NativeJsonBytes}");
        var blobStringLimit = (int)Math.Min(int.MaxValue, InputLimits.RasterBytes * 4 / 3 + 16);
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = InputLimits.NativeJsonDepth });
            string? topKey = null;
            var depth = 0;
            string? pendingTop = null;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        if (depth == 2) topKey = pendingTop;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                        if (depth == 1) pendingTop = reader.GetString();
                        break;
                    case JsonTokenType.String:
                        {
                            var inBlobs = depth == 2 && topKey == "blobs";
                            var limit = inBlobs ? blobStringLimit : InputLimits.OrdinaryStringBytes;
                            if (reader.HasValueSequence ? reader.ValueSequence.Length > limit : reader.ValueSpan.Length > limit)
                                return Result<Unit>.Fail("limit_exceeded", $"a string value is longer than {limit} bytes");
                            break;
                        }
                }
            }
            return Result<Unit>.Ok(default);
        }
        catch (JsonException e)
        {
            // Exceeding MaxDepth also surfaces here.
            var depthIssue = e.Message.Contains("depth", StringComparison.OrdinalIgnoreCase);
            return Result<Unit>.Fail(depthIssue ? "limit_exceeded" : "invalid_request", depthIssue ? $"JSON nesting is deeper than {InputLimits.NativeJsonDepth}" : "the file is not valid JSON");
        }
    }
}

/// <summary>Path rules for anything the user or an agent can name: absolute drive paths only, no UNC, device, stream or reserved names.</summary>
public static class PathPolicy
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static Result<string> Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return Result<string>.Fail("path_not_permitted", "the path is empty or contains a null character");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return Result<string>.Fail("path_not_permitted", "UNC and device paths are not allowed");
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return Result<string>.Fail("path_not_permitted", "the path is not valid"); }
        if (!Path.IsPathRooted(path)) return Result<string>.Fail("path_not_permitted", "the path must be absolute");
        if (full.IndexOf(':', Math.Min(2, full.Length)) >= 0) return Result<string>.Fail("path_not_permitted", "alternate data streams are not allowed");
        foreach (var segment in full.Split('\\', '/'))
        {
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (stem.Length > 0 && Reserved.Contains(stem)) return Result<string>.Fail("path_not_permitted", $"\"{segment}\" is a reserved device name");
        }
        return Result<string>.Ok(full);
    }
}
