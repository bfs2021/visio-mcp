using System.Security.Cryptography;

namespace Diagram.Host.Core;

public enum IngressKind { Json, Vsdx, Image }

/// <summary>A path item observed by the native shell during one user initiated drop.</summary>
public sealed record IngressFile(string Path, bool IsDirectory = false, bool IsShortcut = false);

/// <summary>Safe renderer-facing metadata. File paths and captured bytes never leave the host.</summary>
public sealed record IngressItemDescriptor(string TicketId, string DisplayName, IngressKind Kind);

public sealed record IngressDropBatch(string BatchId, IReadOnlyList<IngressItemDescriptor> Items, IReadOnlyList<string> Rejections);

/// <summary>Host-only ticket contents returned once to a matching visible window/channel.</summary>
public sealed record IngressTicket(
    string TicketId,
    string BatchId,
    string WindowId,
    string ChannelId,
    string DisplayName,
    IngressKind Kind,
    string? FilePath,
    byte[]? ImageBytes,
    string? MimeType,
    int? Width,
    int? Height,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Holds bounded, short-lived capabilities minted by the native user-gesture adapters. This type is not an IPC or MCP endpoint.
/// </summary>
public sealed class IngressTicketRegistry
{
    public const int MaxFilesPerGesture = 32;
    public const int MaxPendingTicketsPerWindow = 256;
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

    static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml",
    };

    sealed record Entry(IngressTicket Ticket, long RetainedBytes);

    readonly object gate = new();
    readonly Dictionary<string, Entry> tickets = new(StringComparer.Ordinal);
    readonly TimeProvider clock;
    long retainedImageBytes;
    bool shutdown;

    public IngressTicketRegistry(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    public IngressDropBatch MintDropBatch(string windowId, string channelId, IReadOnlyList<IngressFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var rejected = new List<string>();
        var items = new List<IngressItemDescriptor>(Math.Min(files.Count, MaxFilesPerGesture));
        var batchId = NewId("batch-");
        lock (gate)
        {
            PruneExpired();
            if (shutdown || !ValidOwner(windowId, channelId))
                return new IngressDropBatch(batchId, items, ["The window is no longer available."]);

            for (var i = 0; i < files.Count; i++)
            {
                if (i >= MaxFilesPerGesture)
                {
                    rejected.Add("A gesture can contain at most 32 files.");
                    continue;
                }

                var candidate = files[i];
                if (candidate.IsDirectory)
                {
                    rejected.Add("Folders cannot be opened from a drop.");
                    continue;
                }
                if (candidate.IsShortcut || string.Equals(Path.GetExtension(candidate.Path), ".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    rejected.Add("Shortcuts cannot be opened from a drop.");
                    continue;
                }

                var validatedPath = PathPolicy.Validate(candidate.Path);
                if (!validatedPath.IsOk || !TryKind(validatedPath.IsOk ? validatedPath.Value : candidate.Path, out var kind))
                {
                    rejected.Add("This file type or path is not supported.");
                    continue;
                }
                if (CountForWindow(windowId) >= MaxPendingTicketsPerWindow)
                {
                    rejected.Add("Too many items are already waiting. Dismiss them or wait for them to expire.");
                    continue;
                }

                var fullPath = validatedPath.Value;
                var ticketId = NewId("ingress-");
                var displayName = SafeDisplayName(Path.GetFileName(fullPath));
                var ticket = new IngressTicket(ticketId, batchId, windowId, channelId, displayName, kind, fullPath, null, null, null, null, clock.GetUtcNow() + TicketLifetime);
                tickets.Add(ticketId, new Entry(ticket, 0));
                items.Add(new IngressItemDescriptor(ticketId, displayName, kind));
            }
        }
        return new IngressDropBatch(batchId, items, rejected);
    }

    public Result<IngressItemDescriptor> MintClipboardImage(string windowId, string channelId, string displayName, string mimeType, byte[] imageBytes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        lock (gate)
        {
            PruneExpired();
            if (shutdown || !ValidOwner(windowId, channelId)) return Result<IngressItemDescriptor>.Fail("not_ready", "the window is no longer available");
            if (!ImageTypes.TryGetValue(MimeExtension(mimeType), out var expectedMime) || expectedMime != mimeType || mimeType == "image/svg+xml")
                return Result<IngressItemDescriptor>.Fail("invalid_request", "the clipboard format is not a supported raster image");
            if (imageBytes.Length == 0 || imageBytes.LongLength > InputLimits.RasterBytes)
                return Result<IngressItemDescriptor>.Fail("limit_exceeded", $"clipboard image bytes exceed the {InputLimits.RasterBytes}-byte limit");
            if (width < 1 || height < 1 || width > InputLimits.RasterSide || height > InputLimits.RasterSide || (long)width * height > InputLimits.RasterPixels)
                return Result<IngressItemDescriptor>.Fail("limit_exceeded", "clipboard image dimensions exceed supported limits");
            if (!string.Equals(ImageMime.Sniff(imageBytes), mimeType, StringComparison.Ordinal))
                return Result<IngressItemDescriptor>.Fail("invalid_request", "clipboard image bytes do not match their declared format");
            var header = RasterHeader.Read(mimeType, imageBytes);
            if (header is null || header.Value.Width != width || header.Value.Height != height)
                return Result<IngressItemDescriptor>.Fail("invalid_request", "clipboard image dimensions do not match its encoded image");
            if (CountForWindow(windowId) >= MaxPendingTicketsPerWindow)
                return Result<IngressItemDescriptor>.Fail("limit_exceeded", "too many items are already waiting");
            if (retainedImageBytes + imageBytes.LongLength > InputLimits.RasterBytes)
                return Result<IngressItemDescriptor>.Fail("limit_exceeded", "pending clipboard images exceed the memory limit");

            var ticketId = NewId("ingress-");
            var batchId = NewId("batch-");
            var safeName = SafeDisplayName(displayName);
            var ticket = new IngressTicket(ticketId, batchId, windowId, channelId, safeName, IngressKind.Image, null, imageBytes.ToArray(), mimeType, width, height, clock.GetUtcNow() + TicketLifetime);
            tickets.Add(ticketId, new Entry(ticket, imageBytes.LongLength));
            retainedImageBytes += imageBytes.LongLength;
            return Result<IngressItemDescriptor>.Ok(new IngressItemDescriptor(ticketId, safeName, IngressKind.Image));
        }
    }

    public IReadOnlyList<IngressItemDescriptor> DescribePending(string windowId, string channelId)
    {
        lock (gate)
        {
            PruneExpired();
            if (shutdown || !ValidOwner(windowId, channelId)) return [];
            return tickets.Values
                .Where(x => x.Ticket.WindowId == windowId && x.Ticket.ChannelId == channelId)
                .Select(x => new IngressItemDescriptor(x.Ticket.TicketId, x.Ticket.DisplayName, x.Ticket.Kind))
                .ToArray();
        }
    }

    public Result<IngressTicket> Consume(string windowId, string channelId, string ticketId)
    {
        lock (gate)
        {
            PruneExpired();
            if (shutdown || !ValidOwner(windowId, channelId) || !tickets.TryGetValue(ticketId, out var entry) ||
                entry.Ticket.WindowId != windowId || entry.Ticket.ChannelId != channelId)
                return Result<IngressTicket>.Fail("invalid_request", "the ingress ticket is unavailable");

            tickets.Remove(ticketId);
            retainedImageBytes -= entry.RetainedBytes;
            return Result<IngressTicket>.Ok(entry.Ticket with { ImageBytes = entry.Ticket.ImageBytes?.ToArray() });
        }
    }

    public int Dismiss(string windowId, string channelId, IReadOnlyList<string> ticketIds)
    {
        ArgumentNullException.ThrowIfNull(ticketIds);
        lock (gate)
        {
            PruneExpired();
            if (shutdown || !ValidOwner(windowId, channelId)) return 0;
            var removed = 0;
            foreach (var id in ticketIds.Distinct(StringComparer.Ordinal))
            {
                if (!tickets.TryGetValue(id, out var entry) || entry.Ticket.WindowId != windowId || entry.Ticket.ChannelId != channelId) continue;
                tickets.Remove(id);
                retainedImageBytes -= entry.RetainedBytes;
                removed++;
            }
            return removed;
        }
    }

    public void ClearOnShutdown()
    {
        lock (gate)
        {
            tickets.Clear();
            retainedImageBytes = 0;
            shutdown = true;
        }
    }

    public void ClearChannel(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return;
        lock (gate)
        {
            foreach (var (id, entry) in tickets.Where(x => x.Value.Ticket.ChannelId == channelId).ToArray())
            {
                tickets.Remove(id);
                retainedImageBytes -= entry.RetainedBytes;
            }
        }
    }

    static bool ValidOwner(string windowId, string channelId) =>
        !string.IsNullOrWhiteSpace(windowId) && windowId.Length <= 128 && !string.IsNullOrWhiteSpace(channelId) && channelId.Length <= 128;

    static bool TryKind(string path, out IngressKind kind)
    {
        if (path.EndsWith(".diagram.json", StringComparison.OrdinalIgnoreCase)) { kind = IngressKind.Json; return true; }
        if (string.Equals(Path.GetExtension(path), ".vsdx", StringComparison.OrdinalIgnoreCase)) { kind = IngressKind.Vsdx; return true; }
        if (ImageTypes.ContainsKey(Path.GetExtension(path))) { kind = IngressKind.Image; return true; }
        kind = default;
        return false;
    }

    static string MimeExtension(string mimeType) => mimeType switch
    {
        "image/png" => ".png", "image/jpeg" => ".jpg", "image/bmp" => ".bmp", "image/svg+xml" => ".svg", _ => string.Empty,
    };

    static string SafeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Clipboard image";
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
        if (cleaned.Length == 0) return "Clipboard image";
        return cleaned.Length <= 128 ? cleaned : cleaned[..128];
    }

    int CountForWindow(string windowId) => tickets.Values.Count(x => x.Ticket.WindowId == windowId);

    void PruneExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var (id, entry) in tickets.Where(x => x.Value.Ticket.ExpiresAt <= now).ToArray())
        {
            tickets.Remove(id);
            retainedImageBytes -= entry.RetainedBytes;
        }
    }

    static string NewId(string prefix) => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
