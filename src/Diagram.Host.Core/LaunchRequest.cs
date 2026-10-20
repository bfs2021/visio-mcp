namespace Diagram.Host.Core;

/// <summary>A file the shell asked the app to open. <c>Kind</c> is "json" (a diagram) or "vsdx" (a Visio drawing, opened through import).</summary>
public sealed record LaunchFile(string Path, string Kind);

/// <summary>
/// What a launch asked for: files to open, or a one-off command. Parsed in the process the person started (so relative paths mean what they
/// typed) and checked again by the running window, which does not trust a forwarded list.
/// </summary>
public sealed record LaunchRequest(IReadOnlyList<LaunchFile> Files, IReadOnlyList<string> Skipped, string? Command)
{
    public const int MaxFiles = 20;
    public const string Register = "register-file-types", Unregister = "unregister-file-types";

    public static LaunchRequest Parse(IEnumerable<string> args)
    {
        var files = new List<LaunchFile>(); var skipped = new List<string>(); string? command = null;
        foreach (var raw in args)
        {
            if (raw == "--" + Register) command = Register;
            else if (raw == "--" + Unregister) command = Unregister;
            else if (raw.StartsWith("--", StringComparison.Ordinal)) skipped.Add($"{raw}: not an option this app knows");
            else Add(raw, relativeOk: true, files, skipped);
        }
        return new LaunchRequest(files, skipped, command);
    }

    /// <summary>The same checks for paths another process forwarded: they must already be full paths.</summary>
    public static LaunchRequest FromForwarded(IEnumerable<string> paths)
    {
        var files = new List<LaunchFile>(); var skipped = new List<string>();
        foreach (var p in paths) Add(p, relativeOk: false, files, skipped);
        return new LaunchRequest(files, skipped, null);
    }

    static void Add(string raw, bool relativeOk, List<LaunchFile> files, List<string> skipped)
    {
        if (files.Count >= MaxFiles) { skipped.Add($"{raw}: more than {MaxFiles} files at once"); return; }
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\0')) { skipped.Add("an empty or invalid path"); return; }
            if (!relativeOk && !Path.IsPathFullyQualified(raw)) { skipped.Add($"{raw}: not a full path"); return; }
            full = Path.GetFullPath(raw);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { skipped.Add($"{raw}: not a usable path"); return; }
        var kind = full.EndsWith(".vsdx", StringComparison.OrdinalIgnoreCase) ? "vsdx" : full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : null;
        if (kind is null) { skipped.Add($"{Path.GetFileName(full)}: only .diagram.json and .vsdx files can be opened"); return; }
        if (!File.Exists(full)) { skipped.Add($"{Path.GetFileName(full)}: the file does not exist"); return; }
        if (files.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) return;
        files.Add(new LaunchFile(full, kind));
    }
}
