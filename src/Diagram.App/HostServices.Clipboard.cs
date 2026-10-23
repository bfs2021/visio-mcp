using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

public sealed partial class HostServices
{
    sealed class ClipboardImageLimitException : Exception { }

    sealed class ClipboardImageStream() : MemoryStream
    {
        static long Limit => InputLimits.RasterBytes;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > Limit) throw new ClipboardImageLimitException();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > Limit) throw new ClipboardImageLimitException();
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (Length + 1 > Limit) throw new ClipboardImageLimitException();
            base.WriteByte(value);
        }
    }

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    readonly object clipboardMarkerGate = new();
    string? clipboardCopyId;
    long clipboardCopySequence = -1;

    ResponseEnvelope DescribeClipboard(RequestEnvelope request)
    {
        try
        {
            var value = window.Dispatcher.Invoke(() =>
            {
                var data = System.Windows.Clipboard.GetDataObject();
                var sequence = (long)GetClipboardSequenceNumber();
                string? marker;
                lock (clipboardMarkerGate) marker = clipboardCopySequence == sequence ? clipboardCopyId : null;
                var bitmap = data is not null && (data.GetDataPresent(System.Windows.DataFormats.Bitmap, autoConvert: false) || data.GetDataPresent("PNG", autoConvert: false));
                var text = data is not null && (data.GetDataPresent(System.Windows.DataFormats.UnicodeText, autoConvert: false) ||
                    data.GetDataPresent(System.Windows.DataFormats.Text, autoConvert: false) || data.GetDataPresent(System.Windows.DataFormats.StringFormat, autoConvert: false));
                return new ClipboardDescriptor(sequence, marker is { Length: <= 128 } ? marker : null, bitmap, text);
            });
            return Ok(request, new JsonObject
            {
                ["sequence"] = value.Sequence, ["internalCopyId"] = value.InternalCopyId,
                ["hasBitmap"] = value.HasBitmap, ["hasText"] = value.HasText,
            });
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { return Err(request, "clipboard_unavailable", "The clipboard is busy or unavailable.", retryable: true); }
    }

    ResponseEnvelope MarkInternalClipboardCopy(RequestEnvelope request)
    {
        ClipboardMarkInternalCopyRequest? input;
        try { input = request.Params.Deserialize<ClipboardMarkInternalCopyRequest>(ContractJson.Options); }
        catch (JsonException) { return Err(request, "invalid_request", "The internal copy marker is invalid."); }
        if (input is null || input.CopyId.Length is < 1 or > 128 || input.CopyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            return Err(request, "invalid_request", "The internal copy marker is invalid.");
        try
        {
            var sequence = window.Dispatcher.Invoke(() => (long)GetClipboardSequenceNumber());
            lock (clipboardMarkerGate) { clipboardCopyId = input.CopyId; clipboardCopySequence = sequence; }
            return Ok(request, new JsonObject { ["sequence"] = sequence });
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { return Err(request, "clipboard_unavailable", "The internal copy is available in memory, but the native clipboard could not be marked.", retryable: true); }
    }

    async Task<ResponseEnvelope> ImportClipboardImageAsync(RequestEnvelope request, CancellationToken ct)
    {
        ClipboardImportImageRequest? input;
        try { input = request.Params.Deserialize<ClipboardImportImageRequest>(ContractJson.Options); }
        catch (JsonException) { return Err(request, "invalid_request", "The clipboard image request is invalid."); }
        if (input is null || input.ExpectedSequence < 0 || string.IsNullOrWhiteSpace(input.DocumentId) || string.IsNullOrWhiteSpace(input.SessionId))
            return Err(request, "invalid_request", "A clipboard sequence and target editor are required.");
        var scope = new Scope { DocumentId = input.DocumentId, SessionId = input.SessionId };
        if (window.Router.ChannelOf(scope.DocumentId) != BridgeRouter.User ||
            !window.Router.DocumentList.Any(document => document.Scope.DocumentId == scope.DocumentId && document.Scope.SessionId == scope.SessionId))
            return Err(request, "document_mismatch", "The visible editor session is no longer open.");

        try
        {
            var image = await window.Dispatcher.InvokeAsync(() =>
            {
                if ((long)GetClipboardSequenceNumber() != input.ExpectedSequence) return (Bytes: (byte[]?)null, Width: 0, Height: 0, Changed: true);
                var bitmap = System.Windows.Clipboard.GetImage();
                if (bitmap is null) return (Bytes: (byte[]?)null, Width: 0, Height: 0, Changed: false);
                if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1 || bitmap.PixelWidth > InputLimits.RasterSide || bitmap.PixelHeight > InputLimits.RasterSide ||
                    (long)bitmap.PixelWidth * bitmap.PixelHeight > InputLimits.RasterPixels)
                    throw new ClipboardImageLimitException();
                using var stream = new ClipboardImageStream();
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                if ((long)GetClipboardSequenceNumber() != input.ExpectedSequence) return (Bytes: (byte[]?)null, Width: 0, Height: 0, Changed: true);
                return (Bytes: stream.ToArray(), Width: bitmap.PixelWidth, Height: bitmap.PixelHeight, Changed: false);
            }, System.Windows.Threading.DispatcherPriority.Background, ct);
            if (image.Changed) return Err(request, "clipboard_changed", "The clipboard changed before the picture could be read. Paste again.", retryable: true);
            if (image.Bytes is null) return Err(request, "invalid_request", "The clipboard no longer contains a supported bitmap.");
            var minted = IngressTickets.MintClipboardImage(IngressWindowId, BridgeRouter.User, "Clipboard image.png", "image/png", image.Bytes, image.Width, image.Height);
            if (!minted.IsOk) return Err(request, minted.Error!);
            var item = minted.Value;
            return Ok(request, new JsonObject
            {
                ["item"] = new JsonObject { ["ticketId"] = item.TicketId, ["displayName"] = item.DisplayName, ["kind"] = "image" },
                ["batchId"] = $"clipboard-{item.TicketId}",
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ClipboardImageLimitException) { return Err(request, "limit_exceeded", "The clipboard image exceeds the supported size limits."); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException or ArgumentException or NotSupportedException)
        { return Err(request, "clipboard_unavailable", "The clipboard picture could not be read. Try copying it again.", retryable: true); }
    }
}
