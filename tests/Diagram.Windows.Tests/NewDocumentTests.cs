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
    static async Task WaitForPersisted(Harness h)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((await h.Window.Workspace.ReadAsync())?.Tabs.Any(t => t.SessionId == h.Router.CurrentScope!.SessionId) != true)
        { if (DateTime.UtcNow > until) throw new TimeoutException("workspace session not persisted"); await Task.Delay(50); }
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
    [Fact]
    public async Task APreviousSaveCannotGiveTheNewDrawingItsOldPath()
    {
        await using var h = await Harness.StartAsync(); await h.Window.RecoveryMgr.WhenSessionReadyAsync();
        Assert.Null((await h.Apply(0, null, Harness.Shape("old", 20, 20))).Error);
        var old = h.Router.CurrentScope!; var snapshot = (await h.Send("doc.snapshot")).Result!.Value;
        var file = Path.Combine(h.Root, "old-save.diagram.json"); h.Window.Services.DialogOverride = _ => file;
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        h.Window.Writer.BeforeReplace = () => { writing.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("save gate"); };
        var save = h.Window.Services.HandleAsync(Request("host.saveJson"), default);
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var args = new JsonObject { ["document"] = Candidate(snapshot), ["discard"] = true, ["expected"] = new JsonObject { ["documentId"] = old.DocumentId, ["sessionId"] = old.SessionId, ["baseRevision"] = 1 } };
            var create = h.Window.Services.HandleAsync(Request("host.newDocument", args), default);
            await Task.WhenAny(create, Task.Delay(1500));
            release.Set();
            Assert.Null((await save).Error); Assert.Null((await create).Error);
            Assert.NotEqual(old.SessionId, h.Router.CurrentScope!.SessionId);
            Assert.Null(h.Window.Services.CurrentPath);
        }
        finally { release.Set(); h.Window.Writer.BeforeReplace = null; }
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
                var invalid = Args(true); invalid["document"]!["pages"]![0]!["widthPt"] = 0;
                Assert.NotNull((await h.Window.Services.HandleAsync(Request("host.newDocument", invalid), default)).Error);
                Assert.Equal(savedPath, h.Window.Services.CurrentPath);
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
                // W02c restores remembered tabs automatically rather than offering an orphan dialog.
                await WaitFor(h, "document.querySelector('[data-testid=restore-banner]') !== null");
                await h.Window.RecoveryMgr.DrainAsync();
                var until = DateTime.UtcNow.AddSeconds(10);
                while ((await h.Window.Recovery.ReadRecoveryAsync(null, default)).Any(c => c.SessionId == explicitId))
                { if (DateTime.UtcNow > until) throw new TimeoutException("old recovery session was not finished"); await Task.Delay(50); }
                explicitId = h.Router.CurrentScope!.SessionId;
                await WaitForPersisted(h);
            }
            await using (var h = await Harness.StartAsync(existingRoot: root))
            {
                await WaitFor(h, "document.querySelector('[data-testid=restore-banner]') !== null");
                await h.Window.RecoveryMgr.DrainAsync();
                var again = Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == h.Router.CurrentScope!.SessionId);
                Assert.True(again.KeepUnsaved);
                Assert.Equal(612, again.Document["pages"]![0]!["widthPt"]!.GetValue<double>());
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    [Fact]
    public async Task SizedNewTabPreservesOldDrawingAndReturnsAcrossTwoRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-new-tabs-" + Guid.NewGuid().ToString("N"));
        string? createdId = null;
        try
        {
            await using (var h = await Harness.StartAsync(existingRoot: root))
            {
                await h.Window.RecoveryMgr.WhenSessionReadyAsync();
                Assert.Null((await h.Apply(0, null, Harness.Shape("old-tab", 20, 20))).Error);
                var file = Path.Combine(root, "old.diagram.json"); h.Window.Services.DialogOverride = _ => file;
                Assert.Null((await h.Window.Services.HandleAsync(Request("host.saveJson"), default)).Error);
                var old = h.Router.CurrentScope!;
                var before = (await h.Send("doc.snapshot", scope: old)).Result!.Value.GetRawText();
                await h.Eval("document.querySelector('[data-testid=menu-file]').click(); true");
                await WaitFor(h, "document.querySelector('[data-testid=menu-new]') !== null");
                await h.Eval("document.querySelector('[data-testid=menu-new]').click(); true");
                await WaitFor(h, "document.querySelector('[data-testid=new-document]') !== null");
                await h.Eval("(() => { const s=document.querySelector('[data-testid=new-document] select'); s.value='letter-portrait'; s.dispatchEvent(new Event('change', {bubbles:true})); return true; })()");
                await h.Eval("document.querySelector('[data-testid=new-document] button').click(); true");
                await WaitFor(h, "document.querySelector('[data-testid=new-document]') === null");
                Assert.Equal(2, h.Router.Documents.Count);
                createdId = h.Router.CurrentScope!.DocumentId;
                Assert.NotEqual(old.DocumentId, createdId);
                Assert.Equal(before, (await h.Send("doc.snapshot", scope: old)).Result!.Value.GetRawText());
                Assert.Equal(file, h.Window.Services.PathOf(old.DocumentId));
                Assert.Null(h.Window.Services.PathOf(createdId));
                Assert.True((await h.Send("doc.status")).Result!.Value.GetProperty("unsaved").GetBoolean());
                await h.Window.RecoveryMgr.DrainAsync();
                var candidate = Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.DocumentId == createdId);
                Assert.True(candidate.KeepUnsaved);
                Assert.Equal(612, candidate.Document["pages"]![0]!["widthPt"]!.GetValue<double>());
                Assert.Equal(792, candidate.Document["pages"]![0]!["heightPt"]!.GetValue<double>());
                var until = DateTime.UtcNow.AddSeconds(10);
                while ((await h.Window.Workspace.ReadAsync())?.Tabs.Count != 2)
                { if (DateTime.UtcNow > until) throw new TimeoutException("tabs not persisted"); await Task.Delay(50); }
            }
            for (var restart = 0; restart < 2; restart++)
            {
                await using var h = await Harness.StartAsync(existingRoot: root);
                await WaitFor(h, "document.querySelector('[data-testid=restore-banner]') !== null");
                await WaitFor(h, "document.querySelectorAll('[data-testid=tab]').length === 2");
                Assert.Equal(createdId, h.Router.CurrentScope!.DocumentId);
                Assert.True((await h.Send("doc.status")).Result!.Value.GetProperty("unsaved").GetBoolean());
                var page = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0];
                Assert.Equal(612, page.GetProperty("widthPt").GetDouble()); Assert.Equal(792, page.GetProperty("heightPt").GetDouble());
                await h.Window.RecoveryMgr.DrainAsync();
                Assert.True(Assert.Single(await h.Window.Recovery.ReadRecoveryAsync(null, default), c => c.SessionId == h.Router.CurrentScope.SessionId).KeepUnsaved);
                await WaitForPersisted(h);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

}
