using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

public sealed partial class ExportService
{
    public PrintJobStore PrintJobs { get; } = new();
    public IPrinterCapabilities PrinterCapabilities { get; set; } = new WindowsPrinterCapabilities();
    public Func<PreparedPrintJob, string, Func<CancellationToken, Task>, CancellationToken, Task<JsonNode>>? PrintOutputHandler { get; set; }

    public async Task<ResponseEnvelope> HandlePrintEditorAsync(RequestEnvelope request, CancellationToken ct)
    {
        try
        {
            var p = request.Params;
            var action = p["action"]?.GetValue<string>() ?? "";
            if (action == "devices")
            {
                var devices = PrinterCapabilities.GetDevices();
                return Ok(request, new JsonObject { ["devices"] = JsonSerializer.SerializeToNode(devices, ContractJson.Options) });
            }
            if (action is "print" or "savePdf")
            {
                var id = p["jobId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) Refuse("invalid_request", "Print output needs the prepared job ID.");
                var active = window.Router.CurrentScope;
                if (active is null) Refuse("not_ready", "There is no active drawing to print.");
                var current = await window.Router.SnapshotAsync(active, ct);
                if (!current.IsOk) throw new ExportRejected(current.Error!);
                var currentState = new ExpectedState { DocumentId = active.DocumentId, SessionId = active.SessionId, BaseRevision = current.Value.Revision };
                if (!PrintJobs.TryTake(id, currentState, out var takenJob, out var code))
                    Refuse(code ?? "job_not_found", code switch { "revision_conflict" => "The drawing changed after preview. Prepare a fresh preview before output.", "session_mismatch" => "The active drawing changed after preview. Prepare a fresh preview before output.", "job_expired" => "The prepared print preview expired. Prepare a fresh preview.", _ => "The prepared print preview is missing or has already been used." });
                if (PrintOutputHandler is null) Refuse("not_ready", "Print output is not available yet.");
                async Task Guard(CancellationToken token) => await CheckExportAsync(takenJob!.Expected, token);
                await Guard(ct);
                return Ok(request, await PrintOutputHandler(takenJob!, action, Guard, ct));
            }
            if (action != "preview") Refuse("invalid_request", "Choose devices, preview, print or savePdf.");

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lifetime.CancelAfter(TimeSpan.FromMinutes(2));
            ct = lifetime.Token;
            var expected = p["expected"]?.Deserialize<ExpectedState>(ContractJson.Options);
            if (expected is null) Refuse("invalid_request", "Print preview needs the drawing identity and revision.");
            var snap = await CheckExportAsync(expected, ct);
            var selected = PrintPageRange.Parse(p["pageRange"]?.GetValue<string>() ?? "1", snap.Document.Pages.Count);
            var printerId = p["printerId"]?.GetValue<string>() ?? "";
            var mediaId = p["mediaId"]?.GetValue<string>() ?? "";
            var device = PrinterCapabilities.GetDevices().FirstOrDefault(x => x.Id == printerId);
            var media = device?.Media.FirstOrDefault(x => x.Id == mediaId);
            if (device is null || media is null) Refuse("unsupported_setting", "Choose a printer and paper size reported by the host.");
            var orientation = p["orientation"]?.GetValue<string>() ?? "portrait";
            var mode = p["layout"]?.GetValue<string>() ?? "fit";
            var requested = p["margins"]?.Deserialize<PrintMargins>(ContractJson.Options) ?? new PrintMargins(36, 36, 36, 36);
            var sourcePages = new List<PrintSourcePage>(selected.Count);
            long renderedBytes = 0;
            foreach (var number in selected)
            {
                await CheckExportAsync(expected, ct);
                var page = snap.Document.Pages[number - 1];
                var scope = new Scope { DocumentId = expected.DocumentId, SessionId = expected.SessionId };
                var rendered = await window.Router.RenderAsync(scope, new JsonObject
                {
                    ["scope"] = "page", ["pageId"] = page.Id, ["format"] = "svg", ["scale"] = 1, ["background"] = "white", ["mode"] = "clean",
                }, ct);
                if (!rendered.IsOk) throw new ExportRejected(rendered.Error!);
                if (rendered.Value.GetProperty("revision").GetInt32() != expected.BaseRevision) Refuse("revision_conflict", "The drawing changed during print preview.");
                var bytes = Convert.FromBase64String(rendered.Value.GetProperty("base64").GetString()!);
                renderedBytes += bytes.Length;
                if (renderedBytes > PrintJobStore.MaximumBytes) Refuse("limit_exceeded", "Rendered print pages exceed 64 MiB.");
                sourcePages.Add(new PrintSourcePage(page.Id, number, page.WidthPt, page.HeightPt, System.Text.Encoding.UTF8.GetString(bytes)));
            }
            await CheckExportAsync(expected, ct);
            var options = new PrintLayoutOptions(media.WidthPt, media.HeightPt, orientation, mode, requested, media.MinimumMargins);
            var sheets = PrintLayoutPlanner.Build(sourcePages, options);
            var effectiveMargins = new PrintMargins(
                Math.Max(requested.LeftPt, media.MinimumMargins.LeftPt), Math.Max(requested.TopPt, media.MinimumMargins.TopPt),
                Math.Max(requested.RightPt, media.MinimumMargins.RightPt), Math.Max(requested.BottomPt, media.MinimumMargins.BottomPt));
            var settings = new PrintDeviceSettings(device.Id, media.Id, media.WidthPt, media.HeightPt, orientation, effectiveMargins);
            var preparedJob = PrintJobs.Prepare(expected, sheets, options, settings);
            var warnings = new JsonArray();
            if (effectiveMargins != requested) warnings.Add("The selected printer has a non-printable border; margins were increased to its imageable area.");
            return Ok(request, new JsonObject
            {
                ["jobId"] = preparedJob.Id,
                ["sheets"] = JsonSerializer.SerializeToNode(preparedJob.Sheets, ContractJson.Options),
                ["settings"] = JsonSerializer.SerializeToNode(preparedJob.Device, ContractJson.Options),
                ["marginWarnings"] = warnings,
                ["expiresAt"] = preparedJob.ExpiresAt,
                ["sourcePageCount"] = selected.Count,
            });
        }
        catch (PrintLayoutException e) { return new ResponseEnvelope { RequestId = request.RequestId, Error = new AppError { Code = e.Code, Message = e.Message, Retryable = false } }; }
        catch (ExportRejected e) { return new ResponseEnvelope { RequestId = request.RequestId, Error = e.Error }; }
        catch (OperationCanceledException) { return new ResponseEnvelope { RequestId = request.RequestId, Error = new AppError { Code = "cancelled", Message = "Print preview timed out or was cancelled.", Retryable = false } }; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
        { return new ResponseEnvelope { RequestId = request.RequestId, Error = new AppError { Code = "invalid_request", Message = e.Message, Retryable = false } }; }
    }
}
