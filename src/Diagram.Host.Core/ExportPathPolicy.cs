using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

/// <summary>What the person is asked when a client wants to write somewhere it was not pre-approved to.</summary>
public sealed record ConsentRequest(string Client, string Path, string Action, string Reason);

/// <summary>The window that shows the choice. Anything but a clear yes (including no answer in time) means no.</summary>
public interface IExportConsent
{
    Task<bool> AskAsync(ConsentRequest request, CancellationToken ct);
}

/// <summary>Default for headless use and tests: nobody can say yes.</summary>
public sealed class DenyAllConsent : IExportConsent
{
    public Task<bool> AskAsync(ConsentRequest request, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>A destination that passed the rules. Revalidate it immediately before writing: the folder can change between approval and write.</summary>
public sealed record ApprovedDestination(string Path, bool Existed, bool Consented, string SessionId, int Revision);

/// <summary>
/// Where a client's export may go. By default: the open document's folder or the application's export folder, with a safe, unique,
/// generated name. Anything else needs the person's explicit choice, and some paths (network shares, devices, alternate data streams,
/// reserved names, links that lead elsewhere) are never allowed, even with consent.
/// </summary>
public sealed class ExportPathPolicy
{
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(60);
    static readonly Dictionary<string, string[]> Extensions = new() { ["vsdx"] = [".vsdx"], ["svg"] = [".svg"], ["png"] = [".png"], ["jpeg"] = [".jpg", ".jpeg"], ["pdf"] = [".pdf"], ["zip"] = [".zip"] };
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    readonly string exportRoot;
    readonly Func<IReadOnlyList<string>> savedDirectories;
    readonly Func<Scope?> currentScope;
    readonly IExportConsent consent;
    readonly TimeSpan timeout;

    public ExportPathPolicy(string exportRoot, Func<IReadOnlyList<string>> savedDirectories, Func<Scope?> currentScope, IExportConsent? consent = null, TimeSpan? consentTimeout = null)
    {
        this.exportRoot = System.IO.Path.GetFullPath(exportRoot);
        this.savedDirectories = savedDirectories;
        this.currentScope = currentScope;
        this.consent = consent ?? new DenyAllConsent();
        timeout = consentTimeout ?? ConsentTimeout;
    }

    public string ExportRoot => exportRoot;

    public IReadOnlyList<string> ApprovedRoots() => [exportRoot, .. savedDirectories().Where(d => d.Length > 0).Select(System.IO.Path.GetFullPath)];

    static string Slug(string s)
    {
        var t = new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (t.Contains("--")) t = t.Replace("--", "-");
        return t.Length == 0 ? "diagram" : t.Length > 60 ? t[..60] : t;
    }

    /// <summary>Rules that no consent can override.</summary>
    internal static Result<string> Canonical(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\0')) return Result<string>.Fail("path_not_permitted", "the path is empty or has an invalid character");
        var unified = raw.Replace('/', '\\');
        if (unified.StartsWith(@"\\", StringComparison.Ordinal)) return Result<string>.Fail("path_not_permitted", "network shares, device paths and extended paths are not allowed");
        var rest = unified.Length >= 2 && unified[1] == ':' ? unified[2..] : unified;
        if (rest.Contains(':')) return Result<string>.Fail("path_not_permitted", "alternate data streams are not allowed");
        string full;
        try { full = System.IO.Path.GetFullPath(raw); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return Result<string>.Fail("path_not_permitted", "the path is not valid"); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return Result<string>.Fail("path_not_permitted", "network shares and device paths are not allowed");
        foreach (var part in full[System.IO.Path.GetPathRoot(full)!.Length..].Split('\\', '/', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem)) return Result<string>.Fail("path_not_permitted", $"{part} is a reserved device name");
            if (part != part.TrimEnd('.', ' ')) return Result<string>.Fail("path_not_permitted", $"{part} ends with a dot or a space");
        }
        return Result<string>.Ok(full);
    }

    static bool Inside(string full, string root) => full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For a path inside an approved folder: no folder between that approved folder and the file may be a link that leads elsewhere.
    /// (A path outside the approved folders is the person's explicit choice, shown to them in full, so links are not second-guessed there.)
    /// </summary>
    static Result<Unit> NoLinkEscape(string full, IReadOnlyList<string> roots, bool inside)
    {
        if (!inside) return Result<Unit>.Ok(default);
        var root = roots.First(r => Inside(full, r)).TrimEnd('\\');
        var walk = System.IO.Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(walk) && !string.Equals(walk.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase))
        {
            var info = new System.IO.DirectoryInfo(walk);
            if (info.Exists && (info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                var target = info.ResolveLinkTarget(true)?.FullName;
                if (target is null || !roots.Any(r => string.Equals(target.TrimEnd('\\'), r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || Inside(target.TrimEnd('\\') + "\\x", r)))
                    return Result<Unit>.Fail("path_not_permitted", "a folder on the path is a link that leads outside the approved folders");
            }
            walk = System.IO.Path.GetDirectoryName(walk);
        }
        return Result<Unit>.Ok(default);
    }

    public async Task<Result<ApprovedDestination>> ResolveAsync(string? requested, string format, string title, Scope scope, int revision, string clientLabel, CancellationToken ct)
    {
        if (!Extensions.TryGetValue(format, out var exts)) return Result<ApprovedDestination>.Fail("invalid_request", "format must be vsdx, svg, png or jpeg");
        if (requested is not null && string.IsNullOrWhiteSpace(requested)) return Result<ApprovedDestination>.Fail("path_not_permitted", "the path is empty");
        var roots = ApprovedRoots();
        string full;
        if (requested is null)
        {
            // a generated name in the export folder, never replacing something that is already there
            var stem = $"{Slug(title)}-r{revision}";
            var candidate = System.IO.Path.Combine(exportRoot, stem + exts[0]);
            for (var n = 2; System.IO.File.Exists(candidate) && n < 1000; n++) candidate = System.IO.Path.Combine(exportRoot, $"{stem}-{n}{exts[0]}");
            var safe = Canonical(candidate);
            if (!safe.IsOk) return Result<ApprovedDestination>.Fail(safe.Error!);
            full = safe.Value;
        }
        else
        {
            var combined = System.IO.Path.IsPathRooted(requested) ? requested : System.IO.Path.Combine(exportRoot, requested);
            var safe = Canonical(combined);
            if (!safe.IsOk) return Result<ApprovedDestination>.Fail(safe.Error!);
            full = safe.Value;
        }
        if (!exts.Contains(System.IO.Path.GetExtension(full).ToLowerInvariant(), StringComparer.Ordinal))
            return Result<ApprovedDestination>.Fail("invalid_request", $"a {format} export must end in {string.Join(" or ", exts)}");

        var inside = roots.Any(r => Inside(full, r));
        var links = NoLinkEscape(full, roots, inside);
        if (!links.IsOk) return Result<ApprovedDestination>.Fail(links.Error!);
        var exists = System.IO.File.Exists(full);
        if (System.IO.Directory.Exists(full)) return Result<ApprovedDestination>.Fail("path_not_permitted", "that path is a folder");

        var asks = new List<string>();
        if (!inside) asks.Add("write outside the approved folders");
        if (exists) asks.Add("replace an existing file");
        var consented = false;
        if (asks.Count > 0)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            bool yes;
            try { yes = await consent.AskAsync(new ConsentRequest(clientLabel, full, string.Join(" and ", asks), $"The {clientLabel} asked to export the document to this file."), limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { yes = false; }
            if (!yes)
                return Result<ApprovedDestination>.Fail("path_not_permitted", inside ? "replacing that file was not approved" : "exports go to the open document's folder or the application's export folder unless you approve a different place",
                    details: new JsonObject { ["exportFolder"] = exportRoot, ["asked"] = true });
            // the editor may have been replaced while the question was open: an answer about one document is not an answer about another
            if (currentScope() is not { } now || now.SessionId != scope.SessionId || now.DocumentId != scope.DocumentId)
                return Result<ApprovedDestination>.Fail("session_mismatch", "the document changed while waiting for your answer, so nothing was written", retryable: true);
            consented = true;
        }
        return Result<ApprovedDestination>.Ok(new ApprovedDestination(full, exists, consented, scope.SessionId, revision));
    }

    /// <summary>Checks the destination again right before the write: the same rules, the same session, and no surprise file where there was none.</summary>
    public Result<ApprovedDestination> Revalidate(ApprovedDestination approved, Scope scope)
    {
        if (currentScope() is not { } now || now.SessionId != approved.SessionId || now.SessionId != scope.SessionId)
            return Result<ApprovedDestination>.Fail("session_mismatch", "the document changed since the destination was approved, so nothing was written", retryable: true);
        var safe = Canonical(approved.Path);
        if (!safe.IsOk) return Result<ApprovedDestination>.Fail(safe.Error!);
        var roots = ApprovedRoots();
        var inside = roots.Any(r => Inside(safe.Value, r));
        var links = NoLinkEscape(safe.Value, roots, inside);
        if (!links.IsOk) return Result<ApprovedDestination>.Fail(links.Error!);
        if (!inside && !approved.Consented) return Result<ApprovedDestination>.Fail("path_not_permitted", "that place is no longer an approved folder");
        var exists = System.IO.File.Exists(safe.Value);
        if (exists && !approved.Existed) return Result<ApprovedDestination>.Fail("destination_changed", "a file appeared at that name after it was chosen, so nothing was written", retryable: true);
        if (System.IO.Directory.Exists(safe.Value)) return Result<ApprovedDestination>.Fail("path_not_permitted", "that path is a folder");
        return Result<ApprovedDestination>.Ok(approved);
    }
}
