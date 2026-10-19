using System.IO;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
namespace Diagram.App;
public sealed partial class HostServices
{
    async Task<string?> RecordRecentAsync(string path, string kind, bool opened)
    {
        try { await window.Recents.RecordAsync(path, kind, opened); return null; }
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
        switch (request.Method)
        {
            case "host.recent.remove":
                if (request.Params["id"]?.GetValue<string>() is not { } id) return Err(request, "invalid_request", "choose a recent entry to remove");
                await window.Recents.RemoveAsync(id, ct); break;
            case "host.recent.clear": await window.Recents.ClearAsync(ct); break;
            case "host.recent.list": break;
            default: return Err(request, "method_not_found", "unknown recent-file action");
        }
        var state = await Task.Run(() => window.Recents.ReadAsync(ct), ct);
        var entries = new JsonArray();
        foreach (var entry in state.Entries)
            entries.Add(new JsonObject { ["id"] = entry.Id, ["path"] = entry.Path, ["kind"] = entry.Kind, ["availability"] = await RecentFilesStore.AvailabilityAsync(entry.Path, ct) });
        return Ok(request, new JsonObject { ["entries"] = entries, ["warning"] = state.Warning });
    }
}
