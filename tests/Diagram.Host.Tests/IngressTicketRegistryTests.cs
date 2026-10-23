using System.Text.Json;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public sealed class IngressTicketRegistryTests
{
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static string PathFor(string root, string name) => Path.Combine(root, name);

    [Fact]
    public void DropTicketsExposeOnlyOpaqueDisplayMetadataAndAreBoundToTheirWindowAndChannel()
    {
        using var dir = new TempDir();
        var registry = new IngressTicketRegistry();
        var secretPath = PathFor(dir.Path, "private.diagram.json");
        var batch = registry.MintDropBatch("window-1", "editor-1", [new(secretPath)]);

        var item = Assert.Single(batch.Items);
        Assert.Equal("private.diagram.json", item.DisplayName);
        Assert.Equal(IngressKind.Json, item.Kind);
        Assert.DoesNotContain(secretPath, JsonSerializer.Serialize(item));
        Assert.DoesNotContain(secretPath, JsonSerializer.Serialize(batch));
        Assert.DoesNotContain(secretPath, item.TicketId);

        Assert.Equal("invalid_request", registry.Consume("window-2", "editor-1", item.TicketId).Error?.Code);
        Assert.Equal("invalid_request", registry.Consume("window-1", "editor-2", item.TicketId).Error?.Code);
        var consumed = registry.Consume("window-1", "editor-1", item.TicketId);
        Assert.True(consumed.IsOk);
        Assert.Equal(Path.GetFullPath(secretPath), consumed.Value.FilePath);
        Assert.Equal("invalid_request", registry.Consume("window-1", "editor-1", item.TicketId).Error?.Code);
    }

    [Fact]
    public void DropBatchKeepsOrderCapsAtThirtyTwoAndRejectsDirectoriesShortcutsAndUnsupportedTypes()
    {
        using var dir = new TempDir();
        var registry = new IngressTicketRegistry();
        var inputs = Enumerable.Range(0, 40).Select(i => new IngressFile(PathFor(dir.Path, $"{i}.diagram.json"))).ToList();
        inputs[1] = new(PathFor(dir.Path, "folder.diagram.json"), IsDirectory: true);
        inputs[2] = new(PathFor(dir.Path, "linked.diagram.json"), IsShortcut: true);
        inputs[3] = new(PathFor(dir.Path, "script.html"));

        var batch = registry.MintDropBatch("w", "c", inputs);

        Assert.Equal(29, batch.Items.Count);
        Assert.Equal("0.diagram.json", batch.Items[0].DisplayName);
        Assert.Equal("4.diagram.json", batch.Items[1].DisplayName);
        Assert.Equal(11, batch.Rejections.Count);
        Assert.All(batch.Rejections, message => Assert.DoesNotContain(dir.Path, message));
        Assert.Equal(29, registry.DescribePending("w", "c").Count);
        Assert.Empty(registry.DescribePending("w", "other-channel"));
    }

    [Fact]
    public void UnsupportedAndUnsafePathsDoNotMintTickets()
    {
        var registry = new IngressTicketRegistry();
        var batch = registry.MintDropBatch("w", "c", [
            new("\\\\server\\share\\drawing.diagram.json"),
            new("C:\\safe\\drawing.diagram.json:secret"),
            new("https://example.test/drawing.diagram.json"),
            new("relative.diagram.json"),
            new("C:\\safe\\shortcut.lnk", IsShortcut: true),
        ]);

        Assert.Empty(batch.Items);
        Assert.Equal(5, batch.Rejections.Count);
    }

    [Fact]
    public void ExpiredTicketsCannotBeConsumedAndAreRemovedFromPendingDescriptions()
    {
        using var dir = new TempDir();
        var clock = new Clock(DateTimeOffset.Parse("2026-10-20T10:00:00Z"));
        var registry = new IngressTicketRegistry(clock);
        var item = Assert.Single(registry.MintDropBatch("w", "c", [new(PathFor(dir.Path, "a.vsdx"))]).Items);

        clock.Now = clock.Now.AddMinutes(5);

        Assert.Equal("invalid_request", registry.Consume("w", "c", item.TicketId).Error?.Code);
        Assert.Empty(registry.DescribePending("w", "c"));
    }

    [Fact]
    public void DismissalIsIdempotentAndCannotReleaseAnotherChannelsTicket()
    {
        using var dir = new TempDir();
        var registry = new IngressTicketRegistry();
        var item = Assert.Single(registry.MintDropBatch("w", "c", [new(PathFor(dir.Path, "a.vsdx"))]).Items);

        Assert.Equal(0, registry.Dismiss("w", "other", [item.TicketId]));
        Assert.Single(registry.DescribePending("w", "c"));
        Assert.Equal(1, registry.Dismiss("w", "c", [item.TicketId, item.TicketId]));
        Assert.Equal(0, registry.Dismiss("w", "c", [item.TicketId]));
        Assert.Empty(registry.DescribePending("w", "c"));
    }

    [Fact]
    public void ChannelResetReleasesOnlyTicketsOwnedByThatRenderer()
    {
        using var dir = new TempDir();
        var registry = new IngressTicketRegistry();
        registry.MintDropBatch("w", "editor-1", [new(PathFor(dir.Path, "a.vsdx"))]);
        registry.MintDropBatch("w", "editor-2", [new(PathFor(dir.Path, "b.vsdx"))]);

        registry.ClearChannel("editor-1");

        Assert.Empty(registry.DescribePending("w", "editor-1"));
        Assert.Single(registry.DescribePending("w", "editor-2"));
    }

    [Fact]
    public void ClipboardImagesAreValidatedClonedAndBoundedByTheRasterInputBudget()
    {
        var registry = new IngressTicketRegistry();
        var image = Bytes.PngHeader(4, 4);
        var minted = registry.MintClipboardImage("w", "c", "clipboard.png", "image/png", image, 4, 4);
        Assert.True(minted.IsOk);
        image[0] = 0;
        var ticket = registry.Consume("w", "c", minted.Value.TicketId);
        Assert.True(ticket.IsOk);
        Assert.Equal((byte)0x89, ticket.Value.ImageBytes![0]);

        var atLimit = new byte[checked((int)InputLimits.RasterBytes)];
        Bytes.PngHeader(1, 1).CopyTo(atLimit, 0);
        var fullBudget = registry.MintClipboardImage("w", "c", "large.png", "image/png", atLimit, 1, 1);
        Assert.True(fullBudget.IsOk);
        var overBudget = registry.MintClipboardImage("w", "c", "small.png", "image/png", Bytes.PngHeader(1, 1), 1, 1);
        Assert.Equal("limit_exceeded", overBudget.Error?.Code);

        Assert.Equal("invalid_request", registry.MintClipboardImage("w", "c", "bad.jpg", "image/jpeg", Bytes.PngHeader(1, 1), 1, 1).Error?.Code);
        Assert.Equal("limit_exceeded", registry.MintClipboardImage("w", "c", "bad.png", "image/png", Bytes.PngHeader(1, 1), 16_385, 1).Error?.Code);
        Assert.Equal("limit_exceeded", registry.MintClipboardImage("w", "c", "bad.png", "image/png", Bytes.PngHeader(1, 1), 8193, 8193).Error?.Code);
    }

    [Fact]
    public void PendingTicketsAreBoundedAndShutdownClearsTheirRetainedResources()
    {
        using var dir = new TempDir();
        var registry = new IngressTicketRegistry();
        var batches = Enumerable.Range(0, IngressTicketRegistry.MaxPendingTicketsPerWindow / IngressTicketRegistry.MaxFilesPerGesture)
            .Select(batch => registry.MintDropBatch("w", "c", Enumerable.Range(0, IngressTicketRegistry.MaxFilesPerGesture)
                .Select(i => new IngressFile(PathFor(dir.Path, $"{batch}-{i}.vsdx"))).ToArray())).ToArray();
        var overflow = registry.MintDropBatch("w", "c", [new(PathFor(dir.Path, "overflow.vsdx"))]);

        Assert.All(batches, batch => Assert.Equal(IngressTicketRegistry.MaxFilesPerGesture, batch.Items.Count));
        Assert.Empty(overflow.Items);
        Assert.Single(overflow.Rejections);
        registry.ClearOnShutdown();
        registry.ClearOnShutdown();
        Assert.Empty(registry.DescribePending("w", "c"));
    }
}
