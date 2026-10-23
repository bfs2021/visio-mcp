using System.IO;
using System.IO.Compression;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Microsoft.Win32;
namespace Diagram.App;

public sealed partial class ExportService
{
    const int ExportBytesLimit=64*1024*1024;
    sealed class ExportRejected(AppError error):Exception(error.Message) { public AppError Error=>error; }
    [DoesNotReturn]
    static void Refuse(string code,string message)=>throw new ExportRejected(new AppError {Retryable=false,Code=code,Message=message});
    async Task<Snapshot> CheckExportAsync(ExpectedState expected,CancellationToken ct)
    {
        var scope=new Scope {DocumentId=expected.DocumentId,SessionId=expected.SessionId};
        if(window.Router.CurrentScope!=scope) Refuse("session_mismatch","The active drawing changed. Open Export again.");
        var snap=await window.Router.SnapshotAsync(scope,ct);
        if(!snap.IsOk)throw new ExportRejected(snap.Error!);
        if(window.Router.CurrentScope!=scope)Refuse("session_mismatch","The active drawing changed during export.");
        if(snap.Value.Revision!=expected.BaseRevision)Refuse("revision_conflict","The drawing changed. Open Export again so every page uses the same revision.");
        return snap.Value;
    }
    sealed class ChosenDestination(string path):IExportConsent
    {
        public Task<bool> AskAsync(ConsentRequest r,CancellationToken ct)=>Task.FromResult(StringComparer.OrdinalIgnoreCase.Equals(r.Path,Path.GetFullPath(path)));
    }
    public async Task<ResponseEnvelope> HandleEditorAsync(RequestEnvelope request,CancellationToken ct)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);lifetime.CancelAfter(TimeSpan.FromMinutes(8));ct=lifetime.Token;
        try
        {
            var p=request.Params;
            var expected=p["expected"]?.Deserialize<ExpectedState>(ContractJson.Options);if(expected is null){Refuse("invalid_request","Export needs the drawing identity and revision.");return null!;}
            var format=p["format"]?.GetValue<string>()??"";var area=p["scope"]?.GetValue<string>()??"";
            var scale=p["scale"]?.Deserialize<double>()??1;var background=p["background"]?.GetValue<string>()??"page";var copy=p["copy"]?.GetValue<bool>()==true;
            var ids=p["elementIds"]?.Deserialize<string[]>()??[];
            if(format is not ("png" or "jpeg" or "svg" or "pdf") || area is not ("page" or "selection" or "all") || !double.IsFinite(scale)||scale<=0||scale>8 || background is not ("page" or "white" or "transparent"))Refuse("invalid_request","Invalid export format, scope, scale or background.");
            if(format=="pdf" && (area!="all"||background=="transparent") || format=="jpeg"&&background=="transparent" || copy&&(format!="png"||area!="selection"))Refuse("invalid_request","PDF exports all pages; JPEG/PDF require an opaque background; Copy uses a PNG selection.");
            if(area=="selection"&&(ids.Length==0||ids.Length>500))Refuse("invalid_request","Select between 1 and 500 objects to export.");
            var snap=await CheckExportAsync(expected,ct);var scope=new Scope {DocumentId=expected.DocumentId,SessionId=expected.SessionId};
            var pageId=p["pageId"]?.GetValue<string>();var pages=area=="all"?snap.Document.Pages.ToArray():snap.Document.Pages.Where(x=>x.Id==(pageId??snap.Document.Pages.First().Id)).ToArray();
            if(pages.Length==0)Refuse("not_found","The export page is no longer available.");
            if(pages.Length>32)Refuse("limit_exceeded","Export at most 32 pages at a time.");
            var renderScale=format=="pdf"?1:scale;
            if(area!="selection"&&(pages.Any(x=>x.WidthPt*renderScale>8192||x.HeightPt*renderScale>8192||x.WidthPt*x.HeightPt*renderScale*renderScale>16000000) || pages.Sum(x=>x.WidthPt*x.HeightPt*renderScale*renderScale)>64000000))Refuse("limit_exceeded","The requested export is too large. Choose a smaller scale or fewer pages.");
            var destinationFormat=area=="all"&&format!="pdf"?"zip":format;
            ApprovedDestination? destination=null;ExportPathPolicy? policy=null;
            if(!copy)
            {
                // Keep the selected filename in the host; web content never supplies a destination.
                var path=await window.Dispatcher.InvokeAsync(()=>{
                    if(window.Services.DialogOverride is {} d)return d("exportImage");
                    var ext=destinationFormat=="jpeg"?"jpg":destinationFormat;
                    var dialog=new SaveFileDialog {Title="Export diagram",Filter=$"{destinationFormat.ToUpperInvariant()} (*.{ext})|*.{ext}",DefaultExt="."+ext,AddExtension=true};
                    return dialog.ShowDialog(window)==true?dialog.FileName:null;
                });
                if(path is null)return Ok(request,new JsonObject {["cancelled"]=true});
                await CheckExportAsync(expected,ct);
                policy=new ExportPathPolicy(ExportRoot,ApprovedRoots,()=>window.Router.CurrentScope,new ChosenDestination(path),editorFormats:true);
                var chosen=await policy.ResolveAsync(path,destinationFormat,snap.Document.Title,scope,snap.Revision,"Editor",ct);if(!chosen.IsOk)throw new ExportRejected(chosen.Error!);destination=chosen.Value;
            }
            var images=new List<(Page Page,byte[] Bytes)>();long total=0;
            foreach(var page in pages)
            {
                await CheckExportAsync(expected,ct);
                var args=new JsonObject {["scope"]=area=="selection"?"region":"page",["pageId"]=page.Id,["format"]=format=="pdf"?"svg":format,["scale"]=format=="pdf"?1:scale,["background"]=background,["mode"]="clean"};
                if(area=="selection"){args["elementIds"]=JsonSerializer.SerializeToNode(ids);args["paddingPt"]=12;args["selectionOnly"]=true;}
                var rendered=await window.Router.RenderAsync(scope,args,ct);if(!rendered.IsOk)throw new ExportRejected(rendered.Error!);
                if(rendered.Value.GetProperty("revision").GetInt32()!=expected.BaseRevision)Refuse("revision_conflict","The drawing changed during export.");
                var b=Convert.FromBase64String(rendered.Value.GetProperty("base64").GetString()!);total+=b.Length;if(total>ExportBytesLimit)Refuse("limit_exceeded","Export exceeds 64 MiB.");images.Add((page,b));
            }
            byte[] output;
            if(format=="pdf")output=await PdfExport.CreateAsync(window,images,ct);
            else if(destinationFormat=="zip")
            {
                using var ms=new MemoryStream();using(var zip=new ZipArchive(ms,ZipArchiveMode.Create,true))for(var i=0;i<images.Count;i++){var safeName=new string(images[i].Page.Name.Select(c=>char.IsLetterOrDigit(c)?c:'-').Take(60).ToArray());var entry=zip.CreateEntry($"{i+1:D3}-{safeName}.{(format=="jpeg"?"jpg":format)}");using var stream=entry.Open();stream.Write(images[i].Bytes);}output=ms.ToArray();
            }
            else output=images[0].Bytes;
            if(output.Length>ExportBytesLimit)Refuse("limit_exceeded","Export exceeds 64 MiB.");
            await CheckExportAsync(expected,ct);
            if(copy)
            {
                await window.Dispatcher.InvokeAsync(()=>{
                    ct.ThrowIfCancellationRequested();
                    if(window.Router.CurrentScope!=scope)Refuse("session_mismatch","The active drawing changed before Copy as image.");
                    if(window.Router.CurrentRevision!=expected.BaseRevision)Refuse("revision_conflict","The drawing changed before Copy as image.");
                    using var ms=new MemoryStream(output);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=ms;bitmap.EndInit();bitmap.Freeze();
                    var data=new DataObject();data.SetImage(bitmap);using var png=new MemoryStream(output);data.SetData("PNG",png);Clipboard.SetDataObject(data,true);
                });
                return Ok(request,new JsonObject {["copied"]=true,["pages"]=1});
            }
            async Task Guard(CancellationToken token){await CheckExportAsync(expected,token);var v=policy!.Revalidate(destination!,scope);if(!v.IsOk)throw new ExportRejected(v.Error!);}
            await Guard(ct);await window.Writer.WriteAsync(destination!.Path,output,ct,Guard);
            return Ok(request,new JsonObject {["path"]=destination.Path,["pages"]=images.Count,["bytes"]=output.Length,["revision"]=expected.BaseRevision});
        }
        catch(OperationCanceledException){return new ResponseEnvelope {RequestId=request.RequestId,Error=new AppError {Code="cancelled",Message="Export timed out or was cancelled. No new export was published.",Retryable=false}};}
        catch(TimeoutException){return new ResponseEnvelope {RequestId=request.RequestId,Error=new AppError {Code="export_failed",Message="PDF generation did not finish. No new export was published.",Retryable=false}};}
        catch(ExportRejected e){return new ResponseEnvelope {RequestId=request.RequestId,Error=e.Error};}
        catch(Exception e)when(e is JsonException or InvalidOperationException or FormatException or ArgumentException){return new ResponseEnvelope {RequestId=request.RequestId,Error=new AppError {Retryable=false,Code="invalid_request",Message=e.Message}};}
        catch(System.Runtime.InteropServices.COMException e){return new ResponseEnvelope {RequestId=request.RequestId,Error=new AppError {Retryable=false,Code="clipboard_unavailable",Message="The clipboard is busy. Try Copy as image again. "+e.Message}};}
    }
}
