using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Microsoft.Win32;
namespace Diagram.App;

public sealed partial class HostServices
{
    sealed record FileOffer(string Token, string Path, ExpectedState Expected, DiskVersion? Baseline, DiskVersion? Observed, string? ReadError);
    readonly Dictionary<string, DiskVersion> diskVersions = new();
    readonly Dictionary<string, FileOffer> fileOffers = new();
    static Scope ScopeOf(ExpectedState expected) => new() { DocumentId = expected.DocumentId, SessionId = expected.SessionId };
    void RecordDisk(string id, DiskVersion version) { lock (pathGate) { diskVersions[id] = version; fileOffers.Remove(id); } }
    DiskVersion? BaselineOf(string id) { lock (pathGate) return diskVersions.GetValueOrDefault(id); }
    static bool SamePath(string? a, string? b) => a is not null && b is not null && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(a), Path.GetFullPath(b));

    async Task<Result<ExpectedState>> FileExpectedAsync(RequestEnvelope request, CancellationToken ct)
    {
        ExpectedState? supplied;
        try { supplied = request.Params["expected"]?.Deserialize<ExpectedState>(ContractJson.Options); }
        catch (JsonException) { return Result<ExpectedState>.Fail("invalid_request", "Invalid document identity."); }
        var scope = supplied is null ? window.Router.CurrentScope : ScopeOf(supplied);
        if (scope is null) return Result<ExpectedState>.Fail("not_ready", "The drawing is not ready.");
        var status = await window.Router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "doc.status", DocumentId = scope.DocumentId, SessionId = scope.SessionId, Params = new() }, ct);
        if (status.Error is { } error) return Result<ExpectedState>.Fail(error);
        var revision = status.Result!.Value.GetProperty("revision").GetInt32();
        if (supplied is not null && revision != supplied.BaseRevision) return Result<ExpectedState>.Fail("revision_conflict", "The drawing changed while the file choice was open. Cancel and try again.");
        return Result<ExpectedState>.Ok(new() { DocumentId = scope.DocumentId, SessionId = scope.SessionId, BaseRevision = revision });
    }
    ResponseEnvelope OfferFile(RequestEnvelope request, string path, ExpectedState expected, DiskVersion? observed, string? readError = null, DiskVersion? baseline = null)
    {
        var offer = new FileOffer(Guid.NewGuid().ToString(), path, expected, baseline ?? BaselineOf(expected.DocumentId), observed, readError);
        lock (pathGate) fileOffers[expected.DocumentId] = offer;
        return Ok(request, new JsonObject { ["conflict"] = JsonSerializer.SerializeToNode(offer, ContractJson.Options) });
    }
    async Task<ResponseEnvelope> CheckFileAsync(RequestEnvelope request, CancellationToken ct)
    {
        var expected = await FileExpectedAsync(request, ct); if (!expected.IsOk) return Err(request, expected.Error!);
        var path = PathOf(expected.Value.DocumentId); if (path is null) return Ok(request, new JsonObject { ["unchanged"] = true });
        try
        {
            var observed = await DiskVersion.ReadAsync(path, ct);
            if (BaselineOf(expected.Value.DocumentId) == observed) return Ok(request, new JsonObject { ["unchanged"] = true });
            return OfferFile(request, path, expected.Value, observed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return OfferFile(request, path, expected.Value, null, e.Message); }
    }
    string? AskSavePath() => Ask("save", () =>
    {
        var d = new SaveFileDialog { Title = "Save diagram", Filter = "Diagram JSON (*.diagram.json)|*.diagram.json", DefaultExt = ".diagram.json", AddExtension = true };
        return d.ShowDialog(window) == true ? d.FileName : null;
    });
    async Task<ResponseEnvelope> SaveFileAsync(RequestEnvelope request, ExpectedState expected, bool saveAs, CancellationToken ct, FileOffer? approved = null)
    {
        var known = PathOf(expected.DocumentId);
        var path = approved?.Path ?? (saveAs || known is null ? AskSavePath() : known);
        if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
        var safe = PathPolicy.Validate(path); if (!safe.IsOk) return Err(request, safe.Error!); path = safe.Value;
        // A tab switch or close during the native dialog must never retarget this save.
        var pinned = request with { Params = new JsonObject { ["expected"] = JsonSerializer.SerializeToNode(expected, ContractJson.Options) } };
        var check = await FileExpectedAsync(pinned, ct); if (!check.IsOk) return Err(request, check.Error!);
        DiskVersion observed;
        try { observed = await DiskVersion.ReadAsync(path, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return OfferFile(request, path, expected, null, e.Message); }
        var baseline = SamePath(path, known) ? BaselineOf(expected.DocumentId) : observed;
        if (approved is not null && SamePath(path, approved.Path)) baseline = approved.Observed;
        if (observed != baseline) return OfferFile(request, path, expected, observed, baseline: baseline);
        try
        {
            var saved = await window.Coordinator.SaveJsonAsync(path, ScopeOf(expected), ct, observed, expected.BaseRevision);
            if (!saved.IsOk) return Err(request, saved.Error!);
            SetPath(expected.DocumentId, saved.Value.Path); RecordDisk(expected.DocumentId, saved.Value.Version!);
            await window.RecoveryMgr.OnSavedAsync(expected.SessionId, saved.Value.Revision, saved.Value.Path);
            return Ok(request, new JsonObject { ["path"] = saved.Value.Path, ["revision"] = saved.Value.Revision, ["warning"] = await RecordRecentAsync(saved.Value.Path, "json", false) });
        }
        catch (DiskChangedException e) { return OfferFile(request, path, expected, e.Observed, baseline: observed); }
    }
    async Task<ResponseEnvelope> ResolveFileAsync(RequestEnvelope request, CancellationToken ct)
    {
        var token = request.Params["token"]?.GetValue<string>();
        FileOffer? offer; lock (pathGate) offer = fileOffers.Values.FirstOrDefault(o => o.Token == token);
        if (offer is null) return Err(request, "invalid_request", "This file choice is no longer current. Save again.");
        var pinned = request with { Params = new JsonObject { ["expected"] = JsonSerializer.SerializeToNode(offer.Expected, ContractJson.Options) } };
        var current = await FileExpectedAsync(pinned, ct); if (!current.IsOk) return Err(request, current.Error!);
        var action = request.Params["action"]?.GetValue<string>();
        if (action == "saveAs") return await SaveFileAsync(request, offer.Expected, true, ct);
        if (action is not ("overwrite" or "reload")) return Err(request, "invalid_request", "Choose Reload, Save as or Overwrite.");
        if (offer.Observed is null) return Err(request, "io_error", "The file cannot be checked. Save as to another location or try again.");
        DiskVersion observed;
        try { observed = await DiskVersion.ReadAsync(offer.Path, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return OfferFile(request, offer.Path, offer.Expected, null, e.Message); }
        if (observed != offer.Observed) return OfferFile(request, offer.Path, offer.Expected, observed);
        if (action == "overwrite") return await SaveFileAsync(request, offer.Expected, false, ct, offer);
        if (!observed.Exists) return Err(request, "not_found", "The file no longer exists. Keep this drawing and use Save as, or explicitly overwrite to recreate it.");
        var status = await window.Router.SendAsync(new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = "doc.status", DocumentId = offer.Expected.DocumentId, SessionId = offer.Expected.SessionId, Params = new() }, ct);
        if (status.Error is { } error) return Err(request, error);
        if (status.Result!.Value.GetProperty("unsaved").GetBoolean() && request.Params["discard"]?.GetValue<bool>() != true) return Err(request, "invalid_request", "Confirm discarding local edits before reloading.");
        try
        {
            var opened = await window.Coordinator.OpenJsonAsync(offer.Path, offer.Expected, ct, observed);
            if (!opened.IsOk) return Err(request, opened.Error!);
            var snap = opened.Value.Snapshot;
            SetPath(snap.DocumentId, offer.Path); RecordDisk(snap.DocumentId, opened.Value.Version!);
            var marked = await window.Router.MarkSavedAsync(new Scope { DocumentId = snap.DocumentId, SessionId = snap.SessionId }, snap.Revision, offer.Path, ct);
            if (!marked.IsOk) return Err(request, marked.Error!);
            string? warning = null;
            try
            {
                await window.RecoveryMgr.DrainAsync();
                var protection = window.RecoveryMgr.StatusOfSession(snap.SessionId);
                if (!window.RecoveryMgr.OpenSessions.Contains(snap.SessionId) || protection.Degraded || protection.LastDurableRevision < snap.Revision)
                    throw new InvalidOperationException("The replacement checkpoint is not durable.");
                await window.RecoveryMgr.DiscardAsync(offer.Expected.SessionId);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { warning = "Reloaded, but the previous recovery session was retained: " + e.Message; }
            return Ok(request, new JsonObject { ["reloaded"] = true, ["path"] = offer.Path, ["revision"] = snap.Revision, ["warning"] = warning });
        }
        catch (DiskChangedException e) { return OfferFile(request, offer.Path, offer.Expected, e.Observed); }
    }
}
