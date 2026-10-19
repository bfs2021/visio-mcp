using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
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
        Assert.False(File.Exists(path));

        var changed = JsonNode.Parse(initial.GetProperty("preferences").GetRawText())!;
        changed["theme"] = "dark";
        var args = new JsonObject { ["preferences"] = changed, ["expectedVersionTag"] = initial.GetProperty("versionTag").GetString() };
        var save = await h.Window.Services.HandleAsync(Req("host.preferences.save", args), default);
        Assert.Null(save.Error);
        Assert.Equal("dark", save.Result!.Value.GetProperty("preferences").GetProperty("theme").GetString());
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
}
