using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Microsoft.Win32;

namespace Diagram.App;

/// <summary>Answers page-initiated host.* requests: native dialogs, file IO and approved image bytes. The page never sees a path it did not choose.</summary>
public sealed partial class HostServices(MainWindow window)
{
    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml",
    };

    readonly object pathGate = new();
    readonly Dictionary<string, string> paths = new();         // the file each open document came from or was last saved to
    readonly Dictionary<string, string> successors = new();    // old document id -> the one that replaced it, for a reply that arrives after the swap

    /// <summary>Listens for documents being closed or replaced so each one's file follows it. Called once the router exists.</summary>
    internal void Attach(BridgeRouter router)
    {
        router.DocumentReplaced += (old, next) => { lock (pathGate) { successors[old.DocumentId] = next.DocumentId; if (paths.Remove(old.DocumentId, out var p)) paths[next.DocumentId] = p; } };
        router.DocumentClosed += s => { lock (pathGate) paths.Remove(s.DocumentId); };
    }

    /// <summary>Path of the active document's last successful open or save (informational; the editor tracks the saved revision).</summary>
    public string? CurrentPath => window.Router.CurrentScope is { } s ? PathOf(s.DocumentId) : null;

    /// <summary>The file one open document came from or was saved to.</summary>
    public string? PathOf(string documentId) { lock (pathGate) return paths.TryGetValue(documentId, out var p) ? p : null; }

    /// <summary>Sets a document's file. If that document has been replaced already, the replacement gets it.</summary>
    internal void SetPath(string documentId, string? path)
    {
        lock (pathGate)
        {
            for (var i = 0; i < 8 && successors.TryGetValue(documentId, out var next); i++) documentId = next;
            if (path is null) paths.Remove(documentId); else paths[documentId] = path;
        }
    }

    internal void SetCurrentPath(string? path) { if (window.Router.CurrentScope is { } s) SetPath(s.DocumentId, path); }

    /// <summary>Test seam: replaces the native dialogs so automated tests can drive open, save and insert without UI.</summary>
    public Func<string, string?>? DialogOverride { get; set; }

    public async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        try
        {
            return request.Method switch
            {
                "host.ping" => Ok(request, new JsonObject { ["pong"] = true }),
                "host.openJson" => await OpenAsync(request, ct),
                "host.saveJson" => await SaveAsync(request, ct),
                "host.pickImage" => await PickImageAsync(request, ct),
                "host.openVsdx" => await OpenVsdxAsync(request, ct),
                "host.exportVsdx" => await ExportVsdxAsync(request, ct),
                var m when m.StartsWith("host.library.", StringComparison.Ordinal) => await LibraryAsync(request, ct),
                "host.app.close" => await window.ApproveCloseAsync(request.Params["discard"]?.GetValue<bool>() == true) ? Ok(request, new JsonObject { ["closing"] = true }) : Err(request, "invalid_request", "the window is not waiting to close"),
                "host.app.closeCancelled" => Ok(request, new JsonObject { ["cancelled"] = window.CancelClose() }),
                var m when m.StartsWith("host.recovery.", StringComparison.Ordinal) => await RecoveryAsync(request, ct),
                var m when m.StartsWith("host.workspace.", StringComparison.Ordinal) => await WorkspaceAsync(request, ct),
                "host.documents.show" => await window.Documents.ShowAsync(request, ct),
                "host.documents.discard" => await window.Documents.DiscardAsync(request, ct),
                "host.documents.reveal" => Shell(request, "reveal"),
                "host.documents.copyPath" => Shell(request, "copyPath"),
                "host.documents.agent" => Ok(request, new JsonObject { ["documents"] = await window.Documents.ListAsync(ct, agentOnly: true) }),
                _ => Err(request, "method_not_found", $"unknown host method {request.Method}"),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Err(request, "io_error", e.Message);
        }
    }

    static ResponseEnvelope Ok(RequestEnvelope r, JsonNode result) => new() { RequestId = r.RequestId, Result = JsonSerializer.SerializeToElement(result) };
    static ResponseEnvelope Err(RequestEnvelope r, string code, string message, bool retryable = false) =>
        new() { RequestId = r.RequestId, Error = new AppError { Code = code, Message = message, Retryable = retryable } };
    static ResponseEnvelope Err(RequestEnvelope r, AppError e) => new() { RequestId = r.RequestId, Error = e };

    /// <summary>Test seam: stands in for Explorer and the clipboard, receiving ("reveal" | "copyPath", path).</summary>
    public Action<string, string>? ShellOverride { get; set; }

    /// <summary>Shows a document's file in Explorer or copies its path. Only the file a document is actually saved to can be named, and only by that document's id.</summary>
    ResponseEnvelope Shell(RequestEnvelope request, string what)
    {
        var id = request.Params["documentId"]?.GetValue<string>();
        var path = id is null ? null : PathOf(id);
        if (path is null || window.Router.ChannelOf(id!) is null) return Err(request, "not_found", "that document has not been saved to a file");
        try
        {
            if (ShellOverride is { } o) o(what, path);
            else if (what == "copyPath") window.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(path));
            else window.Dispatcher.Invoke(() => ShellReveal.Reveal(path));
        }
        catch (Exception e) when (e is IOException or System.Runtime.InteropServices.ExternalException) { return Err(request, "io_error", e.Message); }
        return Ok(request, new JsonObject { [what == "copyPath" ? "copied" : "revealed"] = path });
    }

    string? Ask(string kind, Func<string?> dialog) => DialogOverride is { } o ? o(kind) : window.Dispatcher.Invoke(dialog);

    async Task<ResponseEnvelope> OpenAsync(RequestEnvelope request, CancellationToken ct)
    {
        string? path;
        if (request.Params["path"]?.GetValue<string>() is { Length: > 0 } requested)
        {
            // reopening a tab from the last run: no dialog, but only a file that run listed
            if (!MayReopen(requested)) return Err(request, "invalid_request", "that file was not open in the last session");
            path = Path.GetFullPath(requested);
        }
        else path = Ask("open", () =>
        {
            var d = new OpenFileDialog { Title = "Open diagram", Filter = "Diagram JSON (*.diagram.json)|*.diagram.json|All files (*.*)|*.*" };
            return d.ShowDialog(window) == true ? d.FileName : null;
        });
        if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });

        // Ask the editor where it is right now; the open must name that revision so it cannot discard later edits.
        var status = await window.Router.SendAsync(new RequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString(), Method = "doc.status", DocumentId = window.Router.CurrentScope?.DocumentId, SessionId = window.Router.CurrentScope?.SessionId, Params = new JsonObject(),
        }, ct);
        if (status.Error is { } se) return Err(request, se);
        var s = status.Result!.Value;
        var expected = new ExpectedState { DocumentId = s.GetProperty("documentId").GetString()!, SessionId = s.GetProperty("sessionId").GetString()!, BaseRevision = s.GetProperty("revision").GetInt32() };

        var opened = await window.Coordinator.OpenJsonAsync(path, expected, ct);
        if (!opened.IsOk) return Err(request, opened.Error!);
        SetPath(expected.DocumentId, path);
        // what was just read from the file is, by definition, what is on disk: the document is saved at this revision
        var now = opened.Value.Snapshot;
        await window.Router.MarkSavedAsync(new Scope { DocumentId = now.DocumentId, SessionId = now.SessionId }, now.Revision, path, ct);
        return Ok(request, new JsonObject { ["path"] = path, ["notes"] = new JsonArray(opened.Value.Notes.Select(n => (JsonNode)n).ToArray()) });
    }

    async Task<ResponseEnvelope> OpenVsdxAsync(RequestEnvelope request, CancellationToken ct)
    {
        var path = Ask("openVsdx", () =>
        {
            var d = new OpenFileDialog { Title = "Open Visio drawing", Filter = "Visio drawings (*.vsdx)|*.vsdx|All files (*.*)|*.*" };
            return d.ShowDialog(window) == true ? d.FileName : null;
        });
        if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
        var status = await window.Router.SendAsync(new RequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString(), Method = "doc.status", DocumentId = window.Router.CurrentScope?.DocumentId, SessionId = window.Router.CurrentScope?.SessionId, Params = new JsonObject(),
        }, ct);
        if (status.Error is { } se) return Err(request, se);
        var s = status.Result!.Value;
        var expected = new ExpectedState { DocumentId = s.GetProperty("documentId").GetString()!, SessionId = s.GetProperty("sessionId").GetString()!, BaseRevision = s.GetProperty("revision").GetInt32() };

        var opened = await window.Native.OpenAsync(path, expected, ct);
        if (!opened.IsOk) return Err(request, opened.Error!);
        SetPath(expected.DocumentId, null); // the JSON file the person had open is not this document's home any more: Save asks where to put it
        return Ok(request, new JsonObject { ["path"] = opened.Value.Path, ["summary"] = opened.Value.Report.Summary, ["diagnostics"] = opened.Value.Report.ToJson(), ["lossy"] = opened.Value.Report.IsLossy });
    }

    async Task<ResponseEnvelope> ExportVsdxAsync(RequestEnvelope request, CancellationToken ct)
    {
        var acceptLoss = request.Params["acceptLoss"]?.GetValue<bool>() ?? false;
        var path = request.Params["path"]?.GetValue<string>() ?? Ask("exportVsdx", () =>
        {
            var d = new SaveFileDialog { Title = "Export to Visio", Filter = "Visio drawings (*.vsdx)|*.vsdx", DefaultExt = ".vsdx", AddExtension = true };
            return d.ShowDialog(window) == true ? d.FileName : null;
        });
        if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
        var scope = window.Router.CurrentScope;
        if (scope is null) return Err(request, "not_ready", "the editor is not ready", retryable: true);
        var r = await window.Native.ExportAsync(path, scope, acceptLoss, ct);
        if (!r.IsOk)
        {
            // a refusal because something would be lost carries the chosen path, so the person can agree without choosing it again
            var e = r.Error!;
            if (e.Code == "compatibility_loss") { var d = (JsonObject)e.Details!.DeepClone(); d["path"] = path; e = e with { Details = d }; }
            return Err(request, e);
        }
        return Ok(request, new JsonObject { ["path"] = r.Value.Path, ["revision"] = r.Value.Revision, ["bytes"] = r.Value.Bytes, ["summary"] = r.Value.Report.Summary, ["diagnostics"] = r.Value.Report.ToJson(), ["lossy"] = r.Value.Report.IsLossy });
    }

    async Task<ResponseEnvelope> SaveAsync(RequestEnvelope request, CancellationToken ct)
    {
        var saveAs = request.Params["saveAs"]?.GetValue<bool>() ?? false;
        var target = window.Router.CurrentScope;
        var known = target is null ? null : PathOf(target.DocumentId);
        var path = saveAs || known is null
            ? Ask("save", () =>
            {
                var d = new SaveFileDialog { Title = "Save diagram", Filter = "Diagram JSON (*.diagram.json)|*.diagram.json", DefaultExt = ".diagram.json", AddExtension = true };
                return d.ShowDialog(window) == true ? d.FileName : null;
            })
            : known;
        if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
        var scope = target;
        if (scope is null) return Err(request, "not_ready", "the editor is not ready", retryable: true);

        var saved = await window.Coordinator.SaveJsonAsync(path, scope, ct);
        if (!saved.IsOk) return Err(request, saved.Error!);
        SetPath(scope.DocumentId, saved.Value.Path);
        await window.RecoveryMgr.OnSavedAsync(scope.SessionId, saved.Value.Revision, saved.Value.Path); // work saved up to here needs no recovery
        return Ok(request, new JsonObject { ["path"] = saved.Value.Path, ["revision"] = saved.Value.Revision });
    }

    async Task<ResponseEnvelope> PickImageAsync(RequestEnvelope request, CancellationToken ct)
    {
        var path = Ask("image", () =>
        {
            var d = new OpenFileDialog { Title = "Insert picture", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.svg)|*.png;*.jpg;*.jpeg;*.bmp;*.svg" };
            return d.ShowDialog(window) == true ? d.FileName : null;
        });
        if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
        var safe = PathPolicy.Validate(path);
        if (!safe.IsOk) return Err(request, safe.Error!);
        if (!ImageTypes.TryGetValue(Path.GetExtension(safe.Value), out var mime)) return Err(request, "invalid_request", "that file type is not a supported picture");

        await using var stream = File.OpenRead(safe.Value);
        var prepared = await window.Preparer.PrepareAsync(stream, mime, Path.GetFileName(safe.Value), ct);
        if (!prepared.IsOk) return Err(request, prepared.Error!);
        return Ok(request, new JsonObject
        {
            ["ref"] = JsonSerializer.SerializeToNode(prepared.Value.Ref, ContractJson.Options),
            ["notes"] = new JsonArray(prepared.Value.Notes.Select(n => (JsonNode)n).ToArray()),
            ["width"] = prepared.Value.Width,
            ["height"] = prepared.Value.Height,
        });
    }
}
