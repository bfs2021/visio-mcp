using System.IO;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
namespace Diagram.App;
public sealed partial class HostServices
{
    async Task<string?> RecordRecentAsync(string path, string kind, bool opened)
    {
        try { return await window.Recents.RecordAsync(path, kind, opened); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return "The drawing operation succeeded, but recent-file history could not be updated: " + e.Message; }
    }
    async Task<string?> RecentOpenFolderAsync(CancellationToken ct)
    {
        var state = await Task.Run(() => window.Recents.ReadAsync(ct), ct);
        return state.LastOpenFolder is { } folder && await Task.Run(() => Directory.Exists(folder), ct) ? folder : null;
    }
    async Task<ResponseEnvelope> RecentAsync(RequestEnvelope request, CancellationToken ct)
    {
        string? warning = null;
        switch (request.Method)
        {
            case "host.recent.remove":
                if (request.Params["id"]?.GetValue<string>() is not { } id) return Err(request, "invalid_request", "choose a recent entry to remove");
                warning = await window.Recents.RemoveAsync(id, ct); break;
            case "host.recent.clear": warning = await window.Recents.ClearAsync(ct); break;
            case "host.recent.list": break;
            default: return Err(request, "method_not_found", "unknown recent-file action");
        }
        var state = await Task.Run(() => window.Recents.ReadAsync(ct), ct);
        var availability = await Task.WhenAll(state.Entries.Select(e => RecentFilesStore.AvailabilityAsync(e.Path, ct)));
        var entries = new JsonArray();
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            entries.Add(new JsonObject { ["id"] = entry.Id, ["path"] = entry.Path, ["kind"] = entry.Kind, ["availability"] = availability[i] });
        }
        return Ok(request, new JsonObject { ["entries"] = entries, ["warning"] = warning ?? state.Warning });
    }
}
