using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>The recovery journal on the real stack: a renderer that dies mid-session, and an application that dies and is started again.</summary>
public class CrashRecoveryTests
{
    static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(100); }
    }

    static async Task<Snapshot> Snap(Harness h)
    {
        var r = await h.Send("doc.snapshot");
        if (r.Result is not { } v) throw new InvalidOperationException($"doc.snapshot failed: {r.Error?.Code}: {r.Error?.Message}");
        return v.Deserialize<Snapshot>(ContractJson.Options)!;
    }

    /// <summary>Counts the elements, treating a moment of scope change (the document being replaced) as not yet.</summary>
    static async Task<int> Count(Harness h)
    {
        try { return (await Snap(h)).Document.Pages[0].Elements.Count; } catch (InvalidOperationException) { return -1; }
    }

    static async Task WaitDurable(Harness h, int revision)
    {
        await WaitFor(() => h.Window.RecoveryMgr.Status.LastDurableRevision >= revision);
        await h.Window.RecoveryMgr.DrainAsync();
    }

    /// <summary>Makes three committed edits and waits until they are on disk.</summary>
    static async Task<string[]> Edit(Harness h)
    {
        await WaitFor(() => h.Window.RecoveryMgr.SessionId == h.Router.CurrentScope?.SessionId);
        var r1 = await h.Apply(0, null, Harness.Shape("crm", 20, 20), Harness.Shape("erp", 200, 20));
        Assert.Null(r1.Error);
        var r2 = await h.Apply(1, null, new JsonObject { ["op"] = "createConnector", ["from"] = new JsonObject { ["elementId"] = "crm", ["glue"] = "dynamic" }, ["to"] = new JsonObject { ["elementId"] = "erp", ["glue"] = "dynamic" } });
        Assert.Null(r2.Error);
        var r3 = await h.Apply(2, null, new JsonObject { ["op"] = "move", ["target"] = "crm", ["delta"] = new JsonObject { ["xPt"] = 33, ["yPt"] = 11 } });
        Assert.Null(r3.Error);
        await WaitDurable(h, 3);
        return (await Snap(h)).Document.Pages[0].Elements.Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!["id"]!.GetValue<string>()).ToArray();
    }

    [Fact]
    public async Task ARendererThatDiesIsRestoredToTheLastDurableStateAndTheOldAgentSessionIsRefused()
    {
        await using var h = await Harness.StartAsync();
        var ids = await Edit(h);
        var oldSession = h.Router.CurrentScope!.SessionId;
        var oldDoc = JsonSerializer.Serialize((await Snap(h)).Document, ContractJson.Options);
        Assert.Equal(3, h.Window.RecoveryMgr.Status.LastDurableRevision);

        await h.OnUi(async () => { _ = h.Host.FireDevToolsAsync("Page.crash"); await Task.Delay(300); return 0; });
        await WaitFor(() => h.Host.RendererFailures >= 1);

        // the page reloads, finds the unfinished session, restores it at once, and starts a new session of its own
        await WaitFor(() => h.Router.CurrentScope is { } s && s.SessionId != oldSession, 60);
        await WaitFor(async () => await Count(h) == 3, 60);
        var restored = await Snap(h);
        Assert.Equal(ids.OrderBy(x => x), restored.Document.Pages[0].Elements.Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!["id"]!.GetValue<string>()).OrderBy(x => x)); // the same objects, the same ids
        Assert.Equal(oldDoc.Replace("\"revision\":3", "\"revision\":" + restored.Revision), JsonSerializer.Serialize(restored.Document, ContractJson.Options)); // byte for byte, apart from the revision counter
        Assert.True((await h.Send("doc.status")).Result!.Value.GetProperty("dirty").GetBoolean()); // recovered work is unsaved until it is saved

        // an agent still holding the old session is refused; it re-reads and carries on
        var stale = await h.Send("doc.snapshot", scope: new Scope { DocumentId = restored.DocumentId, SessionId = oldSession });
        Assert.Equal("session_mismatch", stale.Error!.Code);
        var again = await h.Send("doc.apply", new JsonObject
        {
            ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = restored.Revision, ["atomic"] = true,
            ["operations"] = new JsonArray(Harness.Shape("after-crash", 300, 200)),
        });
        Assert.Null(again.Error);

        // the new session has its own checkpoint, the old one was removed only after that, and the work stays protected
        await h.Window.RecoveryMgr.FinishRestoreAsync(oldSession);
        await WaitFor(() => h.Window.RecoveryMgr.Status.LastDurableRevision >= restored.Revision + 1);
        var sessions = Directory.GetDirectories(Path.Combine(h.Root, "recovery")).Select(Path.GetFileName).ToArray();
        Assert.Equal([h.Router.CurrentScope!.SessionId], sessions);
    }

    [Fact]
    public async Task AnApplicationThatDiesIsOfferedItsUnsavedWorkOnTheNextStartAndDiscardRemovesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-restart-" + Guid.NewGuid().ToString("N"));
        string[] ids;
        try
        {
            await using (var first = await Harness.StartAsync(existingRoot: root))
            {
                ids = await Edit(first);
                // the application is killed here: no clean close, no save
                await first.Window.RecoveryMgr.DisposeAsync();
            }
            // the window closing runs the clean-close path, which must keep an unsaved session
            var left = Directory.GetDirectories(Path.Combine(root, "recovery"));
            Assert.Single(left);

            await using var second = await Harness.StartAsync(existingRoot: root);
            await WaitFor(() => second.Window.RecoveryMgr.SessionId == second.Router.CurrentScope?.SessionId);
            // the page asks at start and shows a question; nothing has been restored without an answer
            await WaitFor(async () => (await second.Eval("document.querySelector('[data-testid=recovery-offer]') !== null")) == "true", 30);
            Assert.Empty((await Snap(second)).Document.Pages[0].Elements);
            var offer = await second.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "c", Method = "host.recovery.candidate", Params = new JsonObject() }, default);
            Assert.True(offer.Result!.Value.GetProperty("found").GetBoolean());
            Assert.Equal(3, offer.Result!.Value.GetProperty("lastDurableRevision").GetInt32());
            Assert.False(offer.Result!.Value.GetProperty("auto").GetBoolean());

            // saying yes brings back the same objects and leaves the document dirty
            await second.Eval("document.querySelector('[data-testid=recovery-restore]').click(); true");
            await WaitFor(async () => await Count(second) == 3, 30);
            Assert.Equal(ids.OrderBy(x => x), (await Snap(second)).Document.Pages[0].Elements.Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!["id"]!.GetValue<string>()).OrderBy(x => x));
            Assert.True((await second.Send("doc.status")).Result!.Value.GetProperty("dirty").GetBoolean());
            await WaitFor(async () => (await second.Eval("document.querySelector('[data-testid=recovery-report]') !== null")) == "true", 30);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task DiscardingRemovesTheUnfinishedSessionAndACleanCloseLeavesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-discard-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = await Harness.StartAsync(existingRoot: root)) { await Edit(first); }
            await using (var second = await Harness.StartAsync(existingRoot: root))
            {
                await WaitFor(async () => (await second.Eval("document.querySelector('[data-testid=recovery-offer]') !== null")) == "true", 30);
                await second.Eval("document.querySelector('[data-testid=recovery-discard]').click(); true");
                await WaitFor(() => Directory.GetDirectories(Path.Combine(root, "recovery")).Length <= 1, 30);
            }
            // nothing was edited in the second run, so closing it left nothing behind either
            await WaitFor(() => !Directory.Exists(Path.Combine(root, "recovery")) || Directory.GetDirectories(Path.Combine(root, "recovery")).Length == 0, 30);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static Task WaitFor(Func<Task<bool>> condition, int seconds = 30) => WaitForAsync(condition, seconds);
    static async Task WaitForAsync(Func<Task<bool>> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(150); }
    }
}
