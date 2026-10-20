using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class LaunchRequestTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "launch-" + Guid.NewGuid().ToString("N"));
    string Make(string name) { Directory.CreateDirectory(dir); var p = Path.Combine(dir, name); File.WriteAllText(p, "{}"); return p; }
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void FilesAreClassifiedByExtensionAndMadeFullPaths()
    {
        var diagram = Make("plan.diagram.json"); var visio = Make("Plan.VSDX");
        var r = LaunchRequest.Parse([diagram, visio]);
        Assert.Equal([new LaunchFile(diagram, "json"), new LaunchFile(visio, "vsdx")], r.Files);
        Assert.Empty(r.Skipped);
        Assert.Null(r.Command);
    }

    [Fact]
    public void ARelativePathMeansWhereTheLaunchWasStarted()
    {
        var p = Make("rel.diagram.json");
        var old = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            Assert.Equal([new LaunchFile(p, "json")], LaunchRequest.Parse(["rel.diagram.json"]).Files);
        }
        finally { Directory.SetCurrentDirectory(old); }
    }

    [Fact]
    public void WhatCannotBeOpenedIsReportedNotSilentlyDropped()
    {
        var txt = Make("notes.txt");
        var r = LaunchRequest.Parse([txt, Path.Combine(dir, "gone.vsdx"), "--bogus", "", "a\0b.json"]);
        Assert.Empty(r.Files);
        Assert.Equal(5, r.Skipped.Count);
        Assert.Contains(r.Skipped, s => s.Contains("notes.txt") && s.Contains("only .diagram.json and .vsdx"));
        Assert.Contains(r.Skipped, s => s.Contains("gone.vsdx") && s.Contains("does not exist"));
    }

    [Fact]
    public void TheSameFileTwiceOpensOnceAndTheNumberOfFilesIsCapped()
    {
        var a = Make("a.diagram.json");
        Assert.Single(LaunchRequest.Parse([a, a.ToUpperInvariant().Replace(dir.ToUpperInvariant(), dir)]).Files);
        var many = Enumerable.Range(0, LaunchRequest.MaxFiles + 3).Select(i => Make($"f{i}.diagram.json")).ToList();
        var r = LaunchRequest.Parse(many);
        Assert.Equal(LaunchRequest.MaxFiles, r.Files.Count);
        Assert.Equal(3, r.Skipped.Count);
    }

    [Fact]
    public void RegistrationCommandsAreRecognised()
    {
        Assert.Equal(LaunchRequest.Register, LaunchRequest.Parse(["--register-file-types"]).Command);
        Assert.Equal(LaunchRequest.Unregister, LaunchRequest.Parse(["--unregister-file-types"]).Command);
    }

    [Fact]
    public void AForwardedListMustAlreadyBeFullPaths()
    {
        var p = Make("x.diagram.json");
        Assert.Single(LaunchRequest.FromForwarded([p]).Files);
        var r = LaunchRequest.FromForwarded(["x.diagram.json", "..\\x.diagram.json"]);
        Assert.Empty(r.Files);
        Assert.All(r.Skipped, s => Assert.Contains("not a full path", s));
    }
}
