using System.IO;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

/// <summary>What the page asks about the remembered tabs: record them as they change, and read them back at start.</summary>
public sealed partial class HostServices
{
    // Files the last workspace listed. Reopening one needs no dialog, but only these: the page cannot name any other path.
    readonly HashSet<string> reopenable = new(StringComparer.OrdinalIgnoreCase);

    bool MayReopen(string path) { lock (reopenable) return reopenable.Contains(Path.GetFullPath(path)); }

    async Task<ResponseEnvelope> WorkspaceAsync(RequestEnvelope request, CancellationToken ct)
    {
        switch (request.Method)
        {
            case "host.workspace.save":
            {
                var tabs = new List<WorkspaceTab>();
                foreach (var t in request.Params["tabs"] as JsonArray ?? [])
                {
                    var doc = t?["documentId"]?.GetValue<string>();
                    var session = t?["sessionId"]?.GetValue<string>();
                    if (doc is null || session is null) continue;
                    tabs.Add(new WorkspaceTab(doc, session, PathOf(doc), t?["title"]?.GetValue<string>(), t?["unsaved"]?.GetValue<bool>() == true));
                }
                await window.Workspace.WriteAsync(tabs, request.Params["activeDocumentId"]?.GetValue<string>(), ct);
                return Ok(request, new JsonObject { ["saved"] = tabs.Count });
            }
            case "host.workspace.load":
            {
                var state = await window.Workspace.ReadAsync(ct);
                if (state is null) return Ok(request, new JsonObject { ["found"] = false });
                lock (reopenable)
                {
                    reopenable.Clear();
                    foreach (var t in state.Tabs) if (!string.IsNullOrEmpty(t.Path)) try { reopenable.Add(Path.GetFullPath(t.Path)); } catch (ArgumentException) { }
                }
                var arr = new JsonArray();
                foreach (var t in state.Tabs)
                    arr.Add(new JsonObject { ["documentId"] = t.DocumentId, ["sessionId"] = t.SessionId, ["path"] = t.Path, ["title"] = t.Title, ["unsaved"] = t.Unsaved });
                return Ok(request, new JsonObject { ["found"] = true, ["cleanExit"] = state.CleanExit, ["activeDocumentId"] = state.ActiveDocumentId, ["tabs"] = arr });
            }
            default: return Err(request, "method_not_found", $"unknown host method {request.Method}");
        }
    }
}
