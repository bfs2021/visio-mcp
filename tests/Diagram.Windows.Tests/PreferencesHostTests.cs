using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using System.Windows.Media;
using Xunit;

namespace Diagram.Windows.Tests;

public class PreferencesHostTests
{
    [Fact]
    public async Task VisibleStartupAndAgentRegistryCapturePreferencesBeforeAnnouncingDocuments()
    {
        await using var h = await Harness.StartAsync(navigate: false);
        var preferences = PreferencesValidation.DefaultPreferences();
        var json = JsonSerializer.SerializeToNode(preferences, ContractJson.Options)!;
        json["grid"]!["spacingPt"] = 43;
        json["font"]!["family"] = "Consolas";
        await File.WriteAllTextAsync(Path.Combine(h.Root, "preferences.json"), json.ToJsonString());
        Assert.Empty(h.Router.Documents);
        await h.Ui.InvokeAsync(() => h.Window.Navigate()).Task;
        await h.Router.WaitReadyAsync(Harness.Ct(30));
        var visible = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document");
        Assert.Equal(43, visible.GetProperty("pages")[0].GetProperty("grid").GetProperty("spacingPt").GetDouble());
        Assert.Contains("Consolas", visible.GetProperty("metadata").GetProperty("editor.defaults.v1").GetString());
        var created = await h.Window.Documents.HandleAsync(new RequestEnvelope { RequestId = "create", Method = "docs.create", Params = new() }, Harness.Ct(60));
        Assert.Null(created.Error);
        var scope = new Scope { DocumentId = created.Result!.Value.GetProperty("documentId").GetString()!, SessionId = created.Result.Value.GetProperty("sessionId").GetString()! };
        var agent = (await h.Send("doc.snapshot", scope: scope)).Result!.Value.GetProperty("document");
        Assert.Equal(43, agent.GetProperty("pages")[0].GetProperty("grid").GetProperty("spacingPt").GetDouble());
        Assert.Contains("Consolas", agent.GetProperty("metadata").GetProperty("editor.defaults.v1").GetString());
        Assert.Equal(Diagram.Host.Core.BridgeRouter.Agent, h.Router.ChannelOf(scope.DocumentId));
        Assert.Equal(0, visible.GetProperty("revision").GetInt32());
    }
    [Fact]
    public async Task InternalGetSaveAndConflictUseTheIsolatedPerUserRoot()
    {
        await using var h = await Harness.StartAsync(navigate: false);
        var path = Path.Combine(h.Root, "preferences.json");
        RequestEnvelope Req(string method, JsonObject? args = null) => new() { RequestId = Guid.NewGuid().ToString(), Method = method, Params = args ?? new() };
        var get = await h.Window.Services.HandleAsync(Req("host.preferences.get"), default);
        Assert.Null(get.Error);
        var initial = get.Result!.Value;
        Assert.Equal("light", initial.GetProperty("preferences").GetProperty("theme").GetString());
        Assert.Contains("Segoe UI", initial.GetProperty("installedFonts").EnumerateArray().Select(x => x.GetString()));
        Assert.False(File.Exists(path));

        var changed = JsonNode.Parse(initial.GetProperty("preferences").GetRawText())!;
        changed["theme"] = "dark";
        var args = new JsonObject { ["preferences"] = changed, ["expectedVersionTag"] = initial.GetProperty("versionTag").GetString() };
        var save = await h.Window.Services.HandleAsync(Req("host.preferences.save", args), default);
        Assert.Null(save.Error);
        Assert.Equal("dark", save.Result!.Value.GetProperty("preferences").GetProperty("theme").GetString());
        Assert.Equal("#FF111827", await h.Ui.InvokeAsync(() => ((SolidColorBrush)h.Window.Background).Color.ToString()).Task);
        Assert.True(File.Exists(path));

        var stale = await h.Window.Services.HandleAsync(Req("host.preferences.save", (JsonObject)args.DeepClone()), default);
        Assert.Equal("revision_conflict", stale.Error?.Code);
        Assert.Equal("preferences_changed", stale.Error!.Details!["reason"]!.GetValue<string>());
        Assert.Equal("dark", stale.Error!.Details!["preferences"]!["theme"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidPayloadCannotTouchThePreferencesFile()
    {
        await using var h = await Harness.StartAsync(navigate: false);
        var request = new RequestEnvelope { RequestId = "invalid", Method = "host.preferences.save", Params = new JsonObject { ["preferences"] = new JsonObject { ["schemaVersion"] = 2 }, ["expectedVersionTag"] = "missing" } };
        var result = await h.Window.Services.HandleAsync(request, default);
        Assert.Equal("invalid_request", result.Error?.Code);
        Assert.False(File.Exists(Path.Combine(h.Root, "preferences.json")));
    }

    [Fact]
    public async Task InitializedAgentUsesSavedDefaultsForOnlyItsNextDrawing()
    {
        await using var h = await Harness.StartAsync();
        RequestEnvelope Create(string id) => new() { RequestId = id, Method = "docs.create", Params = new() };
        var first = await h.Window.Documents.HandleAsync(Create("agent-before"), Harness.Ct(30));
        Assert.Null(first.Error);
        var oldScope = new Scope { DocumentId = first.Result!.Value.GetProperty("documentId").GetString()!, SessionId = first.Result.Value.GetProperty("sessionId").GetString()! };
        var oldDocument = (await h.Send("doc.snapshot", scope: oldScope)).Result!.Value.GetProperty("document").GetRawText();
        var initial = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "get-pref", Method = "host.preferences.get", Params = new() }, default);
        var changed = JsonNode.Parse(initial.Result!.Value.GetProperty("preferences").GetRawText())!;
        changed["grid"]!["spacingPt"] = 91;
        changed["font"]!["family"] = "Consolas";
        var saved = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "save-pref", Method = "host.preferences.save", Params = new JsonObject { ["preferences"] = changed, ["expectedVersionTag"] = initial.Result.Value.GetProperty("versionTag").GetString() } }, default);
        Assert.Null(saved.Error);
        var next = await h.Window.Documents.HandleAsync(Create("agent-after"), Harness.Ct(30));
        Assert.Null(next.Error);
        var nextScope = new Scope { DocumentId = next.Result!.Value.GetProperty("documentId").GetString()!, SessionId = next.Result.Value.GetProperty("sessionId").GetString()! };
        var nextDocument = (await h.Send("doc.snapshot", scope: nextScope)).Result!.Value.GetProperty("document");
        Assert.Equal(91, nextDocument.GetProperty("pages")[0].GetProperty("grid").GetProperty("spacingPt").GetDouble());
        Assert.Contains("Consolas", nextDocument.GetProperty("metadata").GetProperty("editor.defaults.v1").GetString());
        Assert.Equal(oldDocument, (await h.Send("doc.snapshot", scope: oldScope)).Result!.Value.GetProperty("document").GetRawText());
    }

    [Fact]
    public async Task DialogFocusAndSavedThemeSurviveNativeRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagram-pref-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var h = await Harness.StartAsync(existingRoot: root))
            {
                await h.Eval("document.querySelector('[data-testid=menu-edit]').click()");
                Assert.Equal("true", await h.Eval("!!document.querySelector('[data-testid=menu-preferences]')"));
                await h.Eval("document.querySelector('[data-testid=menu-preferences]').click()");
                for (var i = 0; i < 40 && await h.Eval("!!document.querySelector('[data-testid=preferences-dialog] select')") != "true"; i++) await Task.Delay(50);
                Assert.Equal("true", await h.Eval("document.activeElement.closest('[data-testid=preferences-dialog]') !== null"));
                await h.Eval("document.querySelector('[data-testid=preferences-dialog] button:nth-last-child(2)').click()"); // Cancel
                Assert.Equal("true", await h.Eval("document.activeElement === document.querySelector('[data-testid=menu-edit]')"));

                var initial = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "ui-get", Method = "host.preferences.get", Params = new() }, default);
                var changed = JsonNode.Parse(initial.Result!.Value.GetProperty("preferences").GetRawText())!;
                changed["theme"] = "dark";
                var saved = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "ui-save", Method = "host.preferences.save", Params = new JsonObject { ["preferences"] = changed, ["expectedVersionTag"] = initial.Result.Value.GetProperty("versionTag").GetString() } }, default);
                Assert.Null(saved.Error);
            }
            await using (var restarted = await Harness.StartAsync(existingRoot: root))
            {
                Assert.Equal("dark", JsonSerializer.Deserialize<string>(await restarted.Eval("document.documentElement.dataset.theme")));
                Assert.Equal("#FF111827", await restarted.Ui.InvokeAsync(() => ((SolidColorBrush)restarted.Window.Background).Color.ToString()).Task);
                await restarted.Eval("document.querySelector('[data-testid=menu-edit]').click()");
                await restarted.Eval("document.querySelector('[data-testid=menu-preferences]').click()");
                for (var i = 0; i < 40 && await restarted.Eval("!!document.querySelector('[data-testid=preferences-dialog] select')") != "true"; i++) await Task.Delay(50);
                Assert.Equal("dark", JsonSerializer.Deserialize<string>(await restarted.Eval("document.querySelectorAll('[data-testid=preferences-dialog] fieldset select')[1]?.value")));
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
