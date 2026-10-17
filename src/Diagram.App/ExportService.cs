using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Visio;

namespace Diagram.App;

/// <summary>
/// export_document: writes vsdx, svg, png or jpeg for an outside client. A client never names a place on the disk freely: files go to the
/// folder of the open document or to the application's export folder, so a path from an agent cannot reach anywhere else.
/// </summary>
public sealed class ExportService(MainWindow window, string exportRoot)
{
    static readonly Dictionary<string, string[]> Extensions = new() { ["vsdx"] = [".vsdx"], ["svg"] = [".svg"], ["png"] = [".png"], ["jpeg"] = [".jpg", ".jpeg"] };

    public string ExportRoot { get; } = Path.GetFullPath(exportRoot);

    /// <summary>The folders a client may export into: the export folder, and the folder the open document came from.</summary>
    public IReadOnlyList<string> ApprovedRoots()
    {
        var roots = new List<string> { ExportRoot };
        if (window.Services?.CurrentPath is { } p && Path.GetDirectoryName(p) is { Length: > 0 } dir) roots.Add(Path.GetFullPath(dir));
        return roots;
    }

    /// <summary>Resolves a requested path (or makes a default one) and refuses anything outside the approved folders.</summary>
    public Result<string> ResolvePath(string? requested, string format, string title, int revision)
    {
        if (!Extensions.TryGetValue(format, out var exts)) return Result<string>.Fail("invalid_request", "format must be vsdx, svg, png or jpeg");
        var name = requested is null
            ? $"{Slug(title)}-r{revision}{exts[0]}"
            : requested;
        string combined;
        try { combined = Path.IsPathRooted(name) ? name : Path.Combine(ExportRoot, name); }
        catch (ArgumentException) { return Result<string>.Fail("path_not_permitted", "the path is not valid"); }
        var safe = PathPolicy.Validate(combined);
        if (!safe.IsOk) return safe;
        var full = safe.Value;
        if (!exts.Contains(Path.GetExtension(full).ToLowerInvariant())) return Result<string>.Fail("invalid_request", $"a {format} export must end in {string.Join(" or ", exts)}");
        var inside = ApprovedRoots().Any(root => full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!inside) return Result<string>.Fail("path_not_permitted", "exports go to the open document's folder or the application's export folder only", details: new JsonObject { ["exportFolder"] = ExportRoot });
        // a link inside the folder must not lead out of it
        var dir = Path.GetDirectoryName(full)!;
        if (Directory.Exists(dir) && new DirectoryInfo(dir).ResolveLinkTarget(true) is { } target && !ApprovedRoots().Any(r => target.FullName.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            return Result<string>.Fail("path_not_permitted", "that folder is a link that leads outside the approved folders");
        return Result<string>.Ok(full);
    }

    static string Slug(string s)
    {
        var t = new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (t.Contains("--")) t = t.Replace("--", "-");
        return t.Length == 0 ? "diagram" : t.Length > 60 ? t[..60] : t;
    }

    public async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        ResponseEnvelope Err(string code, string message, JsonObject? details = null, bool retryable = false) =>
            new() { RequestId = request.RequestId, Error = new AppError { Code = code, Message = message, Retryable = retryable, Details = details } };
        ResponseEnvelope Err2(AppError e) => new() { RequestId = request.RequestId, Error = e };

        var scope = window.Router.CurrentScope;
        if (scope is null) return Err("not_ready", "the editor has not signalled ready", retryable: true);
        if (request.DocumentId != scope.DocumentId) return Err("document_mismatch", "the document is not the open one");
        if (request.SessionId != scope.SessionId) return Err("session_mismatch", "the session is not the current one");
        var format = request.Params["format"]?.GetValue<string>() ?? "";
        var requested = request.Params["path"]?.GetValue<string>();
        var pageId = request.Params["pageId"]?.GetValue<string>();

        // the snapshot barrier gives the title and revision this export will be labelled with
        var snap = await window.Router.SnapshotAsync(scope, ct);
        if (!snap.IsOk) return Err2(snap.Error!);
        var path = ResolvePath(requested, format, snap.Value.Document.Title, snap.Value.Revision);
        if (!path.IsOk) return Err2(path.Error!);
        Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);

        if (format == "vsdx")
        {
            var r = await window.Native.ExportAsync(path.Value, scope, acceptLoss: true, ct);
            if (!r.IsOk) return Err2(r.Error!);
            return Ok(request, new JsonObject
            {
                ["path"] = r.Value.Path, ["format"] = "vsdx", ["revision"] = r.Value.Revision, ["bytes"] = r.Value.Bytes, ["lossy"] = r.Value.Report.IsLossy,
                ["summary"] = r.Value.Report.Summary, ["diagnostics"] = r.Value.Report.ToJson(),
            });
        }

        var p = new JsonObject { ["scope"] = "page", ["format"] = format, ["maxWidth"] = format == "svg" ? 4096 : 3200, ["maxHeight"] = format == "svg" ? 4096 : 2400 };
        if (pageId is not null) p["pageId"] = pageId;
        var rendered = await window.Router.RenderAsync(scope, p, ct);
        if (!rendered.IsOk) return Err2(rendered.Error!);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(rendered.Value.GetProperty("base64").GetString()!); }
        catch (Exception e) when (e is FormatException or KeyNotFoundException or InvalidOperationException) { return Err("internal_error", "the editor returned an unreadable picture"); }
        try { await window.Writer.WriteAsync(path.Value, bytes, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Err("io_error", "the file could not be written: " + e.Message, retryable: true); }
        return Ok(request, new JsonObject
        {
            ["path"] = path.Value, ["format"] = format, ["revision"] = rendered.Value.GetProperty("revision").GetInt32(), ["bytes"] = bytes.Length, ["lossy"] = false,
            ["widthPx"] = rendered.Value.GetProperty("widthPx").GetInt32(), ["heightPx"] = rendered.Value.GetProperty("heightPx").GetInt32(),
        });
    }

    static ResponseEnvelope Ok(RequestEnvelope r, JsonNode result) => new() { RequestId = r.RequestId, Result = JsonSerializer.SerializeToElement(result) };
}
