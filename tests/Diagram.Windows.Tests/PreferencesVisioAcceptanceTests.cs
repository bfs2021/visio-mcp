using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

namespace Diagram.Windows.Tests;

public class PreferencesVisioAcceptanceTests
{
    [Fact]
    public async Task ThemeAndUnitsSavePreservesAuthoredShapeInNativeVisioExport()
    {
        await using var h = await Harness.StartAsync();
        var shape = Harness.Shape("authored", 80, 90);
        shape["style"] = new JsonObject
        {
            ["fill"] = new JsonObject { ["kind"] = "solid", ["colour"] = "#22AA44", ["opacity"] = 1 },
            ["stroke"] = new JsonObject { ["kind"] = "solid", ["colour"] = "#123456", ["opacity"] = 1 },
            ["strokeWidthPt"] = 2, ["dash"] = "solid", ["lineCap"] = "round", ["lineJoin"] = "round"
        };
        shape["text"] = new JsonObject { ["value"] = "Authored", ["fontFamily"] = "Arial", ["fontSizePt"] = 17, ["colour"] = "#AA1177" };
        Assert.Null((await h.Apply(0, null, shape)).Error);
        var beforeDocument = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetRawText();

        async Task<JsonNode> ExportAndImport(string fileName)
        {
            var path = Path.Combine(h.Root, fileName);
            var exported = await h.Window.Native.ExportAsync(path, h.Router.CurrentScope!, true, Harness.Ct(60));
            Assert.True(exported.IsOk, exported.Error?.Message);
            using var stream = File.OpenRead(path);
            var imported = new NativeVsdxImporter().Import(stream, new ImportOptions());
            Assert.True(imported.IsOk, imported.Error?.Message);
            return JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!;
        }
        var before = await ExportAndImport("before-preferences.vsdx");
        var get = await h.Window.Services.HandleAsync(new RequestEnvelope { RequestId = "preferences-get", Method = "host.preferences.get", Params = new() }, default);
        Assert.Null(get.Error);
        var draft = JsonNode.Parse(get.Result!.Value.GetProperty("preferences").GetRawText())!;
        draft["theme"] = "dark";
        draft["units"] = "in";
        draft["font"]!["family"] = "Consolas";
        var save = await h.Window.Services.HandleAsync(new RequestEnvelope
        {
            RequestId = "preferences-save", Method = "host.preferences.save",
            Params = new JsonObject { ["preferences"] = draft, ["expectedVersionTag"] = get.Result.Value.GetProperty("versionTag").GetString() }
        }, default);
        Assert.Null(save.Error);
        Assert.Equal(beforeDocument, (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetRawText());
        var after = await ExportAndImport("after-preferences.vsdx");

        static JsonNode Element(JsonNode doc) => doc["pages"]![0]!["elements"]!.AsArray().Single(e => e!["alias"]?.GetValue<string>() == "authored")!;
        var authoredBefore = Element(before);
        var authoredAfter = Element(after);
        Assert.True(JsonNode.DeepEquals(authoredBefore["style"], authoredAfter["style"]));
        Assert.True(JsonNode.DeepEquals(authoredBefore["text"], authoredAfter["text"]));
        Assert.True(JsonNode.DeepEquals(before["pages"]![0]!["background"], after["pages"]![0]!["background"]));
        Assert.Equal("#22aa44", authoredAfter["style"]!["fill"]!["colour"]!.GetValue<string>());
        Assert.Equal("#123456", authoredAfter["style"]!["stroke"]!["colour"]!.GetValue<string>());
        Assert.Equal("Arial", authoredAfter["text"]!["fontFamily"]!.GetValue<string>());
        Assert.Equal(17, authoredAfter["text"]!["fontSizePt"]!.GetValue<double>());
        Assert.Equal("#aa1177", authoredAfter["text"]!["colour"]!.GetValue<string>());
    }
}