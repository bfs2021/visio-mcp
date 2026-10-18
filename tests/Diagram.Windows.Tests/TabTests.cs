using System.IO;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>W02b: several documents in the real window, closed one at a time and all at once.</summary>
public class TabTests
{
    static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(100); }
    }

    static async Task<bool> Shown(Harness h, string testId) => await h.Eval($"document.querySelector('[data-testid={testId}]') !== null") == "true";
    static Task Click(Harness h, string testId, int index = 0) => h.Eval($"document.querySelectorAll('[data-testid={testId}]')[{index}].click(); true");
    static async Task<int> TabCount(Harness h) => int.Parse(await h.Eval("document.querySelectorAll('[data-testid=tab]').length"));
    static string[] Sessions(string root) => Directory.Exists(Path.Combine(root, "recovery")) ? Directory.GetDirectories(Path.Combine(root, "recovery")) : [];

    /// <summary>Starts the window with the close prompt on, then makes two tabs each with an edit that is on disk.</summary>
    static async Task<(Harness h, string root, Task closed)> StartWithTwoDirtyTabs()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-win-tabs-" + Guid.NewGuid().ToString("N"));
        var h = await Harness.StartAsync(existingRoot: root, promptOnClose: true);
        var closed = new TaskCompletionSource();
        await h.OnUi(() => { h.Window.Closed += (_, _) => closed.TrySetResult(); return Task.FromResult(0); });
        await WaitFor(() => h.Window.RecoveryMgr.SessionId == h.Router.CurrentScope?.SessionId);
        Assert.Null((await h.Apply(0, null, Harness.Shape("one", 20, 20))).Error);
        await Click(h, "tab-new");
        await WaitFor(() => h.Router.Documents.Count == 2 && h.Window.RecoveryMgr.OpenSessions.Count == 2 && h.Window.RecoveryMgr.SessionId == h.Router.CurrentScope?.SessionId);
        Assert.Null((await h.Apply(0, null, Harness.Shape("two", 40, 40))).Error);
        await WaitFor(() => h.Window.RecoveryMgr.Status.LastDurableRevision >= 1);
        await h.Window.RecoveryMgr.DrainAsync();
        Assert.Equal(2, Sessions(root).Length);
        return (h, root, closed.Task);
    }

    [Fact]
    public async Task EachTabIsADocumentTheHostKnowsAbout()
    {
        var (h, root, _) = await StartWithTwoDirtyTabs();
        try
        {
            Assert.Equal(2, await TabCount(h));
            Assert.Equal(2, h.Router.Documents.Count);
            var second = h.Router.CurrentScope!;
            await Click(h, "tab", 0); // switching is announced, so a request for "the current document" means the one on screen
            await WaitFor(() => h.Router.CurrentScope!.DocumentId != second.DocumentId);
            var first = h.Router.CurrentScope!;
            var summary = await h.Send("doc.summary", scope: second); // addressed by id, the hidden tab still answers
            Assert.Null(summary.Error);
            Assert.NotEqual(first.DocumentId, second.DocumentId);
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task DontSaveOnATabDiscardsOnlyThatTabsRecoverySession()
    {
        var (h, root, closed) = await StartWithTwoDirtyTabs();
        try
        {
            await Click(h, "tab-close", 1); // the second tab, which has unsaved work
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
            await Click(h, "close-discard");
            await WaitFor(() => TabCount(h).GetAwaiter().GetResult() == 1);
            await WaitFor(() => Sessions(root).Length == 1);
            Assert.False(closed.IsCompleted); // the window stays open
            Assert.Single(h.Window.RecoveryMgr.OpenSessions);
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ClosingTheWindowAsksOnceAndDontSaveAnyLeavesNothingToRecover()
    {
        var (h, root, closed) = await StartWithTwoDirtyTabs();
        try
        {
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); });
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
            Assert.Contains("2 documents", await h.Eval("document.querySelector('[data-testid=close-prompt]').innerText"));
            await Click(h, "close-discard");
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitFor(() => Sessions(root).Length == 0, 15);
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task SaveAllWritesEveryTabThenCloses()
    {
        var (h, root, closed) = await StartWithTwoDirtyTabs();
        var n = 0;
        try
        {
            h.Window.Services.DialogOverride = _ => Path.Combine(root, $"doc{++n}.diagram.json");
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); });
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
            await Click(h, "close-save");
            await closed.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(2, n);
            var texts = Directory.GetFiles(root, "doc*.diagram.json").Select(File.ReadAllText).ToList();
            Assert.Equal(2, texts.Count);
            Assert.Single(texts, t => t.Contains("\"one\""));
            Assert.Single(texts, t => t.Contains("\"two\""));
            await WaitFor(() => Sessions(root).Length == 0, 15);
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ACrashWithTwoUnsavedTabsKeepsBothSessions()
    {
        var (h, root, _) = await StartWithTwoDirtyTabs();
        try
        {
            await h.Window.RecoveryMgr.DisposeAsync(); // the application dies: no clean close, nothing discarded
            Assert.Equal(2, Sessions(root).Length);
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }
}
