using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

/// <summary>H01: a recovered session knows which session it replaces, so a crash during or after a restore never offers the same work twice.</summary>
public partial class RecoveryTests
{
    const string Old = "00000000-0000-4000-8000-0000000000a1", Mid = "00000000-0000-4000-8000-0000000000a2", New = "00000000-0000-4000-8000-0000000000a3";

    static async Task<RecoverySession> BeginRestored(Env env, string session, string? restoredFrom, int revision = 3) =>
        await env.Store.BeginSessionAsync(Doc, session, DocJson(revision), revision, null, [], default, Fast, restoredFrom);

    static async Task AppendTo(RecoverySession s, string session, params int[] revisions)
    {
        foreach (var r in revisions) Assert.True((await s.AppendAsync(Ev(r, session), default)).IsOk);
    }

    [Fact]
    public async Task ARestoredSessionNamesTheSessionItReplacesAndKnowsWhetherAnythingHappenedSince()
    {
        using var env = new Env();
        await env.Begin(Old);
        var restored = await BeginRestored(env, New, Old);
        var x = (await env.Store.ReadRecoveryAsync(null, default)).Single(c => c.SessionId == New);
        Assert.Equal(Old, x.RestoredFrom);
        Assert.True(x.RestoredUnedited);                 // exactly what was recovered
        Assert.True(x.KeepUnsaved);                      // recovered work is unsaved work, even before it is edited again
        Assert.Null((await env.Store.ReadRecoveryAsync(null, default)).Single(c => c.SessionId == Old).RestoredFrom);

        Assert.True((await restored.AppendAsync(Ev(4, New), default)).IsOk);
        var edited = (await env.Store.ReadRecoveryAsync(null, default)).Single(c => c.SessionId == New);
        Assert.Equal(Old, edited.RestoredFrom);          // still remembered
        Assert.False(edited.RestoredUnedited);           // but it has moved on
    }

