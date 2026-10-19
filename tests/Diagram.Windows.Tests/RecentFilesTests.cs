using System.IO;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;
namespace Diagram.Windows.Tests;
public class RecentFilesTests
{
 static RequestEnvelope Request(string method,JsonObject? args=null)=>new(){RequestId=Guid.NewGuid().ToString(),Method=method,Params=args??new()};
 [Fact] public async Task NativeSuccessfulSavesAndOpensRecordHistoryButCancelledOrFailedActionsDoNot()
 {
  await using var h=await Harness.StartAsync(); await h.Window.RecoveryMgr.WhenSessionReadyAsync();
  var file=Path.Combine(h.Root,"Unicode résumé.diagram.json");h.Window.Services.DialogOverride=_=>file;
  Assert.Null((await h.Window.Services.HandleAsync(Request("host.saveJson"),default)).Error);
  var list=await h.Window.Services.HandleAsync(Request("host.recent.list"),default);Assert.Null(list.Error);
  var entry=list.Result!.Value.GetProperty("entries")[0]; Assert.Equal(file,entry.GetProperty("path").GetString());var id=entry.GetProperty("id").GetString();
  var opened=await h.Window.Services.HandleAsync(Request("host.openJson",new(){["recentId"]=id}),default);Assert.Null(opened.Error);
  var store=new RecentFilesStore(h.Window.Recents.File,new AtomicFileWriter());
  Assert.Single((await store.ReadAsync()).Entries);
  Assert.Equal(h.Root,(await h.Window.Recents.ReadAsync()).LastOpenFolder);
  var before=(await h.Window.Recents.ReadAsync()).Entries.ToArray();
  h.Window.Services.DialogOverride=_=>null;Assert.Null((await h.Window.Services.HandleAsync(Request("host.openJson"),default)).Error);
  var snap=(await h.Send("doc.snapshot")).Result!.Value.GetRawText();
  var refused=await h.Window.Services.HandleAsync(Request("host.openJson",new(){["recentId"]="invented"}),default);Assert.Equal("not_found",refused.Error?.Code);Assert.Equal(snap,(await h.Send("doc.snapshot")).Result!.Value.GetRawText());
  h.Window.Services.DialogOverride=_=>Path.Combine(h.Root,"bad.diagram.json");Assert.NotNull((await h.Window.Services.HandleAsync(Request("host.openJson"),default)).Error);
  Assert.Equal(before,(await h.Window.Recents.ReadAsync()).Entries);
  File.Delete(file);list=await h.Window.Services.HandleAsync(Request("host.recent.list"),default);Assert.Equal("missing",list.Result!.Value.GetProperty("entries")[0].GetProperty("availability").GetString());
  Assert.Null((await h.Window.Services.HandleAsync(Request("host.recent.remove",new(){["id"]=id}),default)).Error);Assert.Empty((await h.Window.Recents.ReadAsync()).Entries);
 }
 [Fact] public async Task ImportSaveAsHistoryAndFailuresDoNotChangeTheLastOpenFolder()
 {
  await using var h=await Harness.StartAsync();await h.Window.RecoveryMgr.WhenSessionReadyAsync();Assert.Null((await h.Apply(0,null,Harness.Shape("kept",20,20))).Error);
  var first=Path.Combine(h.Root,"first.diagram.json");h.Window.Services.DialogOverride=_=>first;Assert.Null((await h.Window.Services.HandleAsync(Request("host.saveJson"),default)).Error);
  h.Window.Services.DialogOverride=_=>first;Assert.Null((await h.Window.Services.HandleAsync(Request("host.openJson"),default)).Error);Assert.Equal(h.Root,(await h.Window.Recents.ReadAsync()).LastOpenFolder);
  var other=Path.Combine(h.Root,"other");Directory.CreateDirectory(other);var second=Path.Combine(other,"second.diagram.json");h.Window.Services.DialogOverride=_=>second;Assert.Null((await h.Window.Services.HandleAsync(Request("host.saveJson",new(){["saveAs"]=true}),default)).Error);Assert.Equal(h.Root,(await h.Window.Recents.ReadAsync()).LastOpenFolder);
  var before=(await h.Window.Recents.ReadAsync()).Entries.ToArray();h.Window.Writer.BeforeReplace=()=>throw new IOException("injected save failure");h.Window.Services.DialogOverride=_=>Path.Combine(h.Root,"failed.diagram.json");Assert.NotNull((await h.Window.Services.HandleAsync(Request("host.saveJson",new(){["saveAs"]=true}),default)).Error);h.Window.Writer.BeforeReplace=null;Assert.Equal(before,(await h.Window.Recents.ReadAsync()).Entries);
  var vsdx=Path.Combine(other,"reference.vsdx");var exported=await h.Window.Native.ExportAsync(vsdx,h.Router.CurrentScope!,true,default);Assert.True(exported.IsOk);Assert.Equal(before,(await h.Window.Recents.ReadAsync()).Entries);
  h.Window.Services.DialogOverride=_=>vsdx;Assert.Null((await h.Window.Services.HandleAsync(Request("host.openVsdx"),default)).Error);var state=await h.Window.Recents.ReadAsync();Assert.Equal("vsdx",state.Entries[0].Kind);Assert.Equal(other,state.LastOpenFolder);
  Assert.Equal("not_found",(await h.Window.Services.HandleAsync(Request("host.openJson",new(){["recentId"]=state.Entries[0].Id}),default)).Error?.Code);
  Assert.Null((await h.Window.Services.HandleAsync(Request("host.openVsdx",new(){["recentId"]=state.Entries[0].Id}),default)).Error);
  await h.Eval("document.querySelector('[data-testid=menu-file]').click(); true");await WaitFor(h,"document.querySelector('[data-testid=menu-recent]') !== null");await h.Eval("document.querySelector('[data-testid=menu-recent]').click();true");await WaitFor(h,"document.querySelector('[data-testid=recent-entry]') !== null");
  var evidence=Path.Combine(Harness.RepoRoot,"artifacts/w05-recent-files");Directory.CreateDirectory(evidence);await File.WriteAllBytesAsync(Path.Combine(evidence,"recent-dialog.png"),await h.OnUi(()=>h.Host.CapturePreviewAsync()));
 }
 [Fact] public async Task HistoryFailureDoesNotReportASuccessfulDrawingSaveAsFailed()
 {
  await using var h=await Harness.StartAsync();await h.Window.RecoveryMgr.WhenSessionReadyAsync();var file=Path.Combine(h.Root,"kept.diagram.json");Directory.CreateDirectory(h.Window.Recents.File);h.Window.Services.DialogOverride=_=>file;
  var saved=await h.Window.Services.HandleAsync(Request("host.saveJson"),default);Assert.Null(saved.Error);Assert.True(File.Exists(file));Assert.Contains("succeeded",saved.Result!.Value.GetProperty("warning").GetString());Assert.Equal(file,h.Window.Services.CurrentPath);
 }
 static async Task WaitFor(Harness h,string expression){var end=DateTime.UtcNow.AddSeconds(15);while(await h.Eval(expression)!="true"){if(DateTime.UtcNow>end)throw new TimeoutException(expression);await Task.Delay(50);}}
}
