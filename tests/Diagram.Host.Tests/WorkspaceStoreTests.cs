using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class WorkspaceStoreTests
{
    static WorkspaceStore Make(TempDir dir) => new(dir.File("workspace.json"), new AtomicFileWriter());
    static readonly WorkspaceTab[] Tabs =
    [
        new("d1", "s1", null, "Untitled 1", true),
        new("d2", "s2", @"C:\Work\plan.diagram.json", "plan.diagram.json", false),
    ];

    [Fact]
    public async Task NothingRememberedYetReadsAsNull()
    {
        using var dir = new TempDir();
        Assert.Null(await Make(dir).ReadAsync());
    }

    [Fact]
    public async Task TabsComeBackInOrderWithTheActiveOneAndAreNotACleanExitWhileRunning()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        await store.WriteAsync(Tabs, "d2");
        var back = (await store.ReadAsync())!;
        Assert.False(back.CleanExit);
        Assert.Equal("d2", back.ActiveDocumentId);
        Assert.Equal(Tabs, back.Tabs);
    }

    [Fact]
    public async Task ANormalExitIsRememberedUntilTheNextWrite()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        await store.MarkCleanExitAsync(); // nothing to mark yet: no file appears
        Assert.False(File.Exists(store.File));
        await store.WriteAsync(Tabs, "d1");
        await store.MarkCleanExitAsync();
        var closed = (await store.ReadAsync())!;
        Assert.True(closed.CleanExit);
        Assert.Equal(Tabs, closed.Tabs);
        await store.WriteAsync(Tabs, "d1"); // the next run is under way
        Assert.False((await store.ReadAsync())!.CleanExit);
    }

    [Fact]
    public async Task AnUnreadableFileIsTreatedAsAbsentAndIsReplacedByTheNextWrite()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        await File.WriteAllTextAsync(store.File, "{ not json");
        Assert.Null(await store.ReadAsync());
        await File.WriteAllTextAsync(store.File, "null");
        Assert.Null(await store.ReadAsync());
        await store.WriteAsync(Tabs, null);
        Assert.Equal(2, (await store.ReadAsync())!.Tabs.Count);
    }

    [Fact]
    public async Task ConcurrentWritesNeverLeaveAHalfWrittenFile()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => store.WriteAsync(Tabs.Take(1 + i % 2).ToArray(), "d1")));
        var back = await store.ReadAsync();
        Assert.NotNull(back);
        Assert.Contains(back!.Tabs.Count, new[] { 1, 2 });
    }
}
