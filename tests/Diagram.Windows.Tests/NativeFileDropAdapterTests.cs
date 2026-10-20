using System.IO;
using System.Windows;
using Diagram.App;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

public sealed class NativeFileDropAdapterTests
{
    [Fact]
    public async Task NativeFileGestureMintsOneBoundedBatchAndNotifiesOnlySafeMetadata()
    {
        await OnSta(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".diagram.json");
            var registry = new IngressTicketRegistry();
            IngressDropBatch? sent = null;
            var adapter = new NativeFileDropAdapter(registry, "window", () => "user", async (batch, _, _) => { sent = batch; await Task.CompletedTask; return true; });
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });

            var result = await adapter.HandleDropAsync(data, new Point(40, 80));

            Assert.True(result.Accepted);
            Assert.NotNull(sent);
            var item = Assert.Single(sent!.Items);
            Assert.EndsWith(".diagram.json", item.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(path, System.Text.Json.JsonSerializer.Serialize(sent));
            Assert.Single(registry.DescribePending("window", "user"));
        });
    }

    [Fact]
    public async Task NonFileNativeDataIsNotAcceptedOrMinted()
    {
        await OnSta(async () =>
        {
            var registry = new IngressTicketRegistry();
            var called = false;
            var adapter = new NativeFileDropAdapter(registry, "window", () => "user", (_, _, _) => { called = true; return Task.FromResult(true); });
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "https://example.test/file.diagram.json");

            var result = await adapter.HandleDropAsync(data, new Point(0, 0));

            Assert.False(result.Accepted);
            Assert.False(called);
            Assert.Empty(registry.DescribePending("window", "user"));
        });
    }

    [Fact]
    public async Task FileGestureRejectsDirectoriesShortcutsAndUnsupportedNames()
    {
        await OnSta(async () =>
        {
            var folder = Path.GetTempPath();
            var shortcut = Path.Combine(Path.GetTempPath(), "drawing.lnk");
            var url = "https://example.test/drawing.diagram.json";
            var registry = new IngressTicketRegistry();
            IngressDropBatch? sent = null;
            var adapter = new NativeFileDropAdapter(registry, "window", () => "user", (batch, _, _) => { sent = batch; return Task.FromResult(true); });
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { folder, shortcut, url, "bad\0name.diagram.json" });

            var result = await adapter.HandleDropAsync(data, new Point(0, 0));

            Assert.True(result.Accepted);
            Assert.Empty(sent!.Items);
            Assert.Equal(4, sent.Rejections.Count);
            Assert.Empty(registry.DescribePending("window", "user"));
        });
    }

    [Fact]
    public async Task CancelledOrReplacedChannelGestureCannotMintTickets()
    {
        await OnSta(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".diagram.json");
            var registry = new IngressTicketRegistry();
            var channel = "user";
            var adapter = new NativeFileDropAdapter(registry, "window", () => channel, (_, _, _) => Task.FromResult(true));
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var cancelled = await adapter.HandleDropAsync(data, new Point(0, 0), cts.Token);
            channel = "agent";
            var replaced = await adapter.HandleDropAsync(data, new Point(0, 0));

            Assert.False(cancelled.Accepted);
            Assert.False(replaced.Accepted);
            Assert.Empty(registry.DescribePending("window", "user"));
            Assert.Empty(registry.DescribePending("window", "agent"));
        });
    }

    [Fact]
    public async Task NativeGestureUsesRegistryCountAndMalformedPathLimits()
    {
        await OnSta(async () =>
        {
            var registry = new IngressTicketRegistry();
            IngressDropBatch? sent = null;
            var adapter = new NativeFileDropAdapter(registry, "window", () => "user", (batch, _, _) => { sent = batch; return Task.FromResult(true); });
            var paths = Enumerable.Range(0, 40).Select(i => Path.Combine(Path.GetTempPath(), $"{i}.diagram.json")).ToList();
            paths[0] = "bad\0name.diagram.json";
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.ToArray());

            var result = await adapter.HandleDropAsync(data, new Point(2, 3));

            Assert.True(result.Accepted);
            Assert.Equal(31, sent!.Items.Count);
            Assert.Equal(9, sent.Rejections.Count);
            Assert.Equal(31, registry.DescribePending("window", "user").Count);
        });
    }

    [Fact]
    public async Task FailedDeliveryReleasesEveryNewTicket()
    {
        await OnSta(async () =>
        {
            var registry = new IngressTicketRegistry();
            var adapter = new NativeFileDropAdapter(registry, "window", () => "user", (_, _, _) => Task.FromResult(false));
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { Path.Combine(Path.GetTempPath(), "lost.vsdx") });

            var result = await adapter.HandleDropAsync(data, new Point(0, 0));

            Assert.False(result.Accepted);
            Assert.Empty(registry.DescribePending("window", "user"));
        });
    }

    [Fact]
    public async Task EditorChannelReplacementDuringDeliveryReleasesTheBatch()
    {
        await OnSta(async () =>
        {
            var channel = "user";
            var registry = new IngressTicketRegistry();
            var adapter = new NativeFileDropAdapter(registry, "window", () => channel, (batch, _, _) =>
            {
                channel = "agent";
                registry.ClearChannel("user");
                return Task.FromResult(true);
            });
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { Path.Combine(Path.GetTempPath(), "changed.diagram.json") });

            var result = await adapter.HandleDropAsync(data, new Point(0, 0));

            Assert.False(result.Accepted);
            Assert.Empty(registry.DescribePending("window", "user"));
        });
    }

    static Task OnSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try { await action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
