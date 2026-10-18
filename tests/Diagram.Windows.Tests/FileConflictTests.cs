using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;
using Diagram.Core.Contracts;
using Xunit;
namespace Diagram.Windows.Tests;
public class FileConflictTests
{
    static RequestEnvelope Req(string method, JsonObject? args = null) => new() { RequestId = Guid.NewGuid().ToString(), Method = method, Params = args ?? new() };
    [Fact]
    public async Task ExternalWriteBeforeSaveLeavesDiskAndLocalWorkIntact()
    {
        await using var h = await Harness.StartAsync(); await h.Window.RecoveryMgr.WhenSessionReadyAsync();
        Assert.Null((await h.Apply(0, null, Harness.Shape("local", 20, 20))).Error);
        var path = Path.Combine(h.Root, "shared.diagram.json"); h.Window.Services.DialogOverride = _ => path;
        Assert.Null((await h.Window.Services.HandleAsync(Req("host.saveJson"), default)).Error);
        var external = await File.ReadAllTextAsync(path) + "\n "; await File.WriteAllTextAsync(path, external);
        Assert.Null((await h.Apply(1, null, Harness.Shape("newer", 60, 20))).Error);
        var before = (await h.Send("doc.snapshot")).Result!.Value.GetRawText();
        var result = await h.Window.Services.HandleAsync(Req("host.saveJson"), default);
        Assert.Null(result.Error);
        Assert.True(result.Result!.Value.TryGetProperty("conflict", out _));
        Assert.Equal(external, await File.ReadAllTextAsync(path));
        Assert.Equal(before, (await h.Send("doc.snapshot")).Result!.Value.GetRawText());
        Assert.True((await h.Send("doc.status")).Result!.Value.GetProperty("unsaved").GetBoolean());
    }
    static string Token(ResponseEnvelope response) => response.Result!.Value.GetProperty("conflict").GetProperty("token").GetString()!;
    static Task<ResponseEnvelope> Resolve(Harness h, string token, string action, bool discard = false) => h.Window.Services.HandleAsync(Req("host.fileConflict.resolve",new(){["token"]=token,["action"]=action,["discard"]=discard}),default);
    static async Task<string> Saved(Harness h)
    {
        await h.Window.RecoveryMgr.WhenSessionReadyAsync(); Assert.Null((await h.Apply(0,null,Harness.Shape("local"))).Error);
        var path=Path.Combine(h.Root,"shared.diagram.json"); h.Window.Services.DialogOverride=_=>path;
        Assert.Null((await h.Window.Services.HandleAsync(Req("host.saveJson"),default)).Error); return path;
    }
    [Fact]
    public async Task SecondExternalChangeRefreshesConsentAndOverwriteCannotReplayOldOffer()
    {
        await using var h=await Harness.StartAsync(); var path=await Saved(h);
        await File.AppendAllTextAsync(path,"\n"); var first=await h.Window.Services.HandleAsync(Req("host.saveJson"),default); var token=Token(first);
        await File.AppendAllTextAsync(path," "); var refreshed=await Resolve(h,token,"overwrite"); var next=Token(refreshed);
        Assert.NotEqual(token,next); Assert.NotNull((await Resolve(h,token,"overwrite")).Error);
        Assert.Null((await Resolve(h,next,"overwrite")).Error);
        Assert.False((await h.Send("doc.status")).Result!.Value.GetProperty("unsaved").GetBoolean());
    }
    [Fact]
    public async Task SaveAsCancelOrSamePathCannotBypassConflictAndDifferentPathPreservesExternalVersion()
    {
        await using var h=await Harness.StartAsync(); var path=await Saved(h);
        await File.AppendAllTextAsync(path,"\n"); var external=await File.ReadAllBytesAsync(path);
        var token=Token(await h.Window.Services.HandleAsync(Req("host.saveJson"),default));
        h.Window.Services.DialogOverride=_=>null; Assert.True((await Resolve(h,token,"saveAs")).Result!.Value.GetProperty("cancelled").GetBoolean());
        h.Window.Services.DialogOverride=_=>path; token=Token(await Resolve(h,token,"saveAs"));
        var alternate=Path.Combine(h.Root,"mine.diagram.json");h.Window.Services.DialogOverride=_=>alternate;
        Assert.Null((await Resolve(h,token,"saveAs")).Error);Assert.Equal(external,await File.ReadAllBytesAsync(path));Assert.True(File.Exists(alternate));
        Assert.Equal(alternate,h.Window.Services.CurrentPath);
    }
    [Fact]
    public async Task ReloadRequiresDiscardAndLeavesWorkUntouchedForInvalidFileOrStaleRevision()
    {
        await using var h=await Harness.StartAsync();var path=await Saved(h);var valid=JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Null((await h.Apply(1,null,Harness.Shape("newer",90,20))).Error);var before=(await h.Send("doc.snapshot")).Result!.Value.GetRawText();
        await File.WriteAllTextAsync(path,"bad json");var token=Token(await h.Window.Services.HandleAsync(Req("host.saveJson"),default));
        Assert.Equal("invalid_request",(await Resolve(h,token,"reload")).Error?.Code);
        Assert.NotNull((await Resolve(h,token,"reload",true)).Error);Assert.Equal(before,(await h.Send("doc.snapshot")).Result!.Value.GetRawText());
        valid["document"]!["title"]="External";await File.WriteAllTextAsync(path,valid.ToJsonString());token=Token(await Resolve(h,token,"reload",true));
        var old=h.Router.CurrentScope!;var loaded=await Resolve(h,token,"reload",true);Assert.Null(loaded.Error);
        Assert.True(loaded.Result!.Value.GetProperty("reloaded").GetBoolean());Assert.NotEqual(old.SessionId,h.Router.CurrentScope!.SessionId);
        Assert.Equal("External",(await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("title").GetString());
        await File.AppendAllTextAsync(path," ");token=Token(await h.Window.Services.HandleAsync(Req("host.saveJson"),default));
        var rev=(await h.Send("doc.snapshot")).Result!.Value.GetProperty("revision").GetInt32();Assert.Null((await h.Apply(rev,null,Harness.Shape("latest",100,20))).Error);
        Assert.Equal("revision_conflict",(await Resolve(h,token,"overwrite")).Error?.Code);
    }
    [Fact]
    public async Task FinalWriteGuardAndUnreadableOrDeletedFilesAreSafe()
    {
        await using var h=await Harness.StartAsync();var path=await Saved(h);var external="external at commit";
        h.Window.Writer.BeforeReplace=()=>File.WriteAllText(path,external);
        var result=await h.Window.Services.HandleAsync(Req("host.saveJson"),default);h.Window.Writer.BeforeReplace=null;
        Assert.NotEmpty(Token(result));Assert.Equal(external,await File.ReadAllTextAsync(path));
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var unreadable=await h.Window.Services.HandleAsync(Req("host.fileConflict.check"),default);
            Assert.False(unreadable.Result!.Value.GetProperty("conflict").TryGetProperty("observed",out _));
        }
        File.Delete(path);var missing=await h.Window.Services.HandleAsync(Req("host.fileConflict.check"),default);var token=Token(missing);
        Assert.False(missing.Result!.Value.GetProperty("conflict").GetProperty("observed").GetProperty("exists").GetBoolean());
        Assert.Equal("not_found",(await Resolve(h,token,"reload",true)).Error?.Code);Assert.Null((await Resolve(h,token,"overwrite")).Error);Assert.True(File.Exists(path));
    }

}