    [Fact]
    public async Task TheMarkerIsInTheSessionFileAtTheMomentTheSessionExists()
    {
        using var env = new Env();
        var s = await BeginRestored(env, New, Old);
        var meta = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(s.Dir, "session.json")))!;
        Assert.Equal(Old, meta["restoredFrom"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUneditedRestoredSessionSurvivesACleanCloseUntilItIsSaved()
    {
        await using var e = new TimerEnv();
        await e.Store.BeginSessionAsync(Doc, Old, DocJson(3), 3, null, [], default, Fast);
        e.Router.OnMessage(TimerOrigin, JsonSerializer.Serialize(new
        {
            protocolVersion = 1, kind = "event", method = "editor.session",
            @params = new { documentId = Doc, sessionId = Sess, revision = 0, replaces = Doc, restoredFrom = Old },
        }), true);
        await e.Manager.DrainAsync();
        await e.Manager.CloseCleanlyAsync();                                                       // nothing was edited since the restore
        var kept = Assert.Single(await e.Store.ReadRecoveryAsync(Old, default));                    // but the recovered work is still there
        Assert.Equal(Sess, kept.SessionId);
        await e.Store.SetSavedAsync(Sess, 0, "C:/plan.diagram.json", default);                      // saving it is what makes it disposable
        Assert.False(Assert.Single(await e.Store.ReadRecoveryAsync(Old, default)).KeepUnsaved);
    }

    [Fact]
    public async Task LineageFindsWhatEachLaterRestoreReplacedThroughAChain()
    {
        using var env = new Env();
        await env.Begin(Old);
        await BeginRestored(env, Mid, Old);
        await BeginRestored(env, New, Mid);
        const string Other = "00000000-0000-4000-8000-0000000000b1";
        await env.Begin(Other);                                           // unrelated pending session
        await BeginRestored(env, "00000000-0000-4000-8000-0000000000b2", "00000000-0000-4000-8000-0000000000ff"); // source long gone: nothing to remove
        var pairs = RecoveryLineage.Superseded(await env.Store.ReadRecoveryAsync(null, default)).OrderBy(p => p.Old).ToList();
        Assert.Equal([(Old, New), (Mid, New)], pairs);
    }

    [Fact]
    public async Task ASessionWithoutAUsableCheckpointSupersedesNothing()
    {
        using var env = new Env();
        await env.Begin(Old);
        var s = await BeginRestored(env, New, Old);
        foreach (var f in Directory.GetFiles(s.Dir, "gen-*.json")) File.Delete(f); // its checkpoint never became durable
        File.Delete(Path.Combine(s.Dir, "manifest.json"));
        var listed = await env.Store.ReadRecoveryAsync(null, default);
        Assert.Equal([Old], listed.Select(c => c.SessionId));              // the half-made session is not a candidate
        Assert.Empty(RecoveryLineage.Superseded(listed));                  // so the old one is kept
    }

    // ---------------------------------------------------------------- the manager
    [Fact]
    public async Task ACrashBeforeTheOldSessionWasRemovedOffersTheWorkOnceAndTellsTheShell()
    {
        await using var e = new TimerEnv();
        var oldSession = await e.Store.BeginSessionAsync(Doc, Old, DocJson(0), 0, "C:/plan.diagram.json", [], default, Fast);
        await AppendTo(oldSession, Old, 1, 2, 3);
        var told = new List<(string, string)>();
        e.Manager.OnSuperseded = (o, n) => { told.Add((o, n)); return Task.CompletedTask; };
        await e.Store.BeginSessionAsync(Doc, New, DocJson(3), 3, "C:/plan.diagram.json", [], default, Fast, Old);

        var offered = await e.Manager.CandidatesAsync(default);
        Assert.Equal([New], offered.Select(c => c.SessionId));
        Assert.Equal([(Old, New)], told);
        Assert.False(Directory.Exists(Path.Combine(e.Dir.File("recovery"), Old)));   // the superseded session is gone
        Assert.True(Directory.Exists(Path.Combine(e.Dir.File("recovery"), New)));    // what was recovered is still protected
        Assert.Equal([New], (await e.Manager.CandidatesAsync(default)).Select(c => c.SessionId)); // and asking again changes nothing
    }

    [Fact]
    public async Task IfTheShellCannotBeToldTheOldSessionIsHiddenButKept()
    {
        await using var e = new TimerEnv();
        var oldSession = await e.Store.BeginSessionAsync(Doc, Old, DocJson(0), 0, null, [], default, Fast);
        await AppendTo(oldSession, Old, 1, 2);
        e.Manager.OnSuperseded = (_, _) => throw new IOException("workspace.json is locked");
        await e.Store.BeginSessionAsync(Doc, New, DocJson(2), 2, null, [], default, Fast, Old);
        Assert.Equal([New], (await e.Manager.CandidatesAsync(default)).Select(c => c.SessionId));
        Assert.True(Directory.Exists(Path.Combine(e.Dir.File("recovery"), Old)));    // nothing lost while the tab still names it
        e.Manager.OnSuperseded = (_, _) => Task.CompletedTask;                        // the shell recovers
        await e.Manager.ReconcileAsync(default);
        Assert.False(Directory.Exists(Path.Combine(e.Dir.File("recovery"), Old)));
    }

    [Fact]
    public async Task ASessionTheEditorStillHasOpenIsNeverRemovedAsSuperseded()
    {
        await using var e = new TimerEnv();
        await e.OpenAsync();                                                           // Sess is open and followed
        await e.Store.BeginSessionAsync(Doc, "00000000-0000-4000-8000-0000000000c9", DocJson(5), 5, null, [], default, Fast, Sess);
        await e.Manager.ReconcileAsync(default);
        Assert.True(Directory.Exists(Path.Combine(e.Dir.File("recovery"), Sess)));
    }

    [Fact]
    public async Task APageAnnouncementThatSaysItRestoredASessionIsWrittenDownWithTheNewSession()
    {
        await using var e = new TimerEnv();
        e.Router.OnMessage(TimerOrigin, JsonSerializer.Serialize(new
        {
            protocolVersion = 1, kind = "event", method = "editor.session",
            @params = new { documentId = Doc, sessionId = Sess, revision = 0, replaces = Doc, restoredFrom = Old },
        }), true);
        await e.Manager.DrainAsync();
        var meta = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(e.Dir.File("recovery"), Sess, "session.json")))!;
        Assert.Equal(Old, meta["restoredFrom"]!.GetValue<string>());
        // and an ordinary new session says nothing of the kind
        Assert.Null(e.Router.TakeRestoredFrom(Sess));
    }

    [Fact]
    public async Task AnOrdinarySessionHasNoRestoredFromAndIsNotTreatedAsRestored()
    {
        await using var e = new TimerEnv();
        await e.OpenAsync();
        var c = Assert.Single(await e.Store.ReadRecoveryAsync(null, default));
        Assert.Null(c.RestoredFrom);
        Assert.False(c.RestoredUnedited);
    }
}
