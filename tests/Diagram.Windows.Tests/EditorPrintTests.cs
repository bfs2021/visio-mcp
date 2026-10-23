using Diagram.Core.Contracts;
using Diagram.App;
using Diagram.Host.Core;
using Xunit;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diagram.Windows.Tests;

public class EditorPrintTests
{
    static ExpectedState State(string document = "doc", string session = "session", int revision = 4) =>
        new() { DocumentId = document, SessionId = session, BaseRevision = revision };

    static IReadOnlyList<PrintSheet> Sheets(string svg = "<svg/>") =>
        [new PrintSheet(1, 612, 792, [1], svg)];
    static PrintLayoutOptions Layout => new(612, 792, "portrait", "fit", new PrintMargins(36, 36, 36, 36), new PrintMargins(0, 0, 0, 0));
    static PrintDeviceSettings Device => new("printer-a", "letter", 612, 792, "portrait", new PrintMargins(36, 36, 36, 36));

    [Fact]
    public void PreparedJobsAreOpaqueBoundedAndOneUse()
    {
        var store = new PrintJobStore();
        var job = store.Prepare(State(), Sheets(), Layout, Device);
        Assert.NotEmpty(job.Id);
        Assert.DoesNotContain("doc", job.Id);
        Assert.True(store.TryTake(job.Id, State(), out var taken));
        Assert.Equal(job.Sheets, taken!.Sheets);
        Assert.False(store.TryTake(job.Id, State(), out _));
    }

    [Theory]
    [InlineData("other", "session", 4, "session_mismatch")]
    [InlineData("doc", "other", 4, "session_mismatch")]
    [InlineData("doc", "session", 5, "revision_conflict")]
    public void WrongScopeOrRevisionConsumesAndRejectsJob(string document, string session, int revision, string error)
    {
        var store = new PrintJobStore();
        var job = store.Prepare(State(), Sheets(), Layout, Device);
        Assert.False(store.TryTake(job.Id, State(document, session, revision), out _, out var code));
        Assert.Equal(error, code);
        Assert.False(store.TryTake(job.Id, State(), out _));
    }

    [Fact]
    public void NewPreviewReplacesOldAndCanBeClearedByDocument()
    {
        var store = new PrintJobStore();
        var first = store.Prepare(State(), Sheets(), Layout, Device);
        var second = store.Prepare(State("next", "session-2"), Sheets(), Layout, Device);
        Assert.False(store.TryTake(first.Id, State(), out _));
        store.Clear("next");
        Assert.False(store.TryTake(second.Id, State("next", "session-2"), out _));
    }

