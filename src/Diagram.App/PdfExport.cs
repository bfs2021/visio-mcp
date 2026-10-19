using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Diagram.Core.Contracts;
namespace Diagram.App;

/// <summary>Prints a host-built, offline document. It never navigates the editor or reads a caller-supplied URL.</summary>
static class PdfExport
{
    public static Task<byte[]> CreateAsync(MainWindow owner,IReadOnlyList<(Page Page,byte[] Bytes)> pages,CancellationToken ct)=>owner.Dispatcher.InvokeAsync(async()=>
    {
        using var view=new WebView2();
        var shell=new Window {Owner=owner,Content=view,Width=900,Height=700,Left=-30000,Top=0,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            shell.Show();await view.EnsureCoreWebView2Async(owner.Web.CoreWebView2.Environment);
            var events=new List<string>();var core=view.CoreWebView2;core.Settings.IsScriptEnabled=false;core.Settings.IsWebMessageEnabled=false;core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDevToolsEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;
            const string origin="https://diagram-print.invalid/";
            var html=new StringBuilder("<!doctype html><meta charset=utf-8><meta http-equiv=Content-Security-Policy content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><style>html,body{margin:0;padding:0}img{display:block}section{break-after:page;overflow:hidden}section:last-child{break-after:auto}");
            string N(double n)=>n.ToString("0.########",CultureInfo.InvariantCulture);
            for(var i=0;i<pages.Count;i++)html.Append($"@page p{i}{{size:{N(pages[i].Page.WidthPt)}pt {N(pages[i].Page.HeightPt)}pt;margin:0}}#p{i}{{page:p{i};width:{N(pages[i].Page.WidthPt)}pt;height:{N(pages[i].Page.HeightPt)}pt}}");
            html.Append("</style>");for(var i=0;i<pages.Count;i++)html.Append($"<section id=p{i}><img width='{N(pages[i].Page.WidthPt*96/72)}' height='{N(pages[i].Page.HeightPt*96/72)}' src='data:image/svg+xml;base64,{Convert.ToBase64String(pages[i].Bytes)}'></section>");
            var bytes=Encoding.UTF8.GetBytes(html.ToString());
            core.NavigationStarting+=(_,e)=>{events.Add("navigate "+e.Uri);e.Cancel=e.Uri!=origin;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(_,e)=>{events.Add("resource "+e.Request.Uri);e.Response=core.Environment.CreateWebResourceResponse(e.Request.Uri==origin?new MemoryStream(bytes):new MemoryStream(),e.Request.Uri==origin?200:403,"OK", "Content-Type: text/html; charset=utf-8");};
            var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted+=(_,e)=>{events.Add("complete "+e.IsSuccess+" "+e.WebErrorStatus);if(e.IsSuccess)ready.TrySetResult();else ready.TrySetException(new IOException("The PDF print document could not load."));};
            core.Navigate(origin);try{await ready.Task.WaitAsync(TimeSpan.FromSeconds(20),ct);}catch(TimeoutException){throw new IOException("PDF print page did not load: "+string.Join("; ",events));}
            var until=DateTime.UtcNow.AddSeconds(15);
            while(await core.ExecuteScriptAsync("Array.from(document.images).every(i=>i.complete && i.naturalWidth>0)")!="true") {if(DateTime.UtcNow>until)throw new IOException("PDF page images did not finish loading.");await Task.Delay(30,ct);}
            // PrintToPdfStreamAsync normalizes CSS page sizes to its global paper size.
            // The same Chromium PDF engine exposes the required per-page sizing flag through CDP.
            var result=await core.CallDevToolsProtocolMethodAsync("Page.printToPDF",System.Text.Json.JsonSerializer.Serialize(new {preferCSSPageSize=true,printBackground=true,displayHeaderFooter=false,marginTop=0,marginBottom=0,marginLeft=0,marginRight=0})).WaitAsync(TimeSpan.FromSeconds(45),ct);
            using var json=System.Text.Json.JsonDocument.Parse(result);var data=json.RootElement.GetProperty("data").GetString()!;
            if(data.Length>90*1024*1024)throw new IOException("PDF exceeds 64 MiB.");var output=Convert.FromBase64String(data);if(output.Length>64*1024*1024)throw new IOException("PDF exceeds 64 MiB.");return output;
        }
        finally {shell.Content=null;shell.Close();}
    }).Task.Unwrap();
}
