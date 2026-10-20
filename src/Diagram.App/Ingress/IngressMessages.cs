using Diagram.Host.Core;

namespace Diagram.App;

/// <summary>Internal renderer messages for native ingress. These messages are not MCP/live-control operations.</summary>
internal static class IngressHostMethods
{
    public const string Available = "host.ingress.available";
    public const string Open = "host.ingress.open";
    public const string ImportImage = "host.ingress.importImage";
    public const string Dismiss = "host.ingress.dismiss";
    public const string ClipboardDescribe = "host.clipboard.describe";
    public const string ClipboardMarkInternalCopy = "host.clipboard.markInternalCopy";
    public const string ClipboardImportImage = "host.clipboard.importImage";
}

internal sealed record IngressDropTarget(string DocumentId, string SessionId, int BaseRevision, string PageId, double X, double Y);
internal sealed record IngressAvailableEvent(string BatchId, IReadOnlyList<IngressItemDescriptor> Items, IReadOnlyList<string> Rejections, IngressDropTarget? Target = null);
internal sealed record IngressOpenRequest(string TicketId, string DocumentId, string SessionId, int BaseRevision);
internal sealed record IngressImageImportRequest(string TicketId, string DocumentId, string SessionId);
internal sealed record IngressDismissRequest(IReadOnlyList<string> TicketIds);
internal sealed record ClipboardDescriptor(long Sequence, string? InternalCopyId, bool HasBitmap, bool HasText);
internal sealed record ClipboardMarkInternalCopyRequest(string CopyId);
internal sealed record ClipboardImportImageRequest(long ExpectedSequence);