    [Fact]
    public void ExpiredAndOversizedJobsAreRejected()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = new PrintJobStore(time);
        var job = store.Prepare(State(), Sheets(), Layout, Device);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(store.TryTake(job.Id, State(), out _, out var code));
        Assert.Equal("job_expired", code);
        Assert.Throws<PrintLayoutException>(() => { store.Prepare(State(), Sheets(new string('x', 65 * 1024 * 1024)), Layout, Device); });
    }

    sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        DateTimeOffset current = initial;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }

    sealed class FakePrinters(params PrintDevice[] devices) : IPrinterCapabilities
    {
        public IReadOnlyList<PrintDevice> GetDevices() => devices;
    }

    static RequestEnvelope PrintRequest(string action, JsonObject? parameters = null) => new()
    {
        RequestId = Guid.NewGuid().ToString(), Method = "host.print", Params = parameters ?? new JsonObject { ["action"] = action }
    };

    static JsonObject PreviewOptions(Harness h) => new()
    {
        ["action"] = "preview",
        ["expected"] = JsonSerializer.SerializeToNode(new ExpectedState { DocumentId = h.Router.CurrentScope!.DocumentId, SessionId = h.Router.CurrentScope.SessionId, BaseRevision = h.Router.CurrentRevision }, ContractJson.Options),
        ["pageRange"] = "1", ["printerId"] = "printer-a", ["mediaId"] = "letter", ["orientation"] = "portrait", ["layout"] = "fit",
        ["margins"] = new JsonObject { ["leftPt"] = 10, ["topPt"] = 10, ["rightPt"] = 10, ["bottomPt"] = 10 },
    };

    [Fact]
    public async Task HostPrintDevicesAndPreviewReturnExactPinnedSheetsAndMarginWarnings()
    {
        await using var h = await Harness.StartAsync();
        h.Window.Exports.PrinterCapabilities = new FakePrinters(new PrintDevice("printer-a", "Printer A", [new PrintMedia("letter", "Letter", 612, 792, new PrintMargins(18, 18, 18, 18))]));
        var devices = await h.Window.Services.HandleAsync(PrintRequest("devices"), Harness.Ct(30));
        Assert.Null(devices.Error);
        Assert.Equal("printer-a", devices.Result!.Value.GetProperty("devices")[0].GetProperty("id").GetString());
        var preview = await h.Window.Services.HandleAsync(PrintRequest("preview", PreviewOptions(h)), Harness.Ct(60));
        Assert.Null(preview.Error);
        var data = preview.Result!.Value;
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("jobId").GetString()));
        Assert.Equal(1, data.GetProperty("sheets").GetArrayLength());
        Assert.Contains("<svg", data.GetProperty("sheets")[0].GetProperty("svg").GetString());
        Assert.Single(data.GetProperty("marginWarnings").EnumerateArray());
        Assert.Equal(18, data.GetProperty("settings").GetProperty("effectiveMargins").GetProperty("leftPt").GetDouble());
    }

    [Fact]
    public async Task UnsupportedPrinterAndNoDevicesAreReportedWithoutCreatingAJob()
    {
        await using var h = await Harness.StartAsync();
        h.Window.Exports.PrinterCapabilities = new FakePrinters();
        var devices = await h.Window.Services.HandleAsync(PrintRequest("devices"), Harness.Ct(30));
        Assert.Empty(devices.Result!.Value.GetProperty("devices").EnumerateArray());
        Assert.Equal("unsupported_setting", (await h.Window.Services.HandleAsync(PrintRequest("preview", PreviewOptions(h)), Harness.Ct(60))).Error?.Code);
        Assert.False(h.Window.Exports.PrintJobs.HasPendingJob);
    }

    [Fact]
    public async Task InvalidSettingsAndRevisionMismatchDoNotExposePreparedJobs()
    {
        await using var h = await Harness.StartAsync();
        h.Window.Exports.PrinterCapabilities = new FakePrinters(new PrintDevice("printer-a", "Printer A", [new PrintMedia("letter", "Letter", 612, 792, new PrintMargins(0, 0, 0, 0))]));
        var invalid = PreviewOptions(h); invalid["orientation"] = "diagonal";
        Assert.Equal("unsupported_setting", (await h.Window.Services.HandleAsync(PrintRequest("preview", invalid), Harness.Ct(60))).Error?.Code);
        Assert.False(h.Window.Exports.PrintJobs.HasPendingJob);
        var stale = PreviewOptions(h); stale["expected"]!["baseRevision"] = h.Router.CurrentRevision + 1;
        Assert.Equal("revision_conflict", (await h.Window.Services.HandleAsync(PrintRequest("preview", stale), Harness.Ct(60))).Error?.Code);
        Assert.False(h.Window.Exports.PrintJobs.HasPendingJob);
    }

    [Fact]
    public async Task OutputRequiresOneFreshPinnedJobAndRunsCallbackOnlyAfterFinalGuard()
    {
        await using var h = await Harness.StartAsync();
        h.Window.Exports.PrinterCapabilities = new FakePrinters(new PrintDevice("printer-a", "Printer A", [new PrintMedia("letter", "Letter", 612, 792, new PrintMargins(0, 0, 0, 0))]));
        var calls = 0;
        h.Window.Exports.PrintOutputHandler = async (_, _, guard, ct) =>
        {
            await guard(ct);
            calls++;
            return new JsonObject { ["printed"] = true };
        };
        var prepared = await h.Window.Services.HandleAsync(PrintRequest("preview", PreviewOptions(h)), Harness.Ct(60));
        Assert.Null(prepared.Error);
        var id = prepared.Result!.Value.GetProperty("jobId").GetString()!;
        Assert.Null((await h.Window.Services.HandleAsync(PrintRequest("print", new JsonObject { ["action"] = "print", ["jobId"] = id }), Harness.Ct(30))).Error);
        Assert.Equal(1, calls);
        Assert.Equal("job_not_found", (await h.Window.Services.HandleAsync(PrintRequest("print", new JsonObject { ["action"] = "print", ["jobId"] = id }), Harness.Ct(30))).Error?.Code);

        prepared = await h.Window.Services.HandleAsync(PrintRequest("preview", PreviewOptions(h)), Harness.Ct(60));
        id = prepared.Result!.Value.GetProperty("jobId").GetString()!;
        Assert.Null((await h.Apply(h.Router.CurrentRevision, null, Harness.Shape("change-after-preview"))).Error);
        Assert.Equal("revision_conflict", (await h.Window.Services.HandleAsync(PrintRequest("print", new JsonObject { ["action"] = "print", ["jobId"] = id }), Harness.Ct(30))).Error?.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SavePrintPdfUsesThePreparedSheetDimensionsAndHostSelectedPath()
    {
        await using var h = await Harness.StartAsync();
        h.Window.Exports.PrinterCapabilities = new FakePrinters(new PrintDevice("printer-a", "Printer A", [new PrintMedia("letter", "Letter", 612, 792, new PrintMargins(0, 0, 0, 0))]));
        var path = Path.Combine(h.Root, "print-layout.pdf");
        h.Window.Services.DialogOverride = kind => kind == "printPdf" ? path : null;
        var prepared = await h.Window.Services.HandleAsync(PrintRequest("preview", PreviewOptions(h)), Harness.Ct(60));
        Assert.Null(prepared.Error);
        var id = prepared.Result!.Value.GetProperty("jobId").GetString()!;
        var response = await h.Window.Services.HandleAsync(PrintRequest("savePdf", new JsonObject { ["action"] = "savePdf", ["jobId"] = id }), Harness.Ct(90));
        Assert.Null(response.Error);
        Assert.Equal(path, response.Result!.Value.GetProperty("path").GetString());
        var pdf = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        Assert.StartsWith("%PDF-", pdf);
        var box = System.Text.RegularExpressions.Regex.Match(pdf, @"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]");
        Assert.True(box.Success);
        Assert.InRange(double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 611, 613);
        Assert.InRange(double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 791, 793);
    }

    [Fact]
    public async Task PhysicalDispatchUsesThePreparedDeviceAndDoesNotFallBack()
    {
        await using var h = await Harness.StartAsync();
        h.Window.Exports.PrinterCapabilities = new FakePrinters(new PrintDevice("printer-a", "Printer A", [new PrintMedia("letter", "Letter", 612, 792, new PrintMargins(0, 0, 0, 0))]));
        PreparedPrintJob? dispatched = null;
        var attempts = 0;
        h.Window.Exports.PhysicalPrintDispatcher = (job, _) =>
        {
            attempts++;
            dispatched = job;
            return Task.FromResult(Microsoft.Web.WebView2.Core.CoreWebView2PrintStatus.PrinterUnavailable);
        };
        var prepared = await h.Window.Services.HandleAsync(PrintRequest("preview", PreviewOptions(h)), Harness.Ct(60));
        var id = prepared.Result!.Value.GetProperty("jobId").GetString()!;
        var response = await h.Window.Services.HandleAsync(PrintRequest("print", new JsonObject { ["action"] = "print", ["jobId"] = id }), Harness.Ct(30));
        Assert.Equal("printer_unavailable", response.Error?.Code);
        Assert.Equal(1, attempts);
        Assert.Equal("printer-a", dispatched!.Device.PrinterName);
        Assert.Equal("letter", dispatched.Device.MediaId);
        Assert.Equal(612, dispatched.Sheets[0].PaperWidthPt);
        Assert.Equal(792, dispatched.Sheets[0].PaperHeightPt);
    }

    [Fact]
    public async Task InvalidRangesAndTileExpansionFailBeforePreparingAJob()
    {
        await using var h = await Harness.StartAsync();
        h.Window.Exports.PrinterCapabilities = new FakePrinters(new PrintDevice("printer-a", "Printer A", [new PrintMedia("tiny", "Tiny", 10, 10, new PrintMargins(0, 0, 0, 0))]));
        var invalid = PreviewOptions(h); invalid["pageRange"] = "1,";
        Assert.Equal("invalid_range", (await h.Window.Services.HandleAsync(PrintRequest("preview", invalid), Harness.Ct(60))).Error?.Code);
        Assert.False(h.Window.Exports.PrintJobs.HasPendingJob);
        var expanding = PreviewOptions(h); expanding["mediaId"] = "tiny"; expanding["layout"] = "tile";
        expanding["margins"] = new JsonObject { ["leftPt"] = 0, ["topPt"] = 0, ["rightPt"] = 0, ["bottomPt"] = 0 };
        Assert.Equal("limit_exceeded", (await h.Window.Services.HandleAsync(PrintRequest("preview", expanding), Harness.Ct(60))).Error?.Code);
        Assert.False(h.Window.Exports.PrintJobs.HasPendingJob);
    }
}
