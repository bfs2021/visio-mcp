using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diagram.Core.Contracts;

/// <summary>Preferences-only evaluator for the embedded canonical schema; not a general JSON Schema engine.</summary>
public static class PreferencesValidation
{
    // ECMAScript String.trim whitespace, shared with the renderer (NEL is deliberately retained).
    static readonly char[] TrimCharacters = "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();
    static readonly JsonNode Schema = LoadSchema();
    static JsonNode LoadSchema()
    {
        using var stream = typeof(PreferencesValidation).Assembly.GetManifestResourceStream("Diagram.Core.Contracts.preferences.schema.json")!;
        return JsonNode.Parse(stream)!;
    }
    public sealed record ValidationResult(AppPreferences? Value, AppError? Error);
    public static AppPreferences DefaultPreferences() => Schema["$defs"]!["AppPreferences"]!["default"]!.Deserialize<AppPreferences>(ContractJson.Options)!;
    public static CapturedDocumentDefaults CaptureDefaults(AppPreferences p) => new() { Grid = p.Grid with { }, Font = p.Font with { } };
    public static ValidationResult ValidatePreferences(JsonNode? value)
    {
        var normalized = value?.DeepClone();
        if (normalized is JsonObject root && root["font"] is JsonObject font && font["family"] is JsonValue family && family.TryGetValue<string>(out var name)) font["family"] = name.Trim(TrimCharacters);
        var problem = Validate(normalized, Schema["$defs"]!["AppPreferences"]!, "");
        if (problem is not null) return new(null, new AppError { Code = "invalid_request", Message = problem, Retryable = false });
        // JSON Schema integers include 10.0/1e1; System.Text.Json's int reader requires an integer token.
        normalized!["schemaVersion"] = Schema["$defs"]!["AppPreferences"]!["properties"]!["schemaVersion"]!["const"]!.DeepClone();
        var seconds = normalized["recovery"]!["checkpointSeconds"]!;
        normalized["recovery"]!["checkpointSeconds"] = (int)double.Parse(seconds.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        return new(normalized.Deserialize<AppPreferences>(ContractJson.Options), null);
    }
    static string? Validate(JsonNode? value, JsonNode definition, string path)
    {
        foreach (var (keyword, _) in definition.AsObject())
            if (keyword is not ("$ref" or "type" or "additionalProperties" or "required" or "properties" or "default" or "const" or "enum" or "minimum" or "maximum" or "minLength" or "maxLength"))
                throw new InvalidOperationException($"Unsupported preferences schema keyword: {keyword}");
        if (definition["$ref"] is JsonNode reference) {
            var parts = reference.GetValue<string>().Split('/');
            if (parts.Length != 3 || parts[0] != "#" || parts[1] != "$defs") throw new InvalidOperationException("Unsupported preferences schema reference");
            return Validate(value, Schema["$defs"]![parts[2]]!, path);
        }
        string Invalid() => $"{path}: invalid preferences value";
        if (definition["const"] is JsonNode constant && !JsonNode.DeepEquals(value, constant)) return Invalid();
        if (definition["enum"] is JsonArray options && !options.Any(option => JsonNode.DeepEquals(value, option))) return Invalid();
        switch (definition["type"]?.GetValue<string>()) {
            case "object":
                if (value is not JsonObject obj) return Invalid();
                var properties = definition["properties"]!.AsObject();
                foreach (var required in definition["required"]!.AsArray()) if (!obj.ContainsKey(required!.GetValue<string>())) return Invalid();
                foreach (var (key, child) in obj) {
                    if (!properties.TryGetPropertyValue(key, out var childSchema)) return Invalid();
                    var error = Validate(child, childSchema!, path + "/" + key); if (error is not null) return error;
                }
                break;
            case "number": case "integer":
                if (value is not JsonValue number || !number.TryGetValue<double>(out var n)) {
                    // JsonValue<int> is also used by typed host callers.
                    if (value is not JsonValue integer || !integer.TryGetValue<int>(out var i)) return Invalid();
                    n = i;
                }
                if (!double.IsFinite(n) || (definition["type"]!.GetValue<string>() == "integer" && n != Math.Truncate(n))) return Invalid();
                if (n < definition["minimum"]!.GetValue<double>() || n > definition["maximum"]!.GetValue<double>()) return Invalid();
                break;
            case "string":
                if (value is not JsonValue text || !text.TryGetValue<string>(out var s)) return Invalid();
                var length = s.EnumerateRunes().Count();
                if (length < definition["minLength"]!.GetValue<int>() || length > definition["maxLength"]!.GetValue<int>()) return Invalid();
                break;
            case "boolean": if (value is not JsonValue flag || !flag.TryGetValue<bool>(out _)) return Invalid(); break;
            case null: break;
            default: throw new InvalidOperationException("Unsupported preferences schema type");
        }
        return null;
    }
}

