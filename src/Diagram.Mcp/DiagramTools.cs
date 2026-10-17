using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Diagram.Mcp;

/// <summary>
/// The MCP tool surface. This process is a proxy: it forwards each call to the application over the pipe and owns no document state.
/// It never generates a transaction id and never re-sends a request; a lost reply is reported as an unknown outcome, and the caller
/// retries with the same transaction id, which the application answers from its result cache.
/// </summary>
[McpServerToolType]
public sealed class DiagramTools(PipeClient app)
{
    const string Identity = "Pass the documentId and sessionId returned by get_document_summary. A different session means the document was reopened: read the summary again.";

    // Deadlines: reads 10 s, mutations 15 s, renders 30 s, export 120 s; the shim adds 5 s of transport time.
    static readonly TimeSpan Read = TimeSpan.FromSeconds(15), Mutate = TimeSpan.FromSeconds(20), Render = TimeSpan.FromSeconds(35), Export = TimeSpan.FromSeconds(125);

    async Task<ResponseEnvelope> Send(string method, string? documentId, string? sessionId, JsonObject p, TimeSpan deadline, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);
        var request = new RequestEnvelope { RequestId = Guid.NewGuid().ToString(), Method = method, DocumentId = documentId, SessionId = sessionId, Params = p };
        try { return await app.CallAsync(request, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Wire.Error(request.RequestId, "timeout_unknown", "no reply before the deadline", outcome: "unknown");
        }
    }

    static CallToolResult Finish(ResponseEnvelope r) =>
        r.Error is { } e ? ToolErrors.Fail(e) : ToolErrors.Ok(JsonNode.Parse(r.Result!.Value.GetRawText())!);

    static string? Check(params (string Name, string? Value)[] ids)
    {
        foreach (var (name, value) in ids) if (!Guid.TryParse(value, out _)) return $"{name} must be a UUID";
        return null;
    }

