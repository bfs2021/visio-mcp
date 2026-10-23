using System.Security.Cryptography;
using System.Text;
using Diagram.Host.Core;

namespace Diagram.App;

public sealed record PrintMedia(string Id, string Label, double WidthPt, double HeightPt, PrintMargins MinimumMargins);
public sealed record PrintDevice(string Id, string Label, IReadOnlyList<PrintMedia> Media);
public sealed record PrintDeviceSettings(string PrinterName, string MediaId, double WidthPt, double HeightPt, string Orientation, PrintMargins EffectiveMargins);
public sealed record PreparedPrintJob(string Id, ExpectedState Expected, IReadOnlyList<PrintSheet> Sheets, PrintLayoutOptions Layout, PrintDeviceSettings Device, DateTimeOffset ExpiresAt);

public sealed class PrintJobStore(TimeProvider? timeProvider = null)
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    readonly object gate = new();
    PreparedPrintJob? current;
    public bool HasPendingJob { get { lock (gate) return current is not null && clock.GetUtcNow() < current.ExpiresAt; } }

    public PreparedPrintJob Prepare(ExpectedState expected, IReadOnlyList<PrintSheet> sheets, PrintLayoutOptions? layout = null, PrintDeviceSettings? device = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(sheets);
        if (string.IsNullOrWhiteSpace(expected.DocumentId) || string.IsNullOrWhiteSpace(expected.SessionId) || expected.BaseRevision < 0 || sheets.Count is < 1 or > 32)
            throw new PrintLayoutException("invalid_request", "The prepared print job has invalid drawing identity or sheet count.");
        long total = 0;
        foreach (var sheet in sheets)
        {
            if (sheet is null || string.IsNullOrWhiteSpace(sheet.Svg)) throw new PrintLayoutException("invalid_geometry", "A print sheet is empty.");
            total += Encoding.UTF8.GetByteCount(sheet.Svg);
            if (total > MaximumBytes) throw new PrintLayoutException("limit_exceeded", "Prepared print sheets exceed 64 MiB.");
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var job = new PreparedPrintJob(id, expected with { }, Array.AsReadOnly(sheets.ToArray()), layout!, device!, clock.GetUtcNow() + Lifetime);
        lock (gate) current = job;
        return job;
    }

    public bool TryTake(string id, ExpectedState expected, out PreparedPrintJob? job) => TryTake(id, expected, out job, out _);

    public bool TryTake(string id, ExpectedState expected, out PreparedPrintJob? job, out string? error)
    {
        lock (gate)
        {
            job = null;
            error = null;
            if (current is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(current.Id), Encoding.ASCII.GetBytes(id ?? string.Empty)))
            {
                error = "job_not_found";
                return false;
            }
            var candidate = current;
            current = null;
            if (clock.GetUtcNow() >= candidate.ExpiresAt) { error = "job_expired"; return false; }
            if (candidate.Expected.DocumentId != expected.DocumentId || candidate.Expected.SessionId != expected.SessionId)
            { error = "session_mismatch"; return false; }
            if (candidate.Expected.BaseRevision != expected.BaseRevision) { error = "revision_conflict"; return false; }
            job = candidate;
            return true;
        }
    }

    public void Clear(string documentId)
    {
        lock (gate) if (current?.Expected.DocumentId == documentId) current = null;
    }
}

public interface IPrinterCapabilities
{
    IReadOnlyList<PrintDevice> GetDevices();
}

/// <summary>Reads the Windows spooler's installed printers and paper capabilities. This never submits a print job.</summary>
public sealed class WindowsPrinterCapabilities : IPrinterCapabilities
{
    public IReadOnlyList<PrintDevice> GetDevices()
    {
        var devices = new List<PrintDevice>();
        try
        {
            using var server = new System.Printing.LocalPrintServer();
            foreach (var queue in server.GetPrintQueues())
            {
                try
                {
                    var capabilities = queue.GetPrintCapabilities();
                    var media = (capabilities.PageMediaSizeCapability ?? []).Where(x => x.Width is > 0 && x.Height is > 0)
                        .Select(x =>
                        {
                            var ticket = queue.DefaultPrintTicket ?? new System.Printing.PrintTicket();
                            ticket.PageMediaSize = x;
                            var paperCapabilities = queue.GetPrintCapabilities(ticket);
                            return new PrintMedia(
                                x.PageMediaSizeName?.ToString() ?? $"{x.Width:0}x{x.Height:0}",
                                x.PageMediaSizeName?.ToString() ?? $"{x.Width:0} × {x.Height:0} pt",
                                x.Width!.Value * .75, x.Height!.Value * .75,
                                Margins(paperCapabilities.PageImageableArea, x.Width.Value, x.Height.Value));
                        })
                        .GroupBy(x => x.Id, StringComparer.Ordinal).Select(g => g.First()).ToArray();
                    if (media.Length > 0) devices.Add(new PrintDevice(queue.Name, queue.Name, Array.AsReadOnly(media)));
                }
                catch (Exception e) when (e is System.Printing.PrintSystemException or InvalidOperationException) { }
                finally { queue.Dispose(); }
            }
        }
        catch (System.Printing.PrintSystemException) { }
        return devices.AsReadOnly();
    }

    static PrintMargins Margins(System.Printing.PageImageableArea? area, double paperWidthDip, double paperHeightDip)
    {
        if (area is null) return new PrintMargins(0, 0, 0, 0);
        // The capabilities API reports imageable origin and extent in 1/96-inch units.
        return new PrintMargins(
            Math.Max(0, area.OriginWidth * .75), Math.Max(0, area.OriginHeight * .75),
            Math.Max(0, (paperWidthDip - area.OriginWidth - area.ExtentWidth) * .75),
            Math.Max(0, (paperHeightDip - area.OriginHeight - area.ExtentHeight) * .75));
    }
}
