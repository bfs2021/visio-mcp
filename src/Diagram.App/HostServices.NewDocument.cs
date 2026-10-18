using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

public sealed partial class HostServices
{
    // File actions are serialised in HandleAsync, so an earlier Save cannot mark or name a replacement document.
    readonly SemaphoreSlim newDocumentGate = new(1, 1);
    async Task<ResponseEnvelope> NewDocumentAsync(RequestEnvelope request, CancellationToken ct)
    {
        ExpectedState? expected; DiagramDocument? document;
        try { expected = request.Params["expected"]?.Deserialize<ExpectedState>(ContractJson.Options); document = request.Params["document"]?.Deserialize<DiagramDocument>(ContractJson.Options); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return Err(request, "invalid_request", "New needs a valid document and expected identity/revision"); }
        if (expected is null || document is null || document.Id == expected.DocumentId || document.Title != "Untitled" || document.Revision != 0 || document.Pages.Count != 1 || document.Pages[0].Elements.Count != 0 || document.Assets.Count != 0 || document.Source is not null)
            return Err(request, "invalid_request", "New needs a fresh, empty untitled document");
        var status = await window.Router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "doc.status", DocumentId = expected.DocumentId, SessionId = expected.SessionId, Params = new() }, ct);
        if (status.Error is { } error) return Err(request, error);
        var state = status.Result!.Value;
        if (state.GetProperty("revision").GetInt32() != expected.BaseRevision) return Err(request, "revision_conflict", "the drawing changed; cancel New and try again");
        if (state.GetProperty("unsaved").GetBoolean() && request.Params["discard"]?.GetValue<bool>() != true) return Err(request, "invalid_request", "save the drawing or explicitly choose Don't save before replacing it");
        var previousPath = PathOf(expected.DocumentId);
        SetPath(expected.DocumentId, null); // the first recovery checkpoint must have the new document's path
        Result<Snapshot> replaced;
        try { replaced = await window.Router.ReplaceAsync(document, expected, ct); }
        catch { if (window.Router.ScopeOf(expected.DocumentId)?.SessionId == expected.SessionId) SetPath(expected.DocumentId, previousPath); throw; }
        if (!replaced.IsOk)
        {
            if (window.Router.ScopeOf(expected.DocumentId)?.SessionId == expected.SessionId) SetPath(expected.DocumentId, previousPath);
            return Err(request, replaced.Error!);
        }
        string? warning = null;
        try
        {
            await window.RecoveryMgr.KeepCurrentUnsavedAsync(replaced.Value.SessionId, ct);
            // Only after the new checkpoint and marker are durable may the replaced session be removed.
            await window.RecoveryMgr.DiscardAsync(expected.SessionId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { warning = "The new drawing was created, but recovery could not finish: " + e.Message + ". The previous session was retained; save the new drawing now."; }
        var result = new JsonObject { ["documentId"] = replaced.Value.DocumentId, ["sessionId"] = replaced.Value.SessionId, ["revision"] = replaced.Value.Revision };
        if (warning is not null) result["recoveryWarning"] = warning;
        return Ok(request, result);
    }
}
