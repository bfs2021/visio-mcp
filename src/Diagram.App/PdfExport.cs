using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Diagram.Core.Contracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Diagram.App;

/// <summary>Renders host-built offline print documents. It never navigates the editor or reads a caller-supplied URL.</summary>
static class PdfExport
{
    sealed record PrintPage(double WidthPt, double HeightPt, byte[] Bytes);

    // W08's PDF export deliberately preserves the original drawing-page dimensions and all-page behavior.
    public static Task<byte[]> CreateAsync(MainWindow owner, IReadOnlyList<(Page Page, byte[] Bytes)> pages, CancellationToken ct) =>
        CreatePdfAsync(owner, pages.Select(x => new PrintPage(x.Page.WidthPt, x.Page.HeightPt, x.Bytes)).ToArray(), ct);

    public static Task<byte[]> CreatePrintSheetsAsync(MainWindow owner, IReadOnlyList<PrintSheet> sheets, CancellationToken ct) =>
        CreatePdfAsync(owner, sheets.Select(x => new PrintPage(x.PaperWidthPt, x.PaperHeightPt, Encoding.UTF8.GetBytes(x.Svg))).ToArray(), ct);

    static Task<byte[]> CreatePdfAsync(MainWindow owner, IReadOnlyList<PrintPage> pages, CancellationToken ct) => owner.Dispatcher.InvokeAsync(async () =>
    {
        var (_, view, shell) = await LoadDocumentAsync(owner, pages, ct);
        try
        {
            var result = await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.printToPDF", JsonSerializer.Serialize(new
            {
                preferCSSPageSize = true, printBackground = true, displayHeaderFooter = false,
                marginTop = 0, marginBottom = 0, marginLeft = 0, marginRight = 0,
            })).WaitAsync(TimeSpan.FromSeconds(45), ct);
            using var json = JsonDocument.Parse(result);
            var data = json.RootElement.GetProperty("data").GetString()!;
            if (data.Length > 90 * 1024 * 1024) throw new IOException("PDF exceeds 64 MiB.");
            var output = Convert.FromBase64String(data);
            if (output.Length > PrintJobStore.MaximumBytes) throw new IOException("PDF exceeds 64 MiB.");
            return output;
        }
        finally { shell.Content = null; shell.Close(); }
    }).Task.Unwrap();

    public static Task<CoreWebView2PrintStatus> PrintAsync(MainWindow owner, PreparedPrintJob job, CancellationToken ct) => owner.Dispatcher.InvokeAsync(async () =>
    {
        var (_, view, shell) = await LoadDocumentAsync(owner,
            job.Sheets.Select(x => new PrintPage(x.PaperWidthPt, x.PaperHeightPt, Encoding.UTF8.GetBytes(x.Svg))).ToArray(), ct);
        try
        {
            var first = job.Sheets[0];
            var settings = view.CoreWebView2.Environment.CreatePrintSettings();
            settings.PrinterName = job.Device.PrinterName;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintSelectionOnly = false;
            settings.PageWidth = first.PaperWidthPt / 72d;
            settings.PageHeight = first.PaperHeightPt / 72d;
            settings.Orientation = job.Device.Orientation == "landscape" ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
            settings.MediaSize = MediaSize(job.Device.MediaId);
            // The sheet SVG already includes the reviewed margins. The browser must add none of its own.
            settings.MarginLeft = settings.MarginTop = settings.MarginRight = settings.MarginBottom = 0;
            return await view.CoreWebView2.PrintAsync(settings).WaitAsync(TimeSpan.FromMinutes(2), ct);
        }
        finally { shell.Content = null; shell.Close(); }
    }).Task.Unwrap();

    static CoreWebView2PrintMediaSize MediaSize(string id)
    {
        var normalized = id.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<CoreWebView2PrintMediaSize>())
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase)) return Enum.Parse<CoreWebView2PrintMediaSize>(name);
        // Exact custom dimensions are also set below. Unsupported custom sizes fail at dispatch; no other media is selected.
        return Enum.Parse<CoreWebView2PrintMediaSize>("Custom");
    }

    static async Task<(CoreWebView2 Core, WebView2 View, Window Shell)> LoadDocumentAsync(MainWindow owner, IReadOnlyList<PrintPage> pages, CancellationToken ct)
    {
        if (pages.Count is < 1 or > 32) throw new IOException("Print document must contain between 1 and 32 sheets.");
        var view = new WebView2();
        var shell = new Window { Owner = owner, Content = view, Width = 900, Height = 700, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            shell.Show();
            await view.EnsureCoreWebView2Async(owner.Web.CoreWebView2.Environment);
            var events = new List<string>();
            var core = view.CoreWebView2;
            core.Settings.IsScriptEnabled = false; core.Settings.IsWebMessageEnabled = false; core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
            const string origin = "https://diagram-print.invalid/";
            var html = new StringBuilder("<!doctype html><meta charset=utf-8><meta http-equiv=Content-Security-Policy content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><style>html,body{margin:0;padding:0}img{display:block;width:100%;height:100%}section{break-after:page;overflow:hidden}section:last-child{break-after:auto}");
            string N(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);
            for (var i = 0; i < pages.Count; i++) html.Append($"@page p{i}{{size:{N(pages[i].WidthPt)}pt {N(pages[i].HeightPt)}pt;margin:0}}#p{i}{{page:p{i};width:{N(pages[i].WidthPt)}pt;height:{N(pages[i].HeightPt)}pt}}");
            html.Append("</style>");
            for (var i = 0; i < pages.Count; i++) html.Append($"<section id=p{i}><img src='data:image/svg+xml;base64,{Convert.ToBase64String(pages[i].Bytes)}'></section>");
            var content = Encoding.UTF8.GetBytes(html.ToString());
            core.NavigationStarting += (_, e) => { events.Add("navigate " + e.Uri); e.Cancel = e.Uri != origin; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                events.Add("resource " + e.Request.Uri);
                e.Response = core.Environment.CreateWebResourceResponse(e.Request.Uri == origin ? new MemoryStream(content) : new MemoryStream(), e.Request.Uri == origin ? 200 : 403, "OK", "Content-Type: text/html; charset=utf-8");
            };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => { events.Add("complete " + e.IsSuccess + " " + e.WebErrorStatus); if (e.IsSuccess) ready.TrySetResult(); else ready.TrySetException(new IOException("The print document could not load.")); };
            core.Navigate(origin);
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (TimeoutException) { throw new IOException("Print page did not load: " + string.Join("; ", events)); }
            var until = DateTime.UtcNow.AddSeconds(15);
            while (await core.ExecuteScriptAsync("Array.from(document.images).every(i=>i.complete && i.naturalWidth>0)") != "true")
            { if (DateTime.UtcNow > until) throw new IOException("Print sheet images did not finish loading."); await Task.Delay(30, ct); }
            return (core, view, shell);
        }
        catch { shell.Content = null; shell.Close(); throw; }
    }
}
