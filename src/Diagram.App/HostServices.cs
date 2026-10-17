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

    /// <summary>Path of the last successful open or save (informational; the editor tracks the saved revision).</summary>
    public string? CurrentPath { get; private set; }

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

    string? Ask(string kind, Func<string?> dialog) => DialogOverride is { } o ? o(kind) : window.Dispatcher.Invoke(dialog);

    async Task<ResponseEnvelope> OpenAsync(RequestEnvelope request, CancellationToken ct)
    {
        var path = Ask("open", () =>
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
        CurrentPath = path;
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
        CurrentPath = null; // the JSON file the person had open is not this document's home any more: Save asks where to put it
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
        var path = saveAs || CurrentPath is null
            ? Ask("save", () =>
            {
                var d = new SaveFileDialog { Title = "Save diagram", Filter = "Diagram JSON (*.diagram.json)|*.diagram.json", DefaultExt = ".diagram.json", AddExtension = true };
                return d.ShowDialog(window) == true ? d.FileName : null;
            })
            : CurrentPath;
        if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
        var scope = window.Router.CurrentScope;
        if (scope is null) return Err(request, "not_ready", "the editor is not ready", retryable: true);

        var saved = await window.Coordinator.SaveJsonAsync(path, scope, ct);
        if (!saved.IsOk) return Err(request, saved.Error!);
        CurrentPath = saved.Value.Path;
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
