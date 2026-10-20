using System.IO;
using System.Windows;
using System.Windows.Controls;
using Diagram.Host.Core;

namespace Diagram.App;

internal sealed record NativeFileDropResult(bool Accepted, string? Error = null);

/// <summary>Turns only a native WPF FileDrop gesture into short-lived host tickets.</summary>
internal sealed class NativeFileDropAdapter(
    IngressTicketRegistry tickets,
    string windowId,
    Func<string?> visibleChannel,
    Func<IngressDropBatch, Point, CancellationToken, Task<bool>> notify)
{
    public async Task<NativeFileDropResult> HandleDropAsync(IDataObject data, Point point, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new(false, "The drop was cancelled.");
        string[] paths;
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return new(false);
            if (data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] nativePaths || nativePaths.Length == 0)
                return new(false, "The drop did not contain a valid file list.");
            paths = nativePaths;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return new(false, "The native file list could not be read.");
        }
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0)
            return new(false, "The drop position is invalid.");

        var channel = visibleChannel();
        if (channel != BridgeRouter.User) return new(false, "There is no visible editor to receive this drop.");
        var files = paths.Select(path => new IngressFile(path, Directory.Exists(path),
            string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))).ToArray();
        var batch = tickets.MintDropBatch(windowId, channel, files);
        if (visibleChannel() != channel)
        {
            tickets.Dismiss(windowId, channel, batch.Items.Select(x => x.TicketId).ToArray());
            return new(false, "The visible editor changed during the drop.");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            tickets.Dismiss(windowId, channel, batch.Items.Select(x => x.TicketId).ToArray());
            return new(false, "The drop was cancelled.");
        }

        try
        {
            if (await notify(batch, point, cancellationToken) && visibleChannel() == channel) return new(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TaskCanceledException or System.Runtime.InteropServices.COMException) { }
        tickets.Dismiss(windowId, channel, batch.Items.Select(x => x.TicketId).ToArray());
        return new(false, "The drop could not be delivered to the editor.");
    }
}
