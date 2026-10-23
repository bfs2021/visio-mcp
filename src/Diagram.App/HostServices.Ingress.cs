using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

public sealed partial class HostServices
{
    async Task<ResponseEnvelope> ImportIngressImageAsync(RequestEnvelope request, CancellationToken ct)
    {
        IngressImageImportRequest? input;
        try { input = request.Params.Deserialize<IngressImageImportRequest>(ContractJson.Options); }
        catch (JsonException) { return Err(request, "invalid_request", "The picture ingress request is invalid."); }
        if (input is null || string.IsNullOrWhiteSpace(input.TicketId) || input.TicketId.Length > 128 ||
            string.IsNullOrWhiteSpace(input.DocumentId) || string.IsNullOrWhiteSpace(input.SessionId))
            return Err(request, "invalid_request", "A picture ingress ticket and target editor are required.");
        if (window.Router.ChannelOf(input.DocumentId) != BridgeRouter.User ||
            !window.Router.DocumentList.Any(document => document.Scope.DocumentId == input.DocumentId && document.Scope.SessionId == input.SessionId))
            return Err(request, "document_mismatch", "The visible editor session is no longer open.");

        var consumed = IngressTickets.Consume(IngressWindowId, BridgeRouter.User, input.TicketId);
        if (!consumed.IsOk) return Err(request, consumed.Error!);
        var ticket = consumed.Value;
        if (ticket.Kind != IngressKind.Image || ticket.FilePath is null && ticket.ImageBytes is null)
            return Err(request, "invalid_request", "This ticket is not a dropped picture.");
        Result<PreparedUpload> prepared;
        if (ticket.FilePath is { } filePath)
        {
            var safe = PathPolicy.Validate(filePath);
            if (!safe.IsOk) return Err(request, safe.Error!);
            prepared = await PrepareFile(safe.Value, ct);
        }
        else if (ticket.ImageBytes is { Length: > 0 } bytes && ticket.MimeType is { } mime)
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            prepared = await window.Preparer.PrepareAsync(stream, mime, ticket.DisplayName, ct);
        }
        else return Err(request, "invalid_request", "The picture ticket has no approved image source.");
        if (!prepared.IsOk) return Err(request, prepared.Error!);
        var imported = await window.Library.ImportAsync(prepared.Value, null, ct);
        if (!imported.IsOk) return Err(request, imported.Error!);
        var result = imported.Value;
        return Ok(request, new JsonObject
        {
            ["asset"] = Entry(result.Asset), ["created"] = result.Created, ["idWasTaken"] = result.IdWasTaken,
            ["notes"] = new JsonArray(result.Notes.Select(note => (JsonNode)note).ToArray()),
            ["width"] = prepared.Value.Width, ["height"] = prepared.Value.Height,
        });
    }

    ResponseEnvelope DismissIngress(RequestEnvelope request)
    {
        IngressDismissRequest? input;
        try { input = request.Params.Deserialize<IngressDismissRequest>(ContractJson.Options); }
        catch (JsonException) { return Err(request, "invalid_request", "The ingress dismissal request is invalid."); }
        if (input?.TicketIds is null || input.TicketIds.Count > IngressTicketRegistry.MaxFilesPerGesture)
            return Err(request, "invalid_request", "Dismiss at most 32 ingress tickets at a time.");
        var dismissed = IngressTickets.Dismiss(IngressWindowId, BridgeRouter.User, input.TicketIds);
        return Ok(request, new JsonObject { ["dismissed"] = dismissed });
    }

    async Task<ResponseEnvelope> OpenIngressAsync(RequestEnvelope request, CancellationToken ct)
    {
        IngressOpenRequest? input;
        try { input = request.Params.Deserialize<IngressOpenRequest>(ContractJson.Options); }
        catch (JsonException) { return Err(request, "invalid_request", "The ingress request is invalid."); }
        if (input is null || string.IsNullOrWhiteSpace(input.TicketId) || string.IsNullOrWhiteSpace(input.DocumentId) ||
            string.IsNullOrWhiteSpace(input.SessionId) || input.BaseRevision < 0)
            return Err(request, "invalid_request", "The ingress request needs a ticket and expected drawing revision.");

        var scope = new Scope { DocumentId = input.DocumentId, SessionId = input.SessionId };
        if (window.Router.ChannelOf(scope.DocumentId) != BridgeRouter.User)
            return Err(request, "document_mismatch", "The target drawing is not in the visible editor.");
        var status = await window.Router.SendAsync(new RequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString(), Method = "doc.status", DocumentId = scope.DocumentId, SessionId = scope.SessionId, Params = new(),
        }, ct, BridgeRouter.User);
        if (status.Error is { } statusError) return Err(request, statusError);
        var currentRevision = status.Result!.Value.GetProperty("revision").GetInt32();
        if (currentRevision != input.BaseRevision)
            return Err(request, "revision_conflict", "The target drawing changed after the drop. Cancel and drop the files again.");

        var ticket = IngressTickets.Consume(IngressWindowId, BridgeRouter.User, input.TicketId);
        if (!ticket.IsOk) return Err(request, ticket.Error!);
        var item = ticket.Value;
        if (item.Kind == IngressKind.Image || item.FilePath is null)
            return Err(request, "invalid_request", "This ticket is not a diagram file.");
        var safe = PathPolicy.Validate(item.FilePath);
        if (!safe.IsOk) return Err(request, safe.Error!);
        var expected = new ExpectedState { DocumentId = scope.DocumentId, SessionId = scope.SessionId, BaseRevision = currentRevision };

        var alreadyOpen = window.Router.DocumentList.FirstOrDefault(d => PathOf(d.Scope.DocumentId) is { } openPath && SamePath(openPath, safe.Value));
        if (alreadyOpen.Scope is not null)
            return Ok(request, new JsonObject { ["path"] = safe.Value, ["existingDocumentId"] = alreadyOpen.Scope.DocumentId });

        if (item.Kind == IngressKind.Json && safe.Value.EndsWith(".diagram.json", StringComparison.OrdinalIgnoreCase))
            return await OpenJsonPathAsync(request, expected, safe.Value, restored: false, ct);
        if (item.Kind == IngressKind.Vsdx && string.Equals(Path.GetExtension(safe.Value), ".vsdx", StringComparison.OrdinalIgnoreCase))
            return await OpenVsdxPathAsync(request, expected, safe.Value, ct);
        return Err(request, "invalid_request", "The ticket kind does not match its file type.");
    }
}
