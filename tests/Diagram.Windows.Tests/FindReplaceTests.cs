using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;
namespace Diagram.Windows.Tests;
public class FindReplaceTests
{
    static async Task WaitFor(Harness h, string expression)
    {
        var until=DateTime.UtcNow.AddSeconds(20);
        while(await h.Eval(expression)!="true") { if(DateTime.UtcNow>until)throw new TimeoutException(expression);await Task.Delay(50); }
    }
    static string SetInput(string label,string value) => $"(() => {{ const el=document.querySelector('input[aria-label=\"{label}\"]'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(el,{JsonSerializer.Serialize(value)}); el.dispatchEvent(new Event('input',{{bubbles:true}})); return true; }})()";
    [Fact]
    public async Task RealFindPanelReplacementSurvivesJsonSaveAndNativeVisioRoundTrip()
    {
        await using var h=await Harness.StartAsync();await h.Window.RecoveryMgr.WhenSessionReadyAsync();
        var shape=Harness.Shape("stable",100,100);shape["text"]=new JsonObject{["value"]="Service Service",["bold"]=true};
        var connector=new JsonObject { ["op"]="createConnector", ["alias"]="stable-connector",
            ["from"]=new JsonObject{["glue"]="none",["point"]=new JsonObject{["x"]=20,["y"]=20}},
            ["to"]=new JsonObject{["glue"]="none",["point"]=new JsonObject{["x"]=80,["y"]=80}},
            ["label"]=new JsonObject{["value"]="Service",["italic"]=true} };
        Assert.Null((await h.Apply(0,null,shape,connector)).Error);
        var before=(await h.Send("doc.snapshot")).Result!.Value;
        var original=before.GetProperty("document").GetProperty("pages")[0].GetProperty("elements")[0];var id=original.GetProperty("id").GetString();
        await h.Eval("document.querySelector('[data-testid=menu-edit]').click(); true");
        await WaitFor(h,"document.querySelector('[data-testid=menu-find]') !== null");
        await h.Eval("document.querySelector('[data-testid=menu-find]').click(); true");
        await WaitFor(h,"document.querySelector('[data-testid=find-panel]') !== null");
        await h.Eval(SetInput("Find literal text","Service"));await h.Eval(SetInput("Replace with","Queue"));
        await WaitFor(h,"document.querySelector('[data-testid=find-count]').textContent.includes('2 editable')");
        await h.Eval("Array.from(document.querySelectorAll('[data-testid=find-panel] button')).find(b=>b.textContent==='Replace all').click(); true");
        await WaitFor(h,"document.querySelector('[data-testid=find-message]').textContent.includes('Replaced text')");
        var after=(await h.Send("doc.snapshot")).Result!.Value;
        var edited=after.GetProperty("document").GetProperty("pages")[0].GetProperty("elements")[0];
        Assert.Equal(2,after.GetProperty("revision").GetInt32());Assert.Equal(id,edited.GetProperty("id").GetString());
        Assert.Equal("stable",edited.GetProperty("alias").GetString());Assert.Equal("Queue Queue",edited.GetProperty("text").GetProperty("value").GetString());Assert.True(edited.GetProperty("text").GetProperty("bold").GetBoolean());
        var jsonPath=Path.Combine(h.Root,"found.diagram.json");h.Window.Services.DialogOverride=_=>jsonPath;
        Assert.Null((await h.Window.Services.HandleAsync(new RequestEnvelope{RequestId=Guid.NewGuid().ToString(),Method="host.saveJson",Params=new JsonObject()},default)).Error);
        var saved=JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!;Assert.Equal("Queue Queue",saved["document"]!["pages"]![0]!["elements"]![0]!["text"]!["value"]!.GetValue<string>());
        var path=Path.Combine(h.Root,"found.vsdx");var exported=await h.Window.Native.ExportAsync(path,h.Router.CurrentScope!,true,default);Assert.True(exported.IsOk,exported.Error?.Message);
        using var stream=File.OpenRead(path);var imported=new NativeVsdxImporter().Import(stream,new ImportOptions());Assert.True(imported.IsOk,imported.Error?.Message);
        var back=JsonSerializer.SerializeToNode(imported.Value.Document,ContractJson.Options)!["pages"]![0]!["elements"]![0]!;
        Assert.Equal(id,back["id"]!.GetValue<string>());Assert.Equal("Queue Queue",back["text"]!["value"]!.GetValue<string>());Assert.True(back["text"]!["bold"]!.GetValue<bool>());
        var label=JsonSerializer.SerializeToNode(imported.Value.Document,ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray().Single(e=>e!["alias"]?.GetValue<string>()=="stable-connector")!["label"]!;
        Assert.Equal("Queue",label["value"]!.GetValue<string>());Assert.True(label["italic"]!.GetValue<bool>());
        var evidence=Path.Combine(Harness.RepoRoot,"artifacts/w11-find-replace");Directory.CreateDirectory(evidence);
        await File.WriteAllBytesAsync(Path.Combine(evidence,"native-find.png"),await h.OnUi(()=>h.Host.CapturePreviewAsync()));
        await File.WriteAllTextAsync(Path.Combine(evidence,"native-verification.json"),JsonSerializer.Serialize(new{verifiedUtc=DateTime.UtcNow,realPanel=true,oneRevision=true,jsonPersistence=true,nativeVisioRoundTrip=true,identityAndBoldRetained=true,connectorLabelAndItalicRetained=true}));
    }
}
