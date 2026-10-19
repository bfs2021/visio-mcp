using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;
namespace Diagram.Windows.Tests;
public class EditorExportTests
{
    static Task<ResponseEnvelope> Export(Harness h, JsonObject p) => h.Window.Services.HandleAsync(new RequestEnvelope { RequestId=Guid.NewGuid().ToString(),Method="host.exportImage",Params=p },Harness.Ct(60));
    static JsonObject Options(Harness h,string format="png",string scope="page") => new() { ["expected"]=JsonSerializer.SerializeToNode(new ExpectedState { DocumentId=h.Router.CurrentScope!.DocumentId,SessionId=h.Router.CurrentScope.SessionId,BaseRevision=0 },ContractJson.Options),["format"]=format,["scope"]=scope,["pageId"]=null,["scale"]=1,["background"]="white" };
    [Fact] public async Task ImageExportCreatesRealPngAndPreservesSnapshot()
    {
        await using var h=await Harness.StartAsync();
        var path=Path.Combine(h.Root,"drawing.png"); h.Window.Services.DialogOverride=_=>path;
        var before=(await h.Send("doc.snapshot")).Result!.Value.GetRawText();
        var r=await Export(h,Options(h)); Assert.Null(r.Error);
        Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},File.ReadAllBytes(path)[..8]);
        Assert.Equal(before,(await h.Send("doc.snapshot")).Result!.Value.GetRawText());
    }
    [Fact] public async Task PdfPreservesMixedPageSizesAndZipHasOrderedImages()
    {
        await using var h=await Harness.StartAsync();
        Assert.Null((await h.Apply(0,null,new JsonObject {["op"]="addPage",["id"]=Guid.NewGuid().ToString(),["name"]="Portrait",["widthPt"]=300,["heightPt"]=600})).Error);
        var p=Options(h,"pdf","all");p["expected"]!["baseRevision"]=1;
        var path=Path.Combine(h.Root,"pages.pdf");h.Window.Services.DialogOverride=_=>path;
        var r=await Export(h,p);Assert.Null(r.Error);
        var pdf=System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));Assert.StartsWith("%PDF-",pdf);
        var boxes=System.Text.RegularExpressions.Regex.Matches(pdf,@"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]");Assert.Equal(2,boxes.Count);
        Assert.InRange(double.Parse(boxes[0].Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture),841,843);
        Assert.InRange(double.Parse(boxes[1].Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture),299,301);
        Assert.InRange(double.Parse(boxes[1].Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture),599,601);
        path=Path.Combine(h.Root,"pages.zip");p["format"]="svg";r=await Export(h,p);Assert.Null(r.Error);
        using var zip=System.IO.Compression.ZipFile.OpenRead(path);Assert.Equal(new[]{"001-Page-1.svg","002-Portrait.svg"},zip.Entries.Select(e=>e.FullName));
        var evidence=Path.Combine(Harness.RepoRoot,"artifacts/w08-editor-export");Directory.CreateDirectory(evidence);File.Copy(Path.Combine(h.Root,"pages.pdf"),Path.Combine(evidence,"mixed-pages.pdf"),true);File.Copy(path,Path.Combine(evidence,"pages.zip"),true);
    }
    [Fact] public async Task CancelAndDestinationChangesLeaveEveryVersionIntact()
    {
        await using var h=await Harness.StartAsync();var before=(await h.Send("doc.snapshot")).Result!.Value.GetRawText();
        h.Window.Services.DialogOverride=_=>null;Assert.True((await Export(h,Options(h))).Result!.Value.GetProperty("cancelled").GetBoolean());
        h.Window.Services.DialogOverride=_=>@"\\not-a-server\share\drawing.png";Assert.Equal("path_not_permitted",(await Export(h,Options(h))).Error?.Code);
        var path=Path.Combine(h.Root,"appeared.png");h.Window.Services.DialogOverride=_=>path;h.Window.Writer.BeforeReplace=()=>File.WriteAllText(path,"external version");
        Assert.Equal("destination_changed",(await Export(h,Options(h))).Error?.Code);Assert.Equal("external version",File.ReadAllText(path));h.Window.Writer.BeforeReplace=null;
        Assert.Equal(before,(await h.Send("doc.snapshot")).Result!.Value.GetRawText());
    }
    [Fact] public async Task ASelectionOnALargePageCanBeCopiedAsNativeBitmapAndPng()
    {
        await using var h=await Harness.StartAsync();var page=(await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0].GetProperty("id").GetString();
        Assert.Null((await h.Apply(0,null,Harness.Shape("copy"),new JsonObject {["op"]="setPage",["pageId"]=page,["patch"]=new JsonObject {["widthPt"]=10000,["heightPt"]=10000}})).Error);
        var p=Options(h,"png","selection");p["expected"]!["baseRevision"]=1;p["elementIds"]=new JsonArray("copy");p["copy"]=true;p["background"]="transparent";
        var original=await h.Ui.InvokeAsync(()=>System.Windows.Clipboard.GetDataObject());
        try{
            var r=await Export(h,p);Assert.Null(r.Error);Assert.True(r.Result!.Value.GetProperty("copied").GetBoolean());
            await h.Ui.InvokeAsync(()=>{var data=System.Windows.Clipboard.GetDataObject()!;Assert.Contains("PNG",data.GetFormats());Assert.True(data.GetDataPresent(System.Windows.DataFormats.Bitmap));var image=System.Windows.Clipboard.GetImage()!;Assert.Equal(74,image.PixelWidth);Assert.Equal(54,image.PixelHeight);});
        }finally{await h.Ui.InvokeAsync(()=>{try{if(original is not null)System.Windows.Clipboard.SetDataObject(original,true);else System.Windows.Clipboard.Clear();}catch(System.Runtime.InteropServices.COMException){}});}
    }
    [Fact] public async Task InvalidOrStaleOptionsNeverOpenADestinationOrPublish()
    {
        await using var h=await Harness.StartAsync();var asked=false;h.Window.Services.DialogOverride=_=>{asked=true;return null;};
        var p=Options(h);p["scale"]=0;Assert.Equal("invalid_request",(await Export(h,p)).Error?.Code);Assert.False(asked);
        p=Options(h);await h.Apply(0,null,Harness.Shape("later"));Assert.Equal("revision_conflict",(await Export(h,p)).Error?.Code);Assert.False(asked);
    }
}
