using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Xunit;

namespace Diagram.Windows.Tests;

public class PreferencesHostTests
{
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
        Assert.Equal("conflict", stale.Error?.Code);
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