    static JsonObject Obj(params (string, JsonNode?)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) if (v is not null) o[k] = v;
        return o;
    }

    /// <summary>A short description of an object for a tool result: enough to recognise it without rereading the whole document.</summary>
    static JsonNode Brief(JsonNode full)
    {
        var o = new JsonObject();
        foreach (var key in new[] { "id", "kind", "alias", "name", "bounds" }) if (full[key] is { } v) o[key] = v.DeepClone();
        var text = full["text"]?["value"] ?? full["label"]?["value"];
        if (text is not null) o["text"] = text.DeepClone();
        return o;
    }

    async Task<JsonNode> Briefs(string documentId, string sessionId, IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Distinct().Take(50).ToList();
        if (list.Count == 0) return new JsonArray();
        var r = await Send("doc.getObjects", documentId, sessionId, Obj(("ids", new JsonArray(list.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()))), Read, ct);
        if (r.Error is not null) return new JsonArray();
        var objects = JsonNode.Parse(r.Result!.Value.GetRawText())!["objects"]!.AsArray();
        return new JsonArray(objects.Select(o => Brief(o!)).ToArray());
    }

    [McpServerTool(Name = "get_document_summary"), Description("Compact semantic context for the open document: ids, revision, pages, layers and every element with its alias or name, type, bounds, text and connections. Call this first; it also returns the documentId and sessionId the other tools need.")]
    public async Task<CallToolResult> GetDocumentSummary(
        [Description("Limit the summary to one page.")] string? pageId = null,
        [Description("Include element bounds (default true).")] bool includeGeometry = true,
        [Description("Include fill, line and other style (default false).")] bool includeStyle = false,
        [Description("Include hidden elements (default false).")] bool includeHidden = false,
        CancellationToken ct = default)
    {
        if (pageId is not null && Check(("pageId", pageId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("doc.summary", null, null, Obj(("pageId", pageId), ("includeGeometry", includeGeometry), ("includeStyle", includeStyle), ("includeHidden", includeHidden)), Read, ct));
    }

    [McpServerTool(Name = "get_objects"), Description("Complete canonical objects for the given ids (UUIDs, or aliases when the document has one page). " + Identity)]
    public async Task<CallToolResult> GetObjects(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("Element ids to fetch, at most 500.")] string[] ids,
        [Description("Page that aliases and names are looked up on.")] string? pageId = null,
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("doc.getObjects", documentId, sessionId, Obj(("ids", new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray())), ("pageId", pageId)), Read, ct));
    }

    [McpServerTool(Name = "apply_operations"), Description("Applies a batch of typed operations as one atomic transaction at baseRevision. If the document changed since baseRevision the call fails with revision_conflict and changes nothing. You supply the transactionId: retrying with the same id and payload returns the original result instead of applying twice. " + Identity)]
    public async Task<CallToolResult> ApplyOperations(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("A UUID you choose for this transaction. Reuse it only to retry the identical request.")] string transactionId,
        [Description("The revision this batch was planned against.")] int baseRevision,
        [Description("The operations, in order. Each has an op field such as createShape, set, move, resize, rotate, delete, group, align, reorder.")] JsonElement operations,
        [Description("Must be true: batches are all or nothing.")] bool atomic = true,
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId), ("transactionId", transactionId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        if (!atomic) return ToolErrors.Fail("invalid_request", "only atomic batches are supported");
        if (operations.ValueKind != JsonValueKind.Array) return ToolErrors.Fail("invalid_request", "operations must be an array");
        var r = await Send("doc.apply", documentId, sessionId,
            Obj(("transactionId", transactionId), ("baseRevision", baseRevision), ("atomic", true), ("operations", JsonNode.Parse(operations.GetRawText()))), Mutate, ct);
        if (r.Error is not null) return ToolErrors.Fail(r.Error);
        var transaction = JsonNode.Parse(r.Result!.Value.GetRawText())!;
        var ids = (transaction["created"]?.AsArray() ?? []).Concat(transaction["changed"]?.AsArray() ?? []).Select(n => n!.GetValue<string>());
        return ToolErrors.Ok(new JsonObject { ["transaction"] = transaction, ["objects"] = await Briefs(documentId, sessionId, ids, ct) });
    }

    [McpServerTool(Name = "execute_script"), Description("Compiles DrawScript and runs it as one atomic transaction at baseRevision. The result includes the operations the script became. Same retry rule as apply_operations. " + Identity)]
    public async Task<CallToolResult> ExecuteScript(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("A UUID you choose for this transaction. Reuse it only to retry the identical request.")] string transactionId,
        [Description("The revision this script was written against.")] int baseRevision,
        [Description("DrawScript source, one command per line.")] string script,
        [Description("Page used for names and aliases when the script does not say 'page use'.")] string? pageId = null,
        [Description("Must be true.")] bool atomic = true,
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId), ("transactionId", transactionId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        if (!atomic) return ToolErrors.Fail("invalid_request", "only atomic scripts are supported");
        var r = await Send("doc.executeScript", documentId, sessionId,
            Obj(("transactionId", transactionId), ("baseRevision", baseRevision), ("atomic", true), ("script", script), ("pageId", pageId)), Mutate, ct);
        if (r.Error is not null) return ToolErrors.Fail(r.Error);
        var body = JsonNode.Parse(r.Result!.Value.GetRawText())!.AsObject();
        var t = body["transaction"]!;
        var ids = (t["created"]?.AsArray() ?? []).Concat(t["changed"]?.AsArray() ?? []).Select(n => n!.GetValue<string>());
        body["objects"] = await Briefs(documentId, sessionId, ids, ct);
        return ToolErrors.Ok(body);
    }

    [McpServerTool(Name = "render_page"), Description("Renders a page to a PNG or JPEG image. Not available yet: returns not_ready. " + Identity)]
    public async Task<CallToolResult> RenderPage(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("The page to render.")] string pageId,
        [Description("png or jpeg.")] string format = "png",
        [Description("Maximum width in pixels (default 1600).")] int maxWidth = 1600,
        [Description("Maximum height in pixels (default 1200).")] int maxHeight = 1200,
        [Description("clean or debug (debug overlays ids and bounds).")] string mode = "clean",
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId), ("pageId", pageId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("doc.render", documentId, sessionId, Obj(("scope", "page"), ("pageId", pageId), ("format", format), ("maxWidth", maxWidth), ("maxHeight", maxHeight), ("mode", mode)), Render, ct));
    }

    [McpServerTool(Name = "render_region"), Description("Renders part of a page, given as bounds or element ids, with padding. Not available yet: returns not_ready. " + Identity)]
    public async Task<CallToolResult> RenderRegion(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("The page to render.")] string pageId,
        [Description("Region as {x,y,width,height} in points. Give this or elementIds, not both.")] JsonElement? bounds = null,
        [Description("Element ids to frame. Give this or bounds, not both.")] string[]? elementIds = null,
        [Description("Padding in points around the region.")] double paddingPt = 12,
        [Description("png or jpeg.")] string format = "png",
        [Description("clean or debug.")] string mode = "clean",
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId), ("pageId", pageId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("doc.render", documentId, sessionId, Obj(("scope", "region"), ("pageId", pageId), ("bounds", bounds is { } b ? JsonNode.Parse(b.GetRawText()) : null),
            ("elementIds", elementIds is null ? null : new JsonArray(elementIds.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray())), ("paddingPt", paddingPt), ("format", format), ("mode", mode)), Render, ct));
    }

    [McpServerTool(Name = "inspect_layout"), Description("Machine-readable layout warnings (outside the page, overlaps, clipped text, dangling connectors). Not available yet: returns not_ready. " + Identity)]
    public async Task<CallToolResult> InspectLayout(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("Limit to one page.")] string? pageId = null,
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("doc.inspectLayout", documentId, sessionId, Obj(("pageId", pageId)), Read, ct));
    }

    [McpServerTool(Name = "list_assets"), Description("The document's image assets (ids, names, types, tags, how many images use each). Prefer these ids when placing a logo. " + Identity)]
    public async Task<CallToolResult> ListAssets(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("Text to look for in asset ids and names.")] string? query = null,
        [Description("Only assets with any of these tags.")] string[]? tags = null,
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("assets.list", documentId, sessionId, Obj(("query", query), ("tags", tags is null ? null : new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()))), Read, ct));
    }

    [McpServerTool(Name = "get_changes"), Description("Transaction summaries since a revision (created, changed and deleted ids), so you can recover from human edits without rereading the document. " + Identity)]
    public async Task<CallToolResult> GetChanges(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("Return transactions after this revision.")] int sinceRevision,
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("doc.changes", documentId, sessionId, Obj(("sinceRevision", sinceRevision)), Read, ct));
    }

    [McpServerTool(Name = "export_document"), Description("Exports the document as vsdx, svg, png or jpeg. Not available yet: returns not_ready. " + Identity)]
    public async Task<CallToolResult> ExportDocument(
        [Description("The document id from get_document_summary.")] string documentId,
        [Description("The session id from get_document_summary.")] string sessionId,
        [Description("vsdx, svg, png or jpeg.")] string format,
        [Description("Optional output path inside the application-approved workspace.")] string? path = null,
        CancellationToken ct = default)
    {
        if (Check(("documentId", documentId), ("sessionId", sessionId)) is { } bad) return ToolErrors.Fail("invalid_request", bad);
        return Finish(await Send("doc.export", documentId, sessionId, Obj(("format", format), ("path", path)), Export, ct));
    }
}
