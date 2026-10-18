using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

/// <summary>What the page asks about recovery: how far the work is protected, whether unfinished work from before exists, and how to take it back or let it go.</summary>
public sealed partial class HostServices
{
    static JsonObject LossJson(RecoveryLoss l) => new()
    {
        ["tornTailDiscarded"] = l.TornTailDiscarded, ["corruptionAfterRevision"] = l.CorruptionAfterRevision,
        ["messages"] = new JsonArray(l.Messages.Select(m => (JsonNode)m).ToArray()),
    };

    /// <summary>What a person needs to recognise a session: the document's title and file, its size at the last checkpoint, and when it last changed.</summary>
    static JsonObject Summary(RecoveryCandidate c)
    {
        var pages = c.Document["pages"] as JsonArray;
        var objects = pages?.Sum(p => (p?["elements"] as JsonArray)?.Count ?? 0) ?? 0;
        return new JsonObject
        {
            ["sessionId"] = c.SessionId, ["documentId"] = c.DocumentId, ["originalPath"] = c.OriginalPath, ["title"] = c.Document["title"]?.GetValue<string>(),
            ["pageCount"] = pages?.Count ?? 0, ["objectCount"] = objects, ["checkpointRevision"] = c.CheckpointRevision, ["lastDurableRevision"] = c.LastDurableRevision,
            ["lastWrite"] = c.LastWrite.ToString("O"), ["loss"] = LossJson(c.Loss),
        };
    }

    async Task<ResponseEnvelope> RecoveryAsync(RequestEnvelope request, CancellationToken ct)
    {
        var mgr = window.RecoveryMgr;
        switch (request.Method)
        {
            case "host.recovery.status":
            {
                var s = mgr.Status;
                return Ok(request, new JsonObject { ["lastDurableRevision"] = s.LastDurableRevision, ["lastCommittedRevision"] = s.LastCommittedRevision, ["degraded"] = s.Degraded, ["note"] = s.Note });
            }
            case "host.recovery.candidate":
            {
                // a short summary only; the document itself is fetched if the person says yes
                var auto = window.Router.TakeRendererFailure();
                var list = await mgr.CandidatesAsync(ct);
                if (list.Count == 0) return Ok(request, new JsonObject { ["found"] = false });
                var c = list[0];
                return Ok(request, new JsonObject
                {
                    ["found"] = true, ["auto"] = auto, ["others"] = list.Count - 1, ["sessionId"] = c.SessionId, ["originalPath"] = c.OriginalPath,
                    ["checkpointRevision"] = c.CheckpointRevision, ["lastDurableRevision"] = c.LastDurableRevision, ["lastWrite"] = c.LastWrite.ToString("O"), ["loss"] = LossJson(c.Loss),
                });
            }
            case "host.recovery.list":
            {
                // every session that holds unsaved work, each described well enough for a person to tell them apart; the documents themselves are fetched on request
                var auto = window.Router.TakeRendererFailure();
                var list = await mgr.CandidatesAsync(ct);
                var sessions = new JsonArray();
                foreach (var c in list) sessions.Add(Summary(c));
                return Ok(request, new JsonObject { ["auto"] = auto, ["sessions"] = sessions });
            }
            case "host.recovery.fetch":
            {
                var id = request.Params["sessionId"]?.GetValue<string>();
                var c = (await mgr.CandidatesAsync(ct)).FirstOrDefault(x => x.SessionId == id);
                if (c is null) return Err(request, "not_found", "that recovery session is no longer available");
                return Ok(request, new JsonObject
                {
                    ["sessionId"] = c.SessionId, ["documentId"] = c.DocumentId, ["originalPath"] = c.OriginalPath, ["checkpointRevision"] = c.CheckpointRevision,
                    ["document"] = c.Document.DeepClone(), ["events"] = new JsonArray(c.Events.Select(e => (JsonNode)e.DeepClone()).ToArray()), ["lastDurableRevision"] = c.LastDurableRevision, ["loss"] = LossJson(c.Loss),
                });
            }
            case "host.recovery.discard":
            {
                var id = request.Params["sessionId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id) || mgr.OpenSessions.Contains(id)) return Err(request, "invalid_request", "that session belongs to an open document and cannot be discarded");
                await mgr.DiscardAsync(id);
                return Ok(request, new JsonObject { ["discarded"] = id });
            }
            case "host.recovery.finish":
            {
                // the page replaced the document with the rebuilt one: keep its original path, and let the old session go once the new one is safe
                var id = request.Params["sessionId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id) || mgr.OpenSessions.Contains(id)) return Err(request, "invalid_request", "that session cannot be finished");
                var original = request.Params["originalPath"]?.GetValue<string>();
                SetCurrentPath(original);
                await mgr.FinishRestoreAsync(id);
                return Ok(request, new JsonObject { ["finished"] = id });
            }
            default: return Err(request, "method_not_found", $"unknown host method {request.Method}");
        }
    }
}
