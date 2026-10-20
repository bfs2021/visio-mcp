using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Diagram.App;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

[CollectionDefinition("Native clipboard", DisableParallelization = true)]
public sealed class NativeClipboardCollection { }

[Collection("Native clipboard")]
public sealed class ClipboardIngressTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool CloseClipboard();
    static async Task<ResponseEnvelope> Call(Harness h, string method, JsonObject parameters) => await h.Window.Services.HandleAsync(new RequestEnvelope
    { RequestId = Guid.NewGuid().ToString(), Method = method, Params = parameters }, Harness.Ct());

    static WriteableBitmap Bitmap()
    {
        var bitmap = new WriteableBitmap(4, 3, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, 4, 3), Enumerable.Repeat((byte)0x88, 4 * 3 * 4).ToArray(), 4 * 4, 0);
        return bitmap;
    }

    [Fact]
    public async Task ExplicitPasteCapturesOnlyCurrentBitmapAndImportsItThroughTheApprovedLibraryPath()
    {
        await using var h = await Harness.StartAsync();
        var original = await h.Ui.InvokeAsync(() => Clipboard.GetDataObject());
        try
        {
            await h.Ui.InvokeAsync(() => Clipboard.SetImage(Bitmap()));
            var described = await Call(h, IngressHostMethods.ClipboardDescribe, new JsonObject());
            Assert.Null(described.Error);
            var sequence = described.Result!.Value.GetProperty("sequence").GetInt64();
            Assert.True(described.Result.Value.GetProperty("hasBitmap").GetBoolean());
            var scope = h.Router.CurrentScope!;
            var minted = await Call(h, IngressHostMethods.ClipboardImportImage, new JsonObject
            { ["expectedSequence"] = sequence, ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId });
            Assert.Null(minted.Error);
            var item = minted.Result!.Value.GetProperty("item");
            Assert.Equal("image", item.GetProperty("kind").GetString());
            var before = (await h.Send("doc.status", scope: scope)).Result!.Value.GetProperty("revision").GetInt32();
            var imported = await Call(h, IngressHostMethods.ImportImage, new JsonObject
            { ["ticketId"] = item.GetProperty("ticketId").GetString(), ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId });
            Assert.Null(imported.Error);
            Assert.Equal(4, imported.Result!.Value.GetProperty("width").GetInt32());
            Assert.Equal(3, imported.Result.Value.GetProperty("height").GetInt32());
            Assert.Equal(before, (await h.Send("doc.status", scope: scope)).Result!.Value.GetProperty("revision").GetInt32());
            Assert.Single(await h.Window.Library.ListAsync(null, null, Harness.Ct()));
        }
        finally
        {
            await h.Ui.InvokeAsync(() => { try { if (original is not null) Clipboard.SetDataObject(original, true); else Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { } });
        }
    }

    [Fact]
    public async Task InternalMarkerMatchesButNewerExternalBitmapReplacesItAndSequenceRacesFailClosed()
    {
        await using var h = await Harness.StartAsync();
        var original = await h.Ui.InvokeAsync(() => Clipboard.GetDataObject());
        try
        {
            await h.Ui.InvokeAsync(() => Clipboard.SetText("ordinary text"));
            var marked = await Call(h, IngressHostMethods.ClipboardMarkInternalCopy, new JsonObject { ["copyId"] = "copy-test-1" });
            Assert.Null(marked.Error);
            var same = await Call(h, IngressHostMethods.ClipboardDescribe, new JsonObject());
            Assert.Equal("copy-test-1", same.Result!.Value.GetProperty("internalCopyId").GetString());
            await h.Ui.InvokeAsync(() => Clipboard.SetImage(Bitmap()));
            var newer = await Call(h, IngressHostMethods.ClipboardDescribe, new JsonObject());
            Assert.Null(newer.Error);
            Assert.True(newer.Result!.Value.GetProperty("hasBitmap").GetBoolean());
            Assert.Null(newer.Result.Value.GetProperty("internalCopyId").GetString());
            var scope = h.Router.CurrentScope!;
            var stale = await Call(h, IngressHostMethods.ClipboardImportImage, new JsonObject
            { ["expectedSequence"] = newer.Result.Value.GetProperty("sequence").GetInt64() - 1, ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId });
            Assert.Equal("clipboard_changed", stale.Error?.Code);
            Assert.Empty(h.Window.Services.IngressTickets.DescribePending(h.Window.Services.IngressWindowId, BridgeRouter.User));
        }
        finally
        {
            await h.Ui.InvokeAsync(() => { try { if (original is not null) Clipboard.SetDataObject(original, true); else Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { } });
        }
    }

    [Fact]
    public async Task BusyNativeClipboardReturnsANonblockingFailure()
    {
        await using var h = await Harness.StartAsync();
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var opened = false;
        var owner = new Thread(() => { opened = OpenClipboard(IntPtr.Zero); held.Set(); release.Wait(); if (opened) CloseClipboard(); });
        owner.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5))); Assert.True(opened);
        try
        {
            var result = await Call(h, IngressHostMethods.ClipboardDescribe, new JsonObject());
            Assert.Equal("clipboard_unavailable", result.Error?.Code);
            Assert.True(result.Error?.Retryable);
        }
        finally { release.Set(); Assert.True(owner.Join(TimeSpan.FromSeconds(5))); }
    }
}
