using System.Diagnostics;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class ExportPathTests
{
    sealed class Consent : IExportConsent
    {
        public bool Answer;
        public Func<Task<bool>>? Custom;
        public readonly List<ConsentRequest> Asked = [];
        public async Task<bool> AskAsync(ConsentRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            if (Custom is not null) { var t = Custom(); await Task.WhenAny(t, Task.Delay(Timeout.Infinite, ct)); ct.ThrowIfCancellationRequested(); return await t; }
            return Answer;
        }
    }

    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly string Exports, Saved, Elsewhere;
        public readonly Consent Consent = new();
        public Scope Scope = new() { DocumentId = "d1", SessionId = "s1" };
        public ExportPathPolicy Policy;
        public Env(TimeSpan? timeout = null)
        {
            Exports = Dir.File("exports"); Saved = Dir.File("saved"); Elsewhere = Dir.File("elsewhere");
            foreach (var d in new[] { Exports, Saved, Elsewhere }) Directory.CreateDirectory(d);
            Policy = new ExportPathPolicy(Exports, () => [Saved], () => Scope, Consent, timeout);
        }
        public Task<Result<ApprovedDestination>> Resolve(string? path, string format = "vsdx", string title = "My diagram", int revision = 5) =>
            Policy.ResolveAsync(path, format, title, Scope, revision, "MCP client", default);
        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public async Task OmittedPathGetsASafeUniqueName()
    {
        using var env = new Env();
        var first = (await env.Resolve(null)).Value;
        Assert.Equal(Path.Combine(Path.GetFullPath(env.Exports), "my-diagram-r5.vsdx"), first.Path);
        Assert.False(first.Consented);
        File.WriteAllText(first.Path, "x");
        var second = (await env.Resolve(null)).Value;
        Assert.Equal(Path.Combine(Path.GetFullPath(env.Exports), "my-diagram-r5-2.vsdx"), second.Path); // never replaces what is there
        Assert.Empty(env.Consent.Asked);
        // hostile characters in a title cannot leave the folder or make a reserved name
        var odd = (await env.Resolve(null, "png", @"..\..\CON:evil*")).Value;
        Assert.StartsWith(Path.GetFullPath(env.Exports), odd.Path);
        Assert.EndsWith(".png", odd.Path);
    }

    [Fact]
    public async Task ExportToTheSavedDocumentsFolderNeedsNoQuestion()
    {
        using var env = new Env();
        var r = await env.Resolve(Path.Combine(env.Saved, "out.png"), "png");
        Assert.True(r.IsOk, r.Error?.Message);
        Assert.Empty(env.Consent.Asked);
        Assert.Equal("my-diagram-r5.vsdx", Path.GetFileName((await env.Resolve("my-diagram-r5.vsdx")).Value.Path)); // a relative name means the export folder
    }

    [Fact]
    public async Task OutsideTheApprovedFoldersAsksAndDenialOrNoAnswerWritesNothing()
    {
        using var env = new Env();
        var target = Path.Combine(env.Elsewhere, "out.vsdx");
        var denied = await env.Resolve(target);
        Assert.Equal("path_not_permitted", denied.Error!.Code);
        var asked = Assert.Single(env.Consent.Asked);
        Assert.Equal("MCP client", asked.Client);
        Assert.Equal(Path.GetFullPath(target), asked.Path);
        Assert.Contains("outside", asked.Action);

        env.Consent.Answer = true;
        var yes = (await env.Resolve(target)).Value;
        Assert.True(yes.Consented);

        // traversal out of the export folder is the same question, not a silent pass
        env.Consent.Answer = false;
        var up = await env.Resolve(@"..\elsewhere\sneaky.vsdx");
        Assert.Equal("path_not_permitted", up.Error!.Code);
        Assert.False(File.Exists(Path.Combine(env.Elsewhere, "sneaky.vsdx")));
    }

    [Fact]
    public async Task ConsentThatNeverComesIsADenialAfterTheTimeout()
    {
        using var env = new Env(TimeSpan.FromMilliseconds(150));
        env.Consent.Custom = () => new TaskCompletionSource<bool>().Task; // the window is ignored
        var started = DateTime.UtcNow;
        var r = await env.Resolve(Path.Combine(env.Elsewhere, "out.vsdx"));
        Assert.Equal("path_not_permitted", r.Error!.Code);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(60), ExportPathPolicy.ConsentTimeout); // the production wait is a minute
    }

    [Fact]
    public async Task ReplacingAnExistingFileNeedsConsentEvenInsideTheFolder()
    {
        using var env = new Env();
        var file = Path.Combine(env.Exports, "keep.vsdx");
        File.WriteAllText(file, "precious");
        var denied = await env.Resolve("keep.vsdx");
        Assert.Equal("path_not_permitted", denied.Error!.Code);
        Assert.Contains("replace", Assert.Single(env.Consent.Asked).Action);
        Assert.Equal("precious", File.ReadAllText(file));
        env.Consent.Answer = true;
        var yes = (await env.Resolve("keep.vsdx")).Value;
        Assert.True(yes.Existed && yes.Consented);
    }

    [Theory]
    [InlineData(@"\\server\share\a.vsdx")]
    [InlineData(@"//server/share/a.vsdx")]
    [InlineData(@"\\.\pipe\a.vsdx")]
    [InlineData(@"\\?\C:\temp\a.vsdx")]
    [InlineData(@"C:\temp\a.vsdx:hidden")]
    [InlineData("a.vsdx:stream")]
    [InlineData("CON.vsdx")]
    [InlineData("nul")]
    [InlineData(@"sub\aux.vsdx")]
    [InlineData("COM1.vsdx")]
    [InlineData("lpt9.vsdx")]
    [InlineData("")]
    public async Task NeverAllowedEvenWithConsent(string path)
    {
        using var env = new Env();
        env.Consent.Answer = true; // saying yes must not matter
        var r = await env.Resolve(path);
        Assert.False(r.IsOk, $"{path} should be refused");
        Assert.Equal("path_not_permitted", r.Error!.Code);
        Assert.Empty(env.Consent.Asked); // refused before anyone is bothered
    }

    [Theory]
    [InlineData("vsdx", "a.exe")]
    [InlineData("vsdx", "a.png")]
    [InlineData("png", "a.vsdx")]
    [InlineData("svg", "a")]
    [InlineData("pdf", "a.pdf")]
    public async Task ExtensionMustMatchTheFormat(string format, string name)
    {
        using var env = new Env();
        Assert.Equal("invalid_request", (await env.Resolve(name, format)).Error!.Code);
        foreach (var (f, n) in new[] { ("jpeg", "a.jpg"), ("jpeg", "a.JPEG"), ("png", "a.PNG") }) Assert.True((await env.Resolve(n, f)).IsOk);
    }

    [Fact]
    public async Task ALinkInsideTheFolderThatLeadsOutIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var env = new Env();
        var link = Path.Combine(env.Exports, "portal");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{env.Elsewhere}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create the test junction");
        env.Consent.Answer = true; // even a yes cannot make a link escape look like the export folder
        var r = await env.Resolve(@"portal\out.vsdx");
        Assert.Equal("path_not_permitted", r.Error!.Code);
        Assert.Contains("link", r.Error.Message);
        Assert.Empty(env.Consent.Asked);
        Assert.False(File.Exists(Path.Combine(env.Elsewhere, "out.vsdx")));
        Directory.Delete(link); // removes the junction, not what it points at
    }

    [Fact]
    public async Task LateConsentAfterTheSessionChangedWritesNothing()
    {
        using var env = new Env();
        env.Consent.Custom = () =>
        {
            env.Scope = new Scope { DocumentId = "d2", SessionId = "s2" }; // the person opened another document while the question was showing
            return Task.FromResult(true);
        };
        var r = await env.Resolve(Path.Combine(env.Elsewhere, "out.vsdx"));
        Assert.Equal("session_mismatch", r.Error!.Code);
    }

    [Fact]
    public async Task TheDestinationIsCheckedAgainRightBeforeTheWrite()
    {
        using var env = new Env();
        var dest = (await env.Resolve("fresh.vsdx")).Value;
        Assert.True(env.Policy.Revalidate(dest, env.Scope).IsOk);

        File.WriteAllText(dest.Path, "someone else got there first");
        Assert.Equal("destination_changed", env.Policy.Revalidate(dest, env.Scope).Error!.Code);
        File.Delete(dest.Path);

        Directory.CreateDirectory(dest.Path); // or made a folder of that name
        Assert.Equal("path_not_permitted", env.Policy.Revalidate(dest, env.Scope).Error!.Code);
        Directory.Delete(dest.Path);

        env.Scope = new Scope { DocumentId = "d1", SessionId = "s9" };
        Assert.Equal("session_mismatch", env.Policy.Revalidate(dest, env.Scope).Error!.Code);
    }

    [Fact]
    public async Task ApprovalForOneFileDoesNotCarryOverToAnother()
    {
        using var env = new Env();
        env.Consent.Answer = true;
        var approved = (await env.Resolve(Path.Combine(env.Elsewhere, "ok.vsdx"))).Value;
        var forged = approved with { Path = Path.Combine(env.Elsewhere, "other.vsdx"), Consented = false };
        Assert.Equal("path_not_permitted", env.Policy.Revalidate(forged, env.Scope).Error!.Code);
    }
}
