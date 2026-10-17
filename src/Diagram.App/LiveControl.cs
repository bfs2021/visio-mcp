using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Ipc;

namespace Diagram.App;

/// <summary>
/// Serves outside clients (the MCP shim) over the user's pipe. It offers the document tools only: open, save, replace and the
/// other lifecycle methods stay inside the application, so no client can reach the file system or the editor's session through here.
/// Every request is forwarded to the page with a host-stamped origin, and the page decides ordering, admission and revisions.
/// </summary>
public sealed class LiveControl : IAsyncDisposable
{
    static readonly HashSet<string> Offered = new()
    {
        "doc.summary", "doc.getObjects", "doc.apply", "doc.executeScript", "doc.changes", "doc.inspectLayout", "doc.render", "doc.export", "assets.list",
    };

    /// <summary>Calls that may omit the scope because they are how a client first learns it.</summary>
    static readonly HashSet<string> ScopeOptional = new() { "doc.summary", "assets.list" };

    readonly BridgeRouter router;
    readonly PipeServer server;
    readonly Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> export;

    public LiveControl(string pipeName, BridgeRouter router, Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> export)
    {
        this.router = router;
        this.export = export;
        server = PipeServer.Start(pipeName, HandleAsync);
    }

    public PipeServer Server => server;

    async Task<ResponseEnvelope> HandleAsync(ConnectionInfo connection, RequestEnvelope request, CancellationToken ct)
    {
        if (!Offered.Contains(request.Method)) return Wire.Error(request.RequestId, "method_not_found", $"{request.Method} is not available to clients");
        if (ScopeOptional.Contains(request.Method) && request.DocumentId is null && request.SessionId is null && router.CurrentScope is { } scope)
            request = request with { DocumentId = scope.DocumentId, SessionId = scope.SessionId };
        if (request.Method == "doc.export") return await export(request, ct); // the host writes the file itself, inside the approved folders
        return await router.SendAsync(request, ct);
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
