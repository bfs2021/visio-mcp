using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Ipc;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>A real stdio MCP client, the real shim process, the real pipe, and the real WPF/WebView2 editor.</summary>
public class LiveControlTests
{
    static string NewPipe() => $"live-test-{Guid.NewGuid():N}";
    static CancellationToken Ct(int s = 60) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;

    static string ShimDll()
    {
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", cfg, "net10.0", "Diagram.Mcp.dll");
    }

    static Task<McpClient> Connect(string pipe) => McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "live", Command = "dotnet", Arguments = [ShimDll()],
        EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
    }), cancellationToken: Ct());

    static JsonElement Body(CallToolResult r) => r.StructuredContent!.Value;
    static string ErrorCode(CallToolResult r) => Body(r).GetProperty("error").GetProperty("code").GetString()!;

    sealed record Identity(string Doc, string Session, int Revision);

    static async Task<Identity> Summary(McpClient c)
    {
        var r = await c.CallToolAsync("get_document_summary", cancellationToken: Ct());
        Assert.True(r.IsError != true);
        var b = Body(r);
        return new Identity(b.GetProperty("documentId").GetString()!, b.GetProperty("sessionId").GetString()!, b.GetProperty("revision").GetInt32());
    }

    static Dictionary<string, object?> Apply(Identity id, string tx, int baseRevision, params object[] ops) => new()
    {
        ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["transactionId"] = tx, ["baseRevision"] = baseRevision,
        ["operations"] = JsonSerializer.SerializeToElement(ops),
    };

    static object Shape(string alias, double x = 10, double y = 10) =>
        new { op = "createShape", alias, bounds = new { x, y, width = 50, height = 30 }, text = new { value = alias } };

    [Fact]
    public async Task ReadsShowTheCurrentCanonicalStore()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var id = await Summary(client);
        Assert.Equal(h.Router.CurrentScope!.DocumentId, id.Doc);
        Assert.Equal(h.Router.CurrentScope!.SessionId, id.Session);
        Assert.Equal(0, id.Revision);

        var applied = await client.CallToolAsync("apply_operations", Apply(id, Guid.NewGuid().ToString(), 0, Shape("alpha"), Shape("beta", 120, 10)), cancellationToken: Ct());
        Assert.True(applied.IsError != true, applied.StructuredContent?.ToString());
        var objects = Body(applied).GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("alias").GetString()).ToList();
        Assert.Equal(new[] { "alpha", "beta" }, objects.OrderBy(x => x));

        // a later read, and the host's own view of the page, agree
        var after = await client.CallToolAsync("get_document_summary", cancellationToken: Ct());
        var page = Body(after).GetProperty("pages")[0];
        Assert.Equal(1, Body(after).GetProperty("revision").GetInt32());
        Assert.Equal(new[] { "alpha", "beta" }, page.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("alias").GetString()).OrderBy(x => x));
        var snap = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        Assert.Equal(2, snap.Document.Pages[0].Elements.Count);
        Assert.Equal("1", await h.Eval("document.body.dataset.revision").ContinueWith(t => t.Result.Trim('"'))); // and the page itself shows it

        var objectsCall = await client.CallToolAsync("get_objects", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["ids"] = new[] { "alpha" } }, cancellationToken: Ct());
        Assert.Equal("shape", Body(objectsCall).GetProperty("objects")[0].GetProperty("kind").GetString());
        var changes = await client.CallToolAsync("get_changes", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["sinceRevision"] = 0 }, cancellationToken: Ct());
        Assert.Equal(2, Body(changes).GetProperty("transactions")[0].GetProperty("created").GetArrayLength());
        var assets = await client.CallToolAsync("list_assets", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session }, cancellationToken: Ct());
        Assert.Equal(0, Body(assets).GetProperty("assets").GetArrayLength());
    }

    [Fact]
    public async Task ReconnectAfterALostCommittedReplyReturnsTheIdenticalResult()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var first = await Connect(pipe);
        var id = await Summary(first);
        var tx = Guid.NewGuid().ToString();
        var args = Apply(id, tx, 0, Shape("once"));
        var original = await first.CallToolAsync("apply_operations", args, cancellationToken: Ct());
        Assert.True(original.IsError != true);

        // the reply "was lost": a new client over a new connection (and a new shim process) retries the identical request
        h.Window.Live!.Server.DropConnections();
        await using var second = await Connect(pipe);
        var retry = await second.CallToolAsync("apply_operations", args, cancellationToken: Ct());
        Assert.True(retry.IsError != true, retry.StructuredContent?.ToString());
        Assert.Equal(Body(original).GetProperty("transaction").GetRawText(), Body(retry).GetProperty("transaction").GetRawText());

        // and on the first client, whose connection was dropped, the next call reconnects by itself
        var again = await first.CallToolAsync("apply_operations", args, cancellationToken: Ct());
        Assert.Equal(Body(original).GetProperty("transaction").GetRawText(), Body(again).GetProperty("transaction").GetRawText());

        var snap = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        Assert.Equal(1, snap.Revision);
        Assert.Single(snap.Document.Pages[0].Elements); // created once, however many times it was sent
    }

    [Fact]
    public async Task ChangedPayloadWithTheSameTransactionIdConflicts()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var id = await Summary(client);
        var tx = Guid.NewGuid().ToString();
        Assert.True((await client.CallToolAsync("apply_operations", Apply(id, tx, 0, Shape("a")), cancellationToken: Ct())).IsError != true);
        var different = await client.CallToolAsync("apply_operations", Apply(id, tx, 0, Shape("b")), cancellationToken: Ct());
        Assert.True(different.IsError);
        Assert.Equal("transaction_id_conflict", ErrorCode(different));
        Assert.Equal(1, h.Router.CurrentRevision);
    }

    [Fact]
    public async Task AStaleRevisionAfterAHumanEditIsRefusedAndTheHumanWorkStaysUntouched()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var id = await Summary(client);
        // the person edits (revision 1) through the application's own path while the agent still holds revision 0
        var human = await h.Apply(0, null, Harness.Shape("human"));
        Assert.Null(human.Error);

        var stale = await client.CallToolAsync("apply_operations", Apply(id, Guid.NewGuid().ToString(), 0, Shape("agent")), cancellationToken: Ct());
        Assert.True(stale.IsError);
        Assert.Equal("revision_conflict", ErrorCode(stale));
        var details = Body(stale).GetProperty("error").GetProperty("details");
        Assert.Equal(1, details.GetProperty("actual").GetInt32());
        Assert.Equal(1, details.GetProperty("changesSinceExpected")[0].GetProperty("created").GetArrayLength()); // what changed is reported so the agent can recover
        var snap = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        Assert.Equal(new[] { "human" }, snap.Document.Pages[0].Elements.Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!["alias"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ScriptCreateRunsOnceAndShowsTheOperationsItBecame()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var id = await Summary(client);
        var tx = Guid.NewGuid().ToString();
        var args = new Dictionary<string, object?>
        {
            ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["transactionId"] = tx, ["baseRevision"] = 0,
            ["script"] = "add shape id=api x=10mm y=10mm w=40mm h=15mm text=\"API\"\nadd shape id=db x=10mm y=40mm w=40mm h=15mm text=\"DB\"\nconnect from=api to=db",
        };
        var first = await client.CallToolAsync("execute_script", args, cancellationToken: Ct());
        Assert.True(first.IsError != true, first.StructuredContent?.ToString());
        Assert.Equal(new[] { "createShape", "createShape", "createConnector" }, Body(first).GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("op").GetString()));
        Assert.Equal(3, Body(first).GetProperty("transaction").GetProperty("created").GetArrayLength());

        await using var second = await Connect(pipe);
        var retry = await second.CallToolAsync("execute_script", args, cancellationToken: Ct());
        Assert.Equal(Body(first).GetProperty("transaction").GetRawText(), Body(retry).GetProperty("transaction").GetRawText());
        var snap = (await h.Send("doc.snapshot")).Result!.Value.Deserialize<Snapshot>(ContractJson.Options)!;
        Assert.Equal(3, snap.Document.Pages[0].Elements.Count); // once, not twice
        Assert.Equal(1, snap.Revision);

        var bad = new Dictionary<string, object?>(args) { ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = 1, ["script"] = "move api dx=1mm dy=1mm\nmove api dx=oops" };
        var failed = await client.CallToolAsync("execute_script", bad, cancellationToken: Ct());
        Assert.True(failed.IsError);
        Assert.Equal("invalid_request", ErrorCode(failed));
        Assert.Equal(2, Body(failed).GetProperty("error").GetProperty("details").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task SessionAndDocumentMismatchesAreRefusedAtTheGate()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var id = await Summary(client);
        var wrongSession = await client.CallToolAsync("get_changes", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = Guid.NewGuid().ToString(), ["sinceRevision"] = 0 }, cancellationToken: Ct());
        Assert.Equal("session_mismatch", ErrorCode(wrongSession));
        var wrongDoc = await client.CallToolAsync("get_changes", new Dictionary<string, object?> { ["documentId"] = Guid.NewGuid().ToString(), ["sessionId"] = id.Session, ["sinceRevision"] = 0 }, cancellationToken: Ct());
        Assert.Equal("document_mismatch", ErrorCode(wrongDoc));
        var render = await client.CallToolAsync("render_page", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["pageId"] = Guid.NewGuid().ToString() }, cancellationToken: Ct());
        Assert.Equal("not_found", ErrorCode(render)); // a page that does not exist is named as such: rendering is real now
    }

    [Fact]
    public async Task LifecycleMethodsAreNotReachableOverThePipe()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = new PipeClient(pipe, TimeSpan.FromSeconds(5));
        var scope = h.Router.CurrentScope!;
        foreach (var method in new[] { "doc.replace", "doc.markSaved", "doc.undo", "doc.status", "host.openJson", "host.saveJson", "host.pickImage", "doc.snapshot" })
        {
            var r = await client.CallAsync(new RequestEnvelope { RequestId = "x", Method = method, DocumentId = scope.DocumentId, SessionId = scope.SessionId, Params = new JsonObject() }, Ct());
            Assert.Equal("method_not_found", r.Error?.Code);
        }
        Assert.Equal(0, h.Router.CurrentRevision);
    }

    [Fact]
    public async Task OnlyOneApplicationOwnsThePipe()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        var e = Record.Exception(() => PipeServer.Start(pipe, (c, r, ct) => Task.FromResult(Wire.Error(r.RequestId, "internal_error", "no"))));
        Assert.True(e is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task VisualLoopInspectRenderOneObjectPatchLeavesTheRestExact()
    {
        var pipe = NewPipe();
        await using var h = await Harness.StartAsync(pipe: pipe);
        await using var client = await Connect(pipe);
        var id = await Summary(client);
        var make = await client.CallToolAsync("apply_operations", Apply(id, Guid.NewGuid().ToString(), 0,
            new { op = "createShape", alias = "tight", bounds = new { x = 40, y = 40, width = 70, height = 24 }, text = new { value = "A label that will not fit in here", fontSizePt = 14 } },
            new { op = "createShape", alias = "roomy", bounds = new { x = 200, y = 40, width = 240, height = 80 }, text = new { value = "Fits easily", fontSizePt = 14 } },
            new { op = "createShape", alias = "other", bounds = new { x = 40, y = 200, width = 100, height = 60 } }), cancellationToken: Ct());
        Assert.True(make.IsError != true, make.StructuredContent?.ToString());
        var pageId = (await client.CallToolAsync("get_document_summary", cancellationToken: Ct())).StructuredContent!.Value.GetProperty("pages")[0].GetProperty("id").GetString()!;
        string Objects(string alias) => Body(client.CallToolAsync("get_objects", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["ids"] = new[] { alias } }, cancellationToken: Ct()).Result).GetProperty("objects")[0].GetRawText();
        var roomyBefore = Objects("roomy"); var otherBefore = Objects("other");

        // 1. inspect: the model is told which element has a problem, and where
        var inspect = await client.CallToolAsync("inspect_layout", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session }, cancellationToken: Ct());
        Assert.True(inspect.IsError != true, inspect.StructuredContent?.ToString());
        var issues = Body(inspect).GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("code").GetString() == "TEXT_OVERFLOW").ToList();
        var issue = Assert.Single(issues);
        var tightId = issue.GetProperty("elementIds")[0].GetString()!;
        Assert.Equal(40, issue.GetProperty("bounds").GetProperty("x").GetDouble());

        // 2. look at just that region: a real PNG comes back as an image, tagged with the revision it shows
        var region = await client.CallToolAsync("render_region", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["pageId"] = pageId, ["elementIds"] = new[] { tightId }, ["paddingPt"] = 10.0 }, cancellationToken: Ct());
        Assert.True(region.IsError != true, region.StructuredContent?.ToString());
        var image = Assert.IsType<ImageContentBlock>(region.Content.First());
        var bytes = image.DecodedData.ToArray();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]); // PNG signature
        Assert.Equal(1, Body(region).GetProperty("revision").GetInt32());
        Assert.Equal(40 - 10, Body(region).GetProperty("cropPt").GetProperty("x").GetDouble());

        // 3. correct only that object
        var fix = await client.CallToolAsync("apply_operations", Apply(id, Guid.NewGuid().ToString(), 1, new { op = "resize", target = tightId, bounds = new { width = 220, height = 60 } }), cancellationToken: Ct());
        Assert.True(fix.IsError != true, fix.StructuredContent?.ToString());
        Assert.Equal(roomyBefore, Objects("roomy")); // every other object is byte for byte what it was
        Assert.Equal(otherBefore, Objects("other"));

        // 4. the problem is gone, and a render of the new revision says so
        var again = await client.CallToolAsync("inspect_layout", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session }, cancellationToken: Ct());
        Assert.DoesNotContain(Body(again).GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "TEXT_OVERFLOW");
        var page = await client.CallToolAsync("render_page", new Dictionary<string, object?> { ["documentId"] = id.Doc, ["sessionId"] = id.Session, ["pageId"] = pageId, ["maxWidth"] = 800 }, cancellationToken: Ct());
        Assert.Equal(2, Body(page).GetProperty("revision").GetInt32());
        Assert.Equal(800, Body(page).GetProperty("widthPx").GetInt32());
    }
}
