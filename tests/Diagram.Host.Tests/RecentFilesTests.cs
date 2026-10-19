using Diagram.Host.Core;
using Xunit;
namespace Diagram.Host.Tests;
public class RecentFilesTests
{
 [Fact] public async Task TenUniqueNewestEntriesPersistWithStableCapabilitiesAndLastOpenFolder()
 {
  using var d=new TempDir(); var s=new RecentFilesStore(d.File("recent.json"),new AtomicFileWriter());
  for(var i=0;i<12;i++) await s.RecordAsync(d.File($"{i}.diagram.json"),"json",false);
  var list=await s.ReadAsync(); Assert.Equal(10,list.Entries.Count); Assert.EndsWith("11.diagram.json",list.Entries[0].Path); Assert.Null(list.LastOpenFolder);
  var id=list.Entries[2].Id; await s.RecordAsync(list.Entries[2].Path.ToUpperInvariant(),"json",true);
  var back=await new RecentFilesStore(s.File,new AtomicFileWriter()).ReadAsync(); Assert.Equal(10,back.Entries.Count); Assert.Equal(id,back.Entries[0].Id); Assert.NotNull(back.LastOpenFolder); Assert.Null(await s.ResolveAsync("invented","json")); Assert.Null(await s.ResolveAsync(id,"vsdx"));
 }
 [Fact] public async Task RemoveAndClearNeverDeleteDocumentsAndClearRemembersFolder()
 {
  using var d=new TempDir(); var s=new RecentFilesStore(d.File("recent.json"),new AtomicFileWriter()); var path=d.File("hello.diagram.json"); await File.WriteAllTextAsync(path,"untouched");
  await s.RecordAsync(path,"json",true); var state=await s.ReadAsync(); await s.RemoveAsync(state.Entries[0].Id); Assert.Null(await s.ResolveAsync(state.Entries[0].Id,"json")); Assert.Equal("untouched",await File.ReadAllTextAsync(path));
  await s.RecordAsync(path,"json",false); await s.ClearAsync(); var back=await s.ReadAsync(); Assert.Empty(back.Entries); Assert.Equal(state.LastOpenFolder,back.LastOpenFolder); Assert.True(File.Exists(path));
 }
 [Theory] [InlineData("{bad")] [InlineData("null")] [InlineData("{\"version\":99,\"entries\":[]}")]
 public async Task InvalidStoreWarnsAndCanRecover(string content)
 {
  using var d=new TempDir();var s=new RecentFilesStore(d.File("recent.json"),new AtomicFileWriter());await File.WriteAllTextAsync(s.File,content);var back=await s.ReadAsync();Assert.Empty(back.Entries);Assert.NotNull(back.Warning);await s.RecordAsync(d.File("ok.diagram.json"),"json",false);Assert.Single((await s.ReadAsync()).Entries);
 }
 [Fact] public async Task OversizeIsBoundedAndFailedAtomicUpdatePreservesHistory()
 {
  using var d=new TempDir();var writer=new AtomicFileWriter();var s=new RecentFilesStore(d.File("recent.json"),writer);await File.WriteAllTextAsync(s.File,new string('x',131073));Assert.NotNull((await s.ReadAsync()).Warning);await s.RecordAsync(d.File("ok.diagram.json"),"json",false);var before=await File.ReadAllBytesAsync(s.File);writer.BeforeReplace=()=>throw new IOException("injected");await Assert.ThrowsAsync<IOException>(()=>s.ClearAsync());Assert.Equal(before,await File.ReadAllBytesAsync(s.File));
 }
 [Fact] public async Task AvailabilityAndConcurrentUpdatesRemainConsistent()
 {
  using var d=new TempDir();var s=new RecentFilesStore(d.File("recent.json"),new AtomicFileWriter());var file=d.File("exists.diagram.json");await File.WriteAllTextAsync(file,"keep");
  Assert.Equal("available",await RecentFilesStore.AvailabilityAsync(file));Assert.Equal("missing",await RecentFilesStore.AvailabilityAsync(d.File("gone")));Assert.Equal("unavailable",await RecentFilesStore.AvailabilityAsync(Path.GetDirectoryName(file)!));
  await Task.WhenAll(Enumerable.Range(0,25).Select(i=>s.RecordAsync(d.File($"{i}.diagram.json"),"json",false)));Assert.Equal(10,(await s.ReadAsync()).Entries.Count);
  await s.RecordAsync(file,"json",true);var state=await s.ReadAsync();Assert.Single(state.Entries.Where(e=>e.Path==file));
  await File.WriteAllTextAsync(s.File,"{\"version\":1,\"entries\":[{\"id\":\"bad\",\"path\":\"relative\",\"kind\":\"json\"}]}");Assert.NotNull((await s.ReadAsync()).Warning);
 }
}
