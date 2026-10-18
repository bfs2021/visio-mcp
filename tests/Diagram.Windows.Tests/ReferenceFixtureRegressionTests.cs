using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

public class ReferenceFixtureRegressionTests
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static string Text(JsonElement e) => e.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object ? text.GetProperty("value").GetString()! : "";
    static Dictionary<string, JsonElement> Objects(JsonElement body) => body.GetProperty("objects").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.Clone());
    static void SameObjects(Dictionary<string, JsonElement> before, Dictionary<string, JsonElement> after, params string[] changed)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (id, e) in before) if (!changed.Contains(id)) Assert.Equal(e.GetRawText(), after[id].GetRawText());
    }
    static void FixtureSemantics(JsonElement operations, Dictionary<string, JsonElement> objects)
    {
        var expected = operations.EnumerateArray().Where(o => o.GetProperty("op").GetString() is "createShape" or "createText").ToArray();
        Assert.Equal(62, expected.Length); Assert.Equal(62, objects.Count);
        Assert.All(objects.Keys, id => Assert.True(Guid.TryParse(id, out _)));
        var byAlias = objects.Values.ToDictionary(e => e.GetProperty("alias").GetString()!);
        Assert.Equal(39, objects.Values.Count(e => Text(e).Length > 0));
        foreach (var op in expected)
        {
            var e = byAlias[op.GetProperty("alias").GetString()!];
            Assert.Equal(op.GetProperty("op").GetString() == "createText" ? "text" : "shape", e.GetProperty("kind").GetString());
            Assert.Equal(Text(op), Text(e));
            foreach (var key in new[] { "x", "y", "width", "height" }) Assert.Equal(op.GetProperty("bounds").GetProperty(key).GetDouble(), e.GetProperty("bounds").GetProperty(key).GetDouble(), 6);
        }
    }
    static void RoundTripSemantics(Dictionary<string, JsonElement> expected, JsonElement elements)
    {
        var actual = elements.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (id, e) in expected)
        {
            Assert.Equal(e.GetProperty("kind").GetString(), actual[id].GetProperty("kind").GetString());
            Assert.Equal(Text(e), Text(actual[id]));
            foreach (var key in new[] { "x", "y", "width", "height" }) Assert.InRange(Math.Abs(e.GetProperty("bounds").GetProperty(key).GetDouble() - actual[id].GetProperty("bounds").GetProperty(key).GetDouble()), 0, 0.05);
        }
    }
    [Fact]
    public async Task SixtyTwoObjectsPreserveSemanticsAndDetectVisibleRegressionsThroughMcpAndNativeVisio()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r05-regression"); Directory.CreateDirectory(evidence);
        var baseline = Path.Combine(Harness.RepoRoot, "tests/fixtures/diagrams/r05-baselines");
        using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, "provenance.json"), ct));
        async Task CheckHash(string file, string key) => Assert.Equal(provenance.RootElement.GetProperty(key).GetString(), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct))));
        await CheckHash(Path.Combine(baseline, "editor.png"), "EditorSha256");
        await CheckHash(Path.Combine(baseline, "visio.png"), "VisioSha256");
        await CheckHash(Path.Combine(Harness.RepoRoot, "tests/fixtures/diagrams/repository-deployment-light.operations.json"), "FixtureSha256");
        var pipe = "r05-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "r05-reference", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        var identity = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = identity.GetProperty("documentId").GetString(), ["sessionId"] = identity.GetProperty("sessionId").GetString() };
        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: ct); Assert.True(r.IsError != true, r.StructuredContent?.ToString()); return r;
        }
        int revision = 0;
        async Task<JsonElement> Apply(object operations)
        {
            var args = Identity(); args["baseRevision"] = revision; args["transactionId"] = Guid.NewGuid().ToString(); args["operations"] = JsonSerializer.SerializeToElement(operations);
            var r = (await Call("apply_operations", args)).StructuredContent!.Value; revision = r.GetProperty("transaction").GetProperty("revision").GetInt32(); return r;
        }
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Harness.RepoRoot, "tests/fixtures/diagrams/repository-deployment-light.operations.json"), ct));
        await Apply(fixture.RootElement);
        var pageId = fixture.RootElement[0].GetProperty("id").GetString()!;
        var summary = (await Call("get_document_summary", Identity())).StructuredContent!.Value;
        var ids = summary.GetProperty("pages").EnumerateArray().Single(p => p.GetProperty("id").GetString() == pageId).GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
        async Task<Dictionary<string, JsonElement>> Read()
        {
            var args = Identity(); args["ids"] = ids; return Objects((await Call("get_objects", args)).StructuredContent!.Value);
        }
        var original = await Read(); FixtureSemantics(fixture.RootElement, original);
        var aliases = original.Values.ToDictionary(e => e.GetProperty("alias").GetString()!, e => e.GetProperty("id").GetString()!);
        var fit = Identity(); fit["ids"] = new[] { aliases["release-left-arrows"], aliases["release-right-arrows"] }; fit["target"] = "visio"; fit["strategy"] = "shrink_font";
        var proposal = (await Call("get_text_layout", fit)).StructuredContent!.Value; await Apply(proposal.GetProperty("operations"));
        var fitted = await Read(); SameObjects(original, fitted, aliases["release-left-arrows"], aliases["release-right-arrows"]);
        async Task<byte[]> Render(string name)
        {
            var args = Identity(); args["pageId"] = pageId; args["maxWidth"] = 1600; args["maxHeight"] = 1300;
            var result = await Call("render_page", args); var bytes = Assert.IsType<ImageContentBlock>(result.Content.First()).DecodedData.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(evidence, name), bytes, ct); return bytes;
        }
        var expectedEditor = await File.ReadAllBytesAsync(Path.Combine(baseline, "editor.png"), ct);
        var editor = RasterRegression.Compare(expectedEditor, await Render("editor.png")); Assert.True(editor.Pass, JsonSerializer.Serialize(editor));
        var inspectorArgs = Identity(); inspectorArgs["pageId"] = pageId;
        var inspected = (await Call("inspect_layout", inspectorArgs)).StructuredContent!.Value;
        Assert.DoesNotContain(inspected.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "ZERO_SIZE");
        SameObjects(fitted, await Read());
        var panel = aliases["infra-actions"];
        var movedPanel = await Apply(new[] { new { op = "move", target = panel, delta = new { xPt = 0, yPt = 5 } } });
        Assert.Single(movedPanel.GetProperty("transaction").GetProperty("changed").EnumerateArray()); SameObjects(fitted, await Read(), panel);
        var near = (await Call("inspect_layout", inspectorArgs)).StructuredContent!.Value;
        Assert.Contains(near.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "NEAR_ALIGNMENT" && i.GetProperty("elementIds").EnumerateArray().Any(id => id.GetString() == panel));
        await Apply(new[] { new { op = "move", target = panel, delta = new { xPt = 0, yPt = -5 } } }); SameObjects(fitted, await Read());
        var rule = aliases["infra-rule"];
        await Apply(new[] { new { op = "set", target = rule, patch = new { hidden = true } } }); SameObjects(fitted, await Read(), rule);
        var missingRule = RasterRegression.Compare(expectedEditor, await Render("missing-divider-control.png")); Assert.False(missingRule.Pass, JsonSerializer.Serialize(missingRule));
        await Apply(new[] { new { op = "set", target = rule, patch = new { hidden = false } } }); SameObjects(fitted, await Read());
        var title = aliases["infra-title"];
        await Apply(new[] { new { op = "move", target = title, delta = new { xPt = 24, yPt = 0 } } }); SameObjects(fitted, await Read(), title);
        var displaced = RasterRegression.Compare(expectedEditor, await Render("displaced-control.png")); Assert.False(displaced.Pass);
        await Apply(new[] { new { op = "move", target = title, delta = new { xPt = -24, yPt = 0 } } }); SameObjects(fitted, await Read());
        Assert.True(RasterRegression.Compare(expectedEditor, await Render("restored.png")).Pass);
        await File.WriteAllTextAsync(Path.Combine(evidence, "objects.json"), JsonSerializer.Serialize(fitted.Values, Pretty), ct);
        await File.WriteAllTextAsync(Path.Combine(evidence, "layout.json"), near.GetRawText(), ct);
        var path = Path.Combine(evidence, "reference.vsdx"); File.Delete(path);
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "reference.vsdx"; var exported = (await Call("export_document", export)).StructuredContent!.Value;
        await File.WriteAllTextAsync(Path.Combine(evidence, "export.json"), exported.GetRawText(), ct);
        using (var stream = File.OpenRead(path))
        {
            var imported = new NativeVsdxImporter().Import(stream, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
            var document = JsonSerializer.SerializeToElement(imported.Value.Document, ContractJson.Options);
            RoundTripSemantics(fitted, document.GetProperty("pages").EnumerateArray().Single(p => p.GetProperty("id").GetString() == pageId).GetProperty("elements"));
        }
        // Independent native Visio instance; original user documents are never opened or modified.
        dynamic? visio = OfficeAcceptanceTests.StartVisio();
        if (visio is null)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "verification.json"), JsonSerializer.Serialize(new {
                verifiedUtc = DateTime.UtcNow, sourceFixtureObjects = 62, exactTextObjects = 39, stableIds = 62,
                singleObjectPreservation = true, readOnlyInspection = true, nativeStatus = "NOT RUN: Visio is unavailable",
                nativeSaveReopenAndMcpRead = false, editor, displacedControl = displaced, missingDividerControl = missingRule,
                exportDiagnostics = exported.GetProperty("diagnostics"), baselineProvenance = "tests/fixtures/diagrams/r05-baselines/provenance.json"
            }, Pretty), ct);
            return;
        }
        var resaved = Path.Combine(evidence, "visio-saved.vsdx"); File.Delete(resaved);
        try
        {
            dynamic document = visio.Documents.Open(path);
            try
            {
                dynamic page = document.Pages.Item(2); Assert.Equal(62, (int)page.Shapes.Count);
                var nativeIds = OfficeAcceptanceTests.AllShapes((object)page.Shapes).Select(s => (string?)OfficeAcceptanceTests.AgentId(s)).ToArray(); Assert.Equal(ids.Order(), nativeIds.Order());
                page.Export(Path.Combine(evidence, "visio.png")); document.SaveAs(resaved);
            }
            finally { document.Saved = true; document.Close(); }
        }
        finally { visio.Quit(); }
        var native = RasterRegression.Compare(await File.ReadAllBytesAsync(Path.Combine(baseline, "visio.png"), ct), await File.ReadAllBytesAsync(Path.Combine(evidence, "visio.png"), ct));
        Assert.True(native.Pass, JsonSerializer.Serialize(native));
        using var savedStream = File.OpenRead(resaved); var nativeImported = new NativeVsdxImporter().Import(savedStream, new ImportOptions()); Assert.True(nativeImported.IsOk, nativeImported.Error?.Message);
        var nativeDoc = JsonSerializer.SerializeToElement(nativeImported.Value.Document, ContractJson.Options);
        RoundTripSemantics(fitted, nativeDoc.GetProperty("pages").EnumerateArray().Single(p => p.GetProperty("id").GetString() == pageId).GetProperty("elements"));
        var replace = await h.Send("doc.replace", new JsonObject { ["document"] = JsonNode.Parse(nativeDoc.GetRawText()), ["baseRevision"] = revision }); Assert.Null(replace.Error);
        identity = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        var reopened = await Read(); RoundTripSemantics(fitted, JsonSerializer.SerializeToElement(reopened.Values));
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.json"), JsonSerializer.Serialize(new {
            verifiedUtc = DateTime.UtcNow, sourceFixtureObjects = 62, exactTextObjects = 39, stableIds = 62,
            singleObjectPreservation = true, readOnlyInspection = true, nativeSaveReopenAndMcpRead = true,
            nativeStatus = "PASS", editor, native, displacedControl = displaced, missingDividerControl = missingRule, nativeImportDiagnostics = nativeImported.Value.Diagnostics,
            exportDiagnostics = exported.GetProperty("diagnostics"), baselineProvenance = "tests/fixtures/diagrams/r05-baselines/provenance.json"
        }, Pretty), ct);
    }
}
