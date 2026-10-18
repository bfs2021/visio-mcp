using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Xunit;

namespace Diagram.Windows.Tests;

public class NewDocumentTests
{
    static async Task WaitFor(Harness h, string expression)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (await h.Eval(expression) != "true") { if (DateTime.UtcNow > until) throw new TimeoutException(expression); await Task.Delay(50); }
    }
    [Fact]
    public async Task FileNewRunsThroughTheRealPageAndHostAndClearsSavedPath()
    {
        await using var h = await Harness.StartAsync();
        await h.Window.RecoveryMgr.WhenSessionReadyAsync();
        Assert.Null((await h.Apply(0, null, Harness.Shape("saved", 20, 20))).Error);
        var file = Path.Combine(h.Root, "saved.diagram.json"); h.Window.Services.DialogOverride = _ => file;
        Assert.Null((await h.Window.Services.HandleAsync(Request("host.saveJson"), default)).Error);
        var old = h.Router.CurrentScope!;
        await h.Eval("document.querySelector('[data-testid=menu-file]').click(); true");
        await WaitFor(h, "document.querySelector('[data-testid=menu-new]') !== null");
        await h.Eval("document.querySelector('[data-testid=menu-new]').click(); true");
        await WaitFor(h, "document.querySelector('[data-testid=new-document]') !== null");
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/w03-new-document"); Directory.CreateDirectory(evidence);
        await File.WriteAllBytesAsync(Path.Combine(evidence, "new-picker.png"), await h.OnUi(() => h.Host.CapturePreviewAsync()));
        await h.Eval("document.querySelector('[data-testid=new-document] button').click(); true");
        await WaitFor(h, "document.querySelector('[data-testid=new-document]') === null");
        Assert.NotEqual(old.SessionId, h.Router.CurrentScope!.SessionId); Assert.Null(h.Window.Services.CurrentPath);
        Assert.True((await h.Send("doc.status")).Result!.Value.GetProperty("unsaved").GetBoolean());
        Assert.Equal("● Untitled - Diagram", JsonSerializer.Deserialize<string>(await h.Eval("document.title")));
        var state = (await h.Send("doc.snapshot")).Result!.Value;
        Assert.Empty(state.GetProperty("document").GetProperty("pages")[0].GetProperty("elements").EnumerateArray());
        await File.WriteAllTextAsync(Path.Combine(evidence, "ui-created.json"), state.GetRawText());
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.json"), JsonSerializer.Serialize(new { verifiedUtc = DateTime.UtcNow, realPageAndHost = true, newIdentity = old.SessionId != h.Router.CurrentScope.SessionId, savedPathCleared = h.Window.Services.CurrentPath is null, explicitBlankUnsaved = true, page = "A4 landscape" }, new JsonSerializerOptions { WriteIndented = true }));
    }
    static RequestEnvelope Request(string method, JsonObject? args = null) => new() { RequestId = Guid.NewGuid().ToString(), Method = method, Params = args ?? new() };
    static JsonObject Candidate(JsonElement snapshot)
    {
        var doc = JsonNode.Parse(snapshot.GetProperty("document").GetRawText())!.AsObject();
        doc["id"] = Guid.NewGuid().ToString(); doc["revision"] = 0; doc["title"] = "Untitled"; doc.Remove("source"); doc["assets"] = new JsonArray();
        var page = doc["pages"]![0]!; page["id"] = Guid.NewGuid().ToString(); page["elements"] = new JsonArray(); page["widthPt"] = 612; page["heightPt"] = 792;
        page["layers"]![0]!["id"] = Guid.NewGuid().ToString();
        return doc;
    }
    [Fact]
    public async Task NewGuardsRevisionAndConsentAndPersistsExplicitBlankRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-new-" + Guid.NewGuid().ToString("N"));
        string? explicitId = null;
        try
        {
            await using (var h = await Harness.StartAsync(existingRoot: root))
            {
                await h.Window.RecoveryMgr.WhenSessionReadyAsync();
                Assert.Null((await h.Apply(0, null, Harness.Shape("original", 20, 20))).Error);
                var savedPath = Path.Combine(root, "old.diagram.json"); h.Window.Services.DialogOverride = _ => savedPath;
                Assert.Null((await h.Window.Services.HandleAsync(Request("host.saveJson"), default)).Error);
                var fileBefore = await File.ReadAllBytesAsync(savedPath);
                Assert.Null((await h.Apply(1, null, Harness.Shape("newer", 150, 20))).Error);
                var old = h.Router.CurrentScope!; var snapshot = (await h.Send("doc.snapshot")).Result!.Value;
                var candidate = Candidate(snapshot);
                JsonObject Args(bool discard, int revision = 2) => new() { ["document"] = candidate.DeepClone(), ["discard"] = discard, ["expected"] = new JsonObject { ["documentId"] = old.DocumentId, ["sessionId"] = old.SessionId, ["baseRevision"] = revision } };
                var denied = await h.Window.Services.HandleAsync(Request("host.newDocument", Args(false)), default);
                Assert.Equal("invalid_request", denied.Error?.Code);
                var stale = await h.Window.Services.HandleAsync(Request("host.newDocument", Args(true, 1)), default);
                Assert.Equal("revision_conflict", stale.Error?.Code);
                Assert.Equal(snapshot.GetRawText(), (await h.Send("doc.snapshot")).Result!.Value.GetRawText());
                var created = await h.Window.Services.HandleAsync(Request("host.newDocument", Args(true)), default);
                Assert.Null(created.Error); Assert.False(created.Result!.Value.TryGetProperty("recoveryWarning", out _));
                Assert.Null(h.Window.Services.CurrentPath); Assert.NotEqual(old.SessionId, h.Router.CurrentScope!.SessionId);
                explicitId = h.Router.CurrentScope.SessionId;
                await h.Window.RecoveryMgr.DrainAsync();
                Assert.DoesNotContain(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == old.SessionId);
                var fresh = Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == explicitId);
                Assert.True(fresh.KeepUnsaved); Assert.Null(fresh.OriginalPath); Assert.Equal(0, fresh.LastDurableRevision);
                Assert.Equal(fileBefore, await File.ReadAllBytesAsync(savedPath));
                Assert.NotNull((await h.Send("doc.snapshot", scope: old)).Error);
                var status = (await h.Send("doc.status")).Result!.Value; Assert.True(status.GetProperty("unsaved").GetBoolean());
                Assert.Contains("Untitled", await h.Eval("document.title"));
            }
            await using (var h = await Harness.StartAsync(existingRoot: root))
            {
                var candidate = Assert.Single(await h.Window.RecoveryMgr.CandidatesAsync(default), c => c.SessionId == explicitId);
                Assert.Equal(612, candidate.Document["pages"]![0]!["widthPt"]!.GetValue<double>());
                Assert.Equal(792, candidate.Document["pages"]![0]!["heightPt"]!.GetValue<double>());
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}


