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

            File.Delete(Path.Combine(root, "workspace.json")); // no remembered tabs: these sessions belong to no tab
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
            var first = await Harness.StartAsync(existingRoot: root);
            await using (first) { await Edit(first); }
            await WaitFor(async () => (await first.Window.Workspace.ReadAsync())?.CleanExit == true, 15); // Closed finishes its asynchronous metadata write before this orphan-session setup deletes it
            File.Delete(Path.Combine(root, "workspace.json")); // no remembered tabs: these sessions belong to no tab
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

    // ---------------------------------------------------------------- H01: a recovered session is recorded as recovered
    static string[] SessionDirs(string root) => Directory.Exists(Path.Combine(root, "recovery")) ? Directory.GetDirectories(Path.Combine(root, "recovery")).Select(d => Path.GetFileName(d)!).ToArray() : [];
    static string? RestoredFrom(string root, string session) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "recovery", session, "session.json")))?["restoredFrom"]?.GetValue<string>();
    static async Task<string[]> ElementIds(Harness h) => (await Snap(h)).Document.Pages[0].Elements.Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!["id"]!.GetValue<string>()).OrderBy(x => x).ToArray();

    [Fact]
    public async Task RecoveredWorkThatIsNotEditedBeforeAnotherCrashIsOfferedOnceMoreAndOnlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-h01a-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] ids; string original;
            await using (var first = await Harness.StartAsync(existingRoot: root))
            {
                ids = await Edit(first);
                original = Assert.Single(SessionDirs(root));
                await first.Window.RecoveryMgr.DisposeAsync();            // crash 1
            }
            File.Delete(Path.Combine(root, "workspace.json"));

            string restoredSession;
            await using (var second = await Harness.StartAsync(existingRoot: root))
            {
                await WaitFor(async () => (await second.Eval("document.querySelector('[data-testid=recovery-offer]') !== null")) == "true", 30);
                Assert.Equal(1, int.Parse(await second.Eval("document.querySelectorAll('[data-testid=recovery-item]').length")));
                await second.Eval("document.querySelector('[data-testid=recovery-restore]').click(); true");
                await WaitFor(async () => await Count(second) == 3, 30);
                await WaitFor(() => SessionDirs(root).Length == 1 && SessionDirs(root)[0] != original, 30);   // the old session is gone, the new one holds the work
                restoredSession = Assert.Single(SessionDirs(root));
                Assert.Equal(original, RestoredFrom(root, restoredSession));                                      // and says what it replaces
                await second.Window.RecoveryMgr.DisposeAsync();                                                   // crash 2, nothing edited since the restore
            }
            File.Delete(Path.Combine(root, "workspace.json"));

            await using (var third = await Harness.StartAsync(existingRoot: root))
            {
                await WaitFor(async () => (await third.Eval("document.querySelector('[data-testid=recovery-offer]') !== null")) == "true", 30);
                Assert.Equal(1, int.Parse(await third.Eval("document.querySelectorAll('[data-testid=recovery-item]').length")));                  // one offer, not two
                Assert.Contains("not changed since", await third.Eval("document.querySelector('[data-testid=recovery-restored-earlier]')?.textContent ?? ''"));  // and it says so
                await third.Eval("document.querySelector('[data-testid=recovery-restore]').click(); true");
                await WaitFor(async () => await Count(third) == 3, 30);
                Assert.Equal(ids.OrderBy(x => x), await ElementIds(third));                                       // the very same objects, twice recovered
                await WaitFor(() => SessionDirs(root).Length == 1 && SessionDirs(root)[0] != restoredSession, 30);
                Assert.Equal(restoredSession, RestoredFrom(root, Assert.Single(SessionDirs(root))));
                Assert.True((await third.Send("doc.status")).Result!.Value.GetProperty("dirty").GetBoolean());    // still unsaved until it is saved
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ACrashBeforeTheOldSessionIsRemovedLeavesOneRestoredTabAndNoSecondOffer()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-h01b-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(Path.GetTempPath(), "diagram-win-h01b-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] ids; string original;
            await using (var first = await Harness.StartAsync(existingRoot: root))
            {
                ids = await Edit(first);
                original = Assert.Single(SessionDirs(root));
                await WaitFor(() => File.Exists(Path.Combine(root, "workspace.json")) && File.ReadAllText(Path.Combine(root, "workspace.json")).Contains(original), 20); // the tab remembers its session
                await first.Window.RecoveryMgr.DisposeAsync();            // crash 1
            }
            CopyDirectory(Path.Combine(root, "recovery", original), backup);

            string restoredSession;
            await using (var second = await Harness.StartAsync(existingRoot: root))
            {
                await WaitFor(async () => await Count(second) == 3, 60);                                          // the remembered tab came back by itself
                await WaitFor(() => SessionDirs(root).Length == 1 && SessionDirs(root)[0] != original, 30);
                restoredSession = Assert.Single(SessionDirs(root));
                Assert.Equal(original, RestoredFrom(root, restoredSession));
                await WaitFor(() => File.ReadAllText(Path.Combine(root, "workspace.json")).Contains(restoredSession), 20);
                await second.Window.RecoveryMgr.DisposeAsync();                                                   // crash 2
            }
            // as if the crash had come before the old session was removed and before the tab list was rewritten
            CopyDirectory(backup, Path.Combine(root, "recovery", original));
            File.WriteAllText(Path.Combine(root, "workspace.json"), File.ReadAllText(Path.Combine(root, "workspace.json")).Replace(restoredSession, original));
            Assert.Equal(2, SessionDirs(root).Length);

            await using var third = await Harness.StartAsync(existingRoot: root);
            await WaitFor(async () => await Count(third) == 3, 60);                                               // the tab followed its work to the new session
            Assert.Equal(ids.OrderBy(x => x), await ElementIds(third));
            Assert.Equal("false", await third.Eval("document.querySelector('[data-testid=recovery-offer]') !== null"));      // nothing offered a second time
            await WaitFor(() => SessionDirs(root).Length == 1 && !SessionDirs(root).Contains(original) && !SessionDirs(root).Contains(restoredSession), 30);
            Assert.Equal(restoredSession, RestoredFrom(root, Assert.Single(SessionDirs(root))));
            Assert.True((await third.Send("doc.status")).Result!.Value.GetProperty("dirty").GetBoolean());
        }
        finally { try { Directory.Delete(root, true); } catch { } try { Directory.Delete(backup, true); } catch { } }
    }

    /// <summary>Run 1 makes three objects and crashes; run 2 recovers them and, optionally, does something to the recovered document before crashing again.</summary>
    static async Task<(string[] Ids, string Restored)> CrashRestoreCrash(string root, Func<Harness, Task>? afterRestore = null)
    {
        string[] ids;
        await using (var first = await Harness.StartAsync(existingRoot: root))
        {
            ids = await Edit(first);
            await first.Window.RecoveryMgr.DisposeAsync();
        }
        File.Delete(Path.Combine(root, "workspace.json"));
        await using var second = await Harness.StartAsync(existingRoot: root);
        await WaitFor(async () => (await second.Eval("document.querySelector('[data-testid=recovery-offer]') !== null")) == "true", 30);
        await second.Eval("document.querySelector('[data-testid=recovery-restore]').click(); true");
        await WaitFor(async () => await Count(second) == 3, 30);
        await WaitFor(() => SessionDirs(root).Length == 1, 30);
        if (afterRestore is not null) await afterRestore(second);
        var restored = Assert.Single(SessionDirs(root));
        await second.Window.RecoveryMgr.DisposeAsync();
        return (ids, restored);
    }

    [Fact]
    public async Task RecoveredWorkThatWasEditedBeforeAnotherCrashComesBackWithTheEditAndIsLabelledChanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-h01c-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (ids, restored) = await CrashRestoreCrash(root, async h =>
            {
                var before = (await Snap(h)).Revision;
                var r = await h.Apply(before, null, Harness.Shape("after-restore", 300, 200));
                Assert.Null(r.Error);
                await WaitFor(() => h.Window.RecoveryMgr.Status.LastDurableRevision >= before + 1, 30);          // the edit is on disk
                await h.Window.RecoveryMgr.DrainAsync();
            });
            File.Delete(Path.Combine(root, "workspace.json"));
            await using var third = await Harness.StartAsync(existingRoot: root);
            await WaitFor(async () => (await third.Eval("document.querySelector('[data-testid=recovery-offer]') !== null")) == "true", 30);
            Assert.Equal(1, int.Parse(await third.Eval("document.querySelectorAll('[data-testid=recovery-item]').length")));
            Assert.Contains("changed since", await third.Eval("document.querySelector('[data-testid=recovery-restored-earlier]')?.textContent ?? ''"));
            Assert.DoesNotContain("not changed since", await third.Eval("document.querySelector('[data-testid=recovery-restored-earlier]')?.textContent ?? ''"));
            await third.Eval("document.querySelector('[data-testid=recovery-restore]').click(); true");
            await WaitFor(async () => await Count(third) == 4, 30);                                        // the three recovered objects and the one added after the restore
            Assert.Equal(ids.OrderBy(x => x), (await ElementIds(third)).Where(id => ids.Contains(id)).OrderBy(x => x));
            await WaitFor(() => SessionDirs(root).Length == 1 && SessionDirs(root)[0] != restored, 30);
            Assert.Equal(restored, RestoredFrom(root, Assert.Single(SessionDirs(root))));                   // the chain goes on: this session replaces the one that was edited
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task DiscardingRecoveredWorkLeavesNothingToOfferOnTheNextStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-h01d-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CrashRestoreCrash(root);
            File.Delete(Path.Combine(root, "workspace.json"));
            await using (var third = await Harness.StartAsync(existingRoot: root))
            {
                await WaitFor(async () => (await third.Eval("document.querySelector('[data-testid=recovery-offer]') !== null")) == "true", 30);
                await third.Eval("document.querySelector('[data-testid=recovery-discard]').click(); true");
                await WaitFor(async () => (await third.Eval("document.querySelector('[data-testid=recovery-offer]') === null")) == "true", 30);
                await WaitFor(() => SessionDirs(root).Length <= 1, 30);                                        // only the blank session of this very run may remain
            }
            await WaitFor(() => SessionDirs(root).Length == 0, 30);                                            // and a clean close removes that too
            File.Delete(Path.Combine(root, "workspace.json"));
            await using var fourth = await Harness.StartAsync(existingRoot: root);
            await Task.Delay(3000);
            Assert.Equal("false", await fourth.Eval("document.querySelector('[data-testid=recovery-offer]') !== null"));  // nothing comes back
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static Task WaitFor(Func<Task<bool>> condition, int seconds = 30) => WaitForAsync(condition, seconds);
    static async Task WaitForAsync(Func<Task<bool>> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(150); }
    }
}
