using System.IO;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>W02c: the open tabs come back at the next start, after a normal exit and after a crash.</summary>
public class WorkspaceRestoreTests
{
    static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(100); }
    }

    static Task Click(Harness h, string testId, int index = 0) => h.Eval($"document.querySelectorAll('[data-testid={testId}]')[{index}].click(); true");
    static async Task<int> Count(Harness h, string testId) => int.Parse(await h.Eval($"document.querySelectorAll('[data-testid={testId}]').length"));
    static async Task<string> Text(Harness h, string testId) => await h.Eval($"(document.querySelector('[data-testid={testId}]') || {{innerText: ''}}).innerText");
    static string WorkspaceFile(string root) => Path.Combine(root, "workspace.json");

    /// <summary>Two tabs: the first has unsaved work ("one"), the second was saved to a file ("two"). The first is showing.</summary>
    static async Task<(Harness h, string root, string file)> StartWithTwoTabs()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-ws-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "two.diagram.json");
        Directory.CreateDirectory(root);
        var h = await Harness.StartAsync(existingRoot: root);
        await WaitFor(() => h.Window.RecoveryMgr.SessionId == h.Router.CurrentScope?.SessionId);
        Assert.Null((await h.Apply(0, null, Harness.Shape("one", 20, 20))).Error);
        await Click(h, "tab-new");
        await WaitFor(() => h.Router.Documents.Count == 2 && h.Window.RecoveryMgr.SessionId == h.Router.CurrentScope?.SessionId);
        Assert.Null((await h.Apply(0, null, Harness.Shape("two", 40, 40))).Error);
        h.Window.Services.DialogOverride = _ => file;
        var saved = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "s", Method = "host.saveJson", Params = new JsonObject { ["saveAs"] = true } }, default);
        Assert.Null(saved.Error);
        await Click(h, "tab", 0);
        await WaitFor(() => h.Window.RecoveryMgr.Status.LastDurableRevision >= 1);
        await h.Window.RecoveryMgr.DrainAsync();
        // the page has told the host about both tabs, the unsaved one first and showing
        await WaitFor(() => File.Exists(WorkspaceFile(root)) && File.ReadAllText(WorkspaceFile(root)).Contains("two.diagram.json") && File.ReadAllText(WorkspaceFile(root)).Contains("\"unsaved\": true"));
        await WaitFor(() => JsonNode.Parse(File.ReadAllText(WorkspaceFile(root)))!["activeDocumentId"]?.GetValue<string>() == h.Router.CurrentScope!.DocumentId);
        return (h, root, file);
    }

    static async Task<string> SnapshotText(Harness h, Scope scope) => (await h.Send("doc.snapshot", scope: scope)).Result!.Value.GetRawText();

    [Fact]
    public async Task AfterACrashBothTabsComeBackAndTheUnsavedOneIsStillUnsaved()
    {
        var (first, root, file) = await StartWithTwoTabs();
        Harness? second = null;
        try
        {
            var remembered = (await first.Window.Workspace.ReadAsync())!;
            Assert.False(remembered.CleanExit); // while running, the file says the app has not exited
            await first.Window.RecoveryMgr.DisposeAsync(); // the application dies with unsaved work
            await first.DisposeAsync();
            await first.Window.Workspace.WriteAsync(remembered.Tabs, remembered.ActiveDocumentId); // a real crash would not have run the shutdown that marks a clean exit

            second = await Harness.StartAsync(existingRoot: root);
            await WaitFor(() => Count(second, "tab").GetAwaiter().GetResult() == 2);
            await WaitFor(() => Count(second, "restore-banner").GetAwaiter().GetResult() == 1);
            Assert.Contains("after an unexpected exit", await Text(second, "restore-banner"));
            Assert.Equal(0, await Count(second, "recovery-offer")); // nothing is asked
            await WaitFor(() => second.Router.Documents.Count == 2);

            var docs = second.Router.Documents;
            var snapshots = new List<string>();
            foreach (var d in docs) snapshots.Add(await SnapshotText(second, d));
            Assert.Single(snapshots, s => s.Contains("\"one\""));  // recovered from its journal
            Assert.Single(snapshots, s => s.Contains("\"two\""));  // reopened from its file
            Assert.Equal(file, second.Window.Services.PathOf(docs.Single(d => snapshots[docs.ToList().IndexOf(d)].Contains("\"two\"")).DocumentId));
            // the one with unsaved work is showing, as it was
            Assert.Contains("\"one\"", await SnapshotText(second, second.Router.CurrentScope!));
            var labels = await second.Eval("[...document.querySelectorAll('[data-testid=tab]')].map(t => t.dataset.unsaved).join(',')");
            Assert.Equal("true,false", labels.Trim('"'));
        }
        finally { if (second is not null) await second.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task AfterANormalExitTheWindowClosesWithoutAskingAndTheNextStartReopensEverything()
    {
        var (first, root, _) = await StartWithTwoTabs();
        Harness? second = null;
        try
        {
            var closed = new TaskCompletionSource();
            await first.OnUi(() => { first.Window.Closed += (_, _) => closed.TrySetResult(); first.Window.Close(); return Task.FromResult(0); });
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30)); // no question: the work is kept
            Assert.Equal(0, 0);
            await WaitFor(() => (first.Window.Workspace.ReadAsync().GetAwaiter().GetResult()?.CleanExit) == true, 15);

            second = await Harness.StartAsync(existingRoot: root);
            await WaitFor(() => Count(second, "tab").GetAwaiter().GetResult() == 2);
            await WaitFor(() => Count(second, "restore-banner").GetAwaiter().GetResult() == 1);
            Assert.Contains("Reopened 2 documents, 1 with unsaved changes", await Text(second, "restore-banner"));
            Assert.Equal(0, await Count(second, "recovery-offer"));
            await WaitFor(() => second.Router.Documents.Count == 2);
        }
        finally { if (second is not null) await second.DisposeAsync(); await first.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ASessionNoTabOwnsIsStillOfferedAndOpensAsATab()
    {
        var (first, root, _) = await StartWithTwoTabs();
        Harness? second = null;
        try
        {
            var remembered = (await first.Window.Workspace.ReadAsync())!;
            await first.Window.RecoveryMgr.DisposeAsync();
            await first.DisposeAsync();
            // the workspace file forgets the unsaved tab (it was lost), but its recovery session is still on disk
            await first.Window.Workspace.WriteAsync(remembered.Tabs.Where(t => !t.Unsaved).ToList(), null);

            second = await Harness.StartAsync(existingRoot: root);
            await WaitFor(() => Count(second, "recovery-offer").GetAwaiter().GetResult() == 1);
            Assert.Equal(1, await Count(second, "tab")); // only the saved file came back
            await Click(second, "recovery-restore");
            await WaitFor(() => Count(second, "tab").GetAwaiter().GetResult() == 2);
            await WaitFor(() => second.Router.Documents.Count == 2);
        }
        finally { if (second is not null) await second.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ThePageCanOnlyReopenFilesTheLastSessionListed()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-ws-" + Guid.NewGuid().ToString("N"));
        var h = await Harness.StartAsync(existingRoot: root);
        try
        {
            var r = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "o", Method = "host.openJson", Params = new JsonObject { ["path"] = Path.Combine(root, "elsewhere.diagram.json") } }, default);
            Assert.Equal("invalid_request", r.Error!.Code);
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }
}
