using Diagram.Ipc;
using Xunit;

public class InstanceGuardTests
{
    static string NewName() => $"guard-test-{Guid.NewGuid():N}";
    static async Task<T> Within<T>(Task<T> t, int seconds = 10) => await t.WaitAsync(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task OnlyTheFirstLaunchOwnsTheInstanceAndTheNameIsFreeAgainAfterItExits()
    {
        var name = NewName();
        var first = InstanceGuard.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(InstanceGuard.TryAcquire(name));
        await first!.DisposeAsync();
        await using var again = InstanceGuard.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task ASecondLaunchHandsItsFilesToTheRunningWindow()
    {
        var name = NewName();
        await using var owner = InstanceGuard.TryAcquire(name)!;
        var got = new TaskCompletionSource<IReadOnlyList<string>>();
        owner.Attach(p => { got.TrySetResult(p); return Task.CompletedTask; });
        Assert.True(await InstanceGuard.ForwardAsync(name, [@"C:\a\one.diagram.json", @"C:\a\two.vsdx"], TimeSpan.FromSeconds(5)));
        Assert.Equal([@"C:\a\one.diagram.json", @"C:\a\two.vsdx"], await Within(got.Task));
    }

    [Fact]
    public async Task FilesSentWhileTheWindowIsStillStartingAreDeliveredWhenItAttaches()
    {
        var name = NewName();
        await using var owner = InstanceGuard.TryAcquire(name)!;
        Assert.True(await InstanceGuard.ForwardAsync(name, [@"C:\early.vsdx"], TimeSpan.FromSeconds(5)));
        var got = new TaskCompletionSource<IReadOnlyList<string>>();
        owner.Attach(p => { got.TrySetResult(p); return Task.CompletedTask; });
        Assert.Equal([@"C:\early.vsdx"], await Within(got.Task));
    }

    [Fact]
    public async Task SeveralLaunchesInARowAllArriveAndTheNameStaysTaken()
    {
        var name = NewName();
        await using var owner = InstanceGuard.TryAcquire(name)!;
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        owner.Attach(p => { foreach (var x in p) seen.Add(x); return Task.CompletedTask; });
        for (var i = 0; i < 5; i++)
        {
            Assert.True(await InstanceGuard.ForwardAsync(name, [$@"C:\f{i}.vsdx"], TimeSpan.FromSeconds(5)));
            Assert.Null(InstanceGuard.TryAcquire(name)); // never a moment when a new launch could think it is first
        }
        for (var i = 0; i < 100 && seen.Count < 5; i++) await Task.Delay(50);
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task ALaunchWithNoFilesStillReachesTheWindowSoItCanComeForward()
    {
        var name = NewName();
        await using var owner = InstanceGuard.TryAcquire(name)!;
        var got = new TaskCompletionSource<int>();
        owner.Attach(p => { got.TrySetResult(p.Count); return Task.CompletedTask; });
        Assert.True(await InstanceGuard.ForwardAsync(name, [], TimeSpan.FromSeconds(5)));
        Assert.Equal(0, await Within(got.Task));
    }

    [Fact]
    public async Task NothingListeningMeansTheForwardFailsRatherThanStartingAnotherWindow()
    {
        Assert.False(await InstanceGuard.ForwardAsync(NewName(), [@"C:\x.vsdx"], TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task AMalformedMessageIsRefusedAndNeverReachesTheWindow()
    {
        var name = NewName();
        await using var owner = InstanceGuard.TryAcquire(name)!;
        var delivered = 0;
        owner.Attach(_ => { Interlocked.Increment(ref delivered); return Task.CompletedTask; });
        await using var c = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await c.ConnectAsync(5000);
        using var bad = System.Text.Json.JsonSerializer.SerializeToDocument(new { kind = "open", paths = new object[] { 5 } });
        await FrameCodec.WriteAsync(c, bad, new CancellationTokenSource(5000).Token);
        using var reply = await FrameCodec.ReadAsync(c, new CancellationTokenSource(5000).Token);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, delivered);

        var tooMany = Enumerable.Range(0, InstanceGuard.MaxPaths + 1).Select(i => $@"C:\{i}.vsdx").ToList();
        Assert.False(await InstanceGuard.ForwardAsync(name, tooMany, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, delivered);
    }

    [Fact]
    public void EachUserAndLogonSessionHasItsOwnLaunchName()
    {
        var a = PipeNames.ForLaunch("S-1-5-21-1", 0x3e7);
        Assert.NotEqual(a, PipeNames.ForLaunch("S-1-5-21-2", 0x3e7)); // another Windows user: a window of their own
        Assert.NotEqual(a, PipeNames.ForLaunch("S-1-5-21-1", 0x1a2b3)); // the same user in another logon session
        Assert.NotEqual(PipeNames.ForLaunch(), PipeNames.For()); // not the MCP pipe
        Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, PipeNames.ForLaunch());
    }
}
