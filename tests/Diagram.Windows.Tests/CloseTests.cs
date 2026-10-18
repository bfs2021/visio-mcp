using System.IO;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>W01: closing the real window with unsaved changes asks, and each answer does what it says.</summary>
public class CloseTests
{
    static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met"); await Task.Delay(100); }
    }

    static async Task<bool> Shown(Harness h, string testId) => await h.Eval($"document.querySelector('[data-testid={testId}]') !== null") == "true";
    static Task Click(Harness h, string testId) => h.Eval($"document.querySelector('[data-testid={testId}]').click(); true");

    /// <summary>Starts the window with the close prompt on, makes an edit, and waits until it is on disk.</summary>
    static async Task<(Harness h, string root, Task closed)> StartDirty(string? existingRoot = null, bool edit = true)
    {
        var root = existingRoot ?? Path.Combine(Path.GetTempPath(), "diagram-win-close-" + Guid.NewGuid().ToString("N"));
        var h = await Harness.StartAsync(existingRoot: root, promptOnClose: true);
        var closed = new TaskCompletionSource();
        await h.OnUi(() => { h.Window.Closed += (_, _) => closed.TrySetResult(); return Task.FromResult(0); });
        if (edit)
        {
            await WaitFor(() => h.Window.RecoveryMgr.SessionId == h.Router.CurrentScope?.SessionId);
            Assert.Null((await h.Apply(0, null, Harness.Shape("crm", 20, 20))).Error);
            await WaitFor(() => h.Window.RecoveryMgr.Status.LastDurableRevision >= 1);
            await h.Window.RecoveryMgr.DrainAsync();
        }
        return (h, root, closed.Task);
    }

    static string[] Sessions(string root) => Directory.Exists(Path.Combine(root, "recovery")) ? Directory.GetDirectories(Path.Combine(root, "recovery")) : [];

    [Fact]
    public async Task ADocumentWithNoChangesClosesWithoutAsking()
    {
        var (h, root, closed) = await StartDirty(edit: false);
        try
        {
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); });
            await closed.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task TheWindowTitleNamesTheFileAndMarksUnsavedWork()
    {
        var (h, root, _) = await StartDirty(edit: false);
        var file = Path.Combine(root, "plan.diagram.json");
        try
        {
            async Task<string> Title() => await h.OnUi(() => Task.FromResult(h.Window.Title));
            await WaitFor(() => Title().GetAwaiter().GetResult() == "Untitled - Diagram");
            await WaitFor(() => h.Window.RecoveryMgr.SessionId == h.Router.CurrentScope?.SessionId);
            Assert.Null((await h.Apply(0, null, Harness.Shape("crm", 20, 20))).Error);
            await WaitFor(() => Title().GetAwaiter().GetResult() == "● Untitled - Diagram");
            h.Window.Services.DialogOverride = _ => file;
            var saved = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "s", Method = "host.saveJson", Params = new JsonObject { ["saveAs"] = true } }, default);
            Assert.Null(saved.Error);
            await WaitFor(() => Title().GetAwaiter().GetResult() == "plan.diagram.json - Diagram");
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task TheRecoveryListDescribesEachSessionAndForgetsOnesThatWereDiscarded()
    {
        var (first, root, _) = await StartDirty();
        try
        {
            await first.Window.RecoveryMgr.DisposeAsync(); // the application dies with unsaved work
        }
        finally { await first.DisposeAsync(); }
        try
        {
            await using var second = await Harness.StartAsync(existingRoot: root);
            await WaitFor(() => second.Window.RecoveryMgr.SessionId == second.Router.CurrentScope?.SessionId);
            var list = await second.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "l", Method = "host.recovery.list", Params = new JsonObject() }, default);
            Assert.Null(list.Error);
            var sessions = list.Result!.Value.GetProperty("sessions");
            Assert.Equal(1, sessions.GetArrayLength());
            var one = sessions[0];
            Assert.Equal(1, one.GetProperty("pageCount").GetInt32());
            Assert.Equal(1, one.GetProperty("lastDurableRevision").GetInt32());
            Assert.True(one.TryGetProperty("title", out _) && one.TryGetProperty("lastWrite", out _) && one.TryGetProperty("objectCount", out _));
            var id = one.GetProperty("sessionId").GetString()!;
            var gone = await second.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "d", Method = "host.recovery.discard", Params = new JsonObject { ["sessionId"] = id } }, default);
            Assert.Null(gone.Error);
            var after = await second.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "l2", Method = "host.recovery.list", Params = new JsonObject() }, default);
            Assert.Equal(0, after.Result!.Value.GetProperty("sessions").GetArrayLength());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task DontSaveClosesAndTheWorkIsNotOfferedOnTheNextStart()
    {
        var (h, root, closed) = await StartDirty();
        try
        {
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); });
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
            Assert.False(closed.IsCompleted); // held until the person answers
            Assert.Single(Sessions(root));
            await Click(h, "close-discard");
            await closed.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Empty(Sessions(root));
        }
        finally { await h.DisposeAsync(); }
        try
        {
            await using var second = await Harness.StartAsync(existingRoot: root);
            await WaitFor(() => second.Window.RecoveryMgr.SessionId == second.Router.CurrentScope?.SessionId);
            var offer = await second.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "c", Method = "host.recovery.candidate", Params = new JsonObject() }, default);
            Assert.False(offer.Result!.Value.GetProperty("found").GetBoolean()); // nothing to recover: the person chose not to keep it
            Assert.Equal("false", await second.Eval("document.querySelector('[data-testid=recovery-offer]') !== null"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task CancelKeepsTheWindowOpenAndTheWorkProtectedAndItAsksAgain()
    {
        var (h, root, closed) = await StartDirty();
        try
        {
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); });
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
            await Click(h, "close-cancel");
            await WaitFor(() => !Shown(h, "close-prompt").GetAwaiter().GetResult());
            await Task.Delay(500);
            Assert.False(closed.IsCompleted);
            Assert.Single(Sessions(root)); // still protected
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); }); // asks again
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task SaveWritesTheFileThenClosesAndLeavesNothingToRecover()
    {
        var (h, root, closed) = await StartDirty();
        var file = Path.Combine(root, "plan.diagram.json");
        try
        {
            h.Window.Services.DialogOverride = _ => file;
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); });
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
            await Click(h, "close-save");
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(File.Exists(file));
            Assert.Contains("crm", File.ReadAllText(file));
            Assert.Empty(Sessions(root));
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ACancelledSaveAsDoesNotCloseTheWindow()
    {
        var (h, root, closed) = await StartDirty();
        try
        {
            h.Window.Services.DialogOverride = _ => null; // the person cancels the file dialog
            await h.OnUi(() => { h.Window.Close(); return Task.FromResult(0); });
            await WaitFor(() => Shown(h, "close-prompt").GetAwaiter().GetResult());
            await Click(h, "close-save");
            await WaitFor(() => Shown(h, "close-error").GetAwaiter().GetResult());
            Assert.False(closed.IsCompleted);
            Assert.Single(Sessions(root));
        }
        finally { await h.DisposeAsync(); try { Directory.Delete(root, true); } catch { } }
    }
}
