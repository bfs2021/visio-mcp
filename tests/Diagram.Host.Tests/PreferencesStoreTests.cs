using System.Text;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;
namespace Diagram.Host.Tests;

public class PreferencesStoreTests
{
    static PreferencesStore Make(TempDir dir, AtomicFileWriter? writer = null) => new(dir.File("preferences.json"), writer ?? new());
    [Fact] public async Task MissingUsesDefaultsWithoutCreatingAFile()
    {
        using var d = new TempDir(); var s = await Make(d).ReadAsync();
        Assert.Equal("mm", s.Preferences.Units); Assert.Null(s.Diagnostic); Assert.False(File.Exists(d.File("preferences.json")));
        Assert.Equal(s.VersionTag, (await Make(d).ReadAsync()).VersionTag);
    }
    [Fact] public async Task SaveSurvivesRestartAndRootsAreIsolated()
    {
        using var d = new TempDir(); using var other = new TempDir(); var store = Make(d); var before = await store.ReadAsync();
        var result = await store.SaveAsync(before.Preferences with { Theme = "dark" }, before.VersionTag, false);
        Assert.Equal(PreferencesSaveStatus.Success, result.Status); Assert.Equal("dark", (await Make(d).ReadAsync()).Preferences.Theme);
        Assert.Equal("light", (await Make(other).ReadAsync()).Preferences.Theme);
    }
    [Theory] [InlineData("corrupt")] [InlineData("future")] [InlineData("oversized")]
    public async Task InvalidFilesRemainUntouchedAndExplicitResetBacksUpExactBytes(string kind)
    {
        using var d = new TempDir(); var path = d.File("preferences.json");
        var bytes = kind == "oversized" ? Enumerable.Repeat((byte)' ', 65537).ToArray() : Encoding.UTF8.GetBytes(kind == "future" ? "{\"schemaVersion\":2}" : "{broken");
        await File.WriteAllBytesAsync(path, bytes); var store = Make(d); var before = await store.ReadAsync();
        Assert.NotNull(before.Diagnostic); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(BlobStore.HashOf(bytes), before.VersionTag);
        Assert.Equal(PreferencesSaveStatus.ValidationError, (await store.SaveAsync(before.Preferences, before.VersionTag, false)).Status);
        Assert.Equal(PreferencesSaveStatus.Conflict, (await store.SaveAsync(before.Preferences, "stale", true)).Status);
        Assert.Equal(PreferencesSaveStatus.Success, (await store.SaveAsync(before.Preferences, before.VersionTag, true)).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(d.Path, "preferences.json.*.bak"))));
    }
    [Fact] public async Task IndependentWritersUsingSameTagHaveOneWinner()
    {
        using var d = new TempDir(); var a = Make(d); var b = Make(d); var first = await a.ReadAsync();
        var results = await Task.WhenAll(a.SaveAsync(first.Preferences with { Theme = "dark" }, first.VersionTag, false), b.SaveAsync(first.Preferences with { Units = "in" }, first.VersionTag, false));
        Assert.Single(results, r => r.Status == PreferencesSaveStatus.Success); var conflict = Assert.Single(results, r => r.Status == PreferencesSaveStatus.Conflict);
        Assert.Equal((await a.ReadAsync()).VersionTag, conflict.Snapshot.VersionTag);
    }
    [Fact] public async Task FailedReplacementRetainsOldBytesAndEffectiveSettings()
    {
        using var d = new TempDir(); var store = Make(d); var first = await store.ReadAsync(); await store.SaveAsync(first.Preferences, first.VersionTag, false);
        var bytes = await File.ReadAllBytesAsync(d.File("preferences.json")); var current = await store.ReadAsync();
        var failing = Make(d, new() { BeforeReplace = () => throw new IOException("injected replacement failure") });
        var result = await failing.SaveAsync(current.Preferences with { Theme = "dark" }, current.VersionTag, false);
        Assert.Equal(PreferencesSaveStatus.IoError, result.Status); Assert.Equal("light", result.Snapshot.Preferences.Theme); Assert.Equal(bytes, await File.ReadAllBytesAsync(d.File("preferences.json")));
    }
    [Fact] public async Task UnreadableSourceCannotBeResetWithoutBackup()
    {
        using var d = new TempDir(); var path = d.File("preferences.json"); await File.WriteAllTextAsync(path, "original");
        await using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var store = Make(d); var before = await store.ReadAsync(); Assert.NotNull(before.Diagnostic);
            Assert.Equal(PreferencesSaveStatus.IoError, (await store.SaveAsync(before.Preferences, before.VersionTag, true)).Status);
        }
        Assert.Equal("original", await File.ReadAllTextAsync(path)); Assert.Empty(Directory.GetFiles(d.Path, "*.bak"));
    }
    [Fact] public async Task ExclusiveSiblingLockWaitIsCancellable()
    {
        using var d = new TempDir(); var store = Make(d); var before = await store.ReadAsync();
        await using var held = new FileStream(d.File("preferences.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(before.Preferences, before.VersionTag, false, cts.Token));
        Assert.False(File.Exists(d.File("preferences.json")));
    }

    [Fact] public async Task ExternalEditBeforeAtomicReplaceReturnsConflictAndPreservesItsBytes()
    {
        using var d = new TempDir(); var path = d.File("preferences.json");
        var writer = new AtomicFileWriter(); var store = Make(d, writer); var first = await store.ReadAsync();
        var external = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");
        writer.BeforeReplace = () => File.WriteAllBytes(path, external);
        var result = await store.SaveAsync(first.Preferences with { Theme = "dark" }, first.VersionTag, false);
        Assert.Equal(PreferencesSaveStatus.Conflict, result.Status);
        Assert.Equal(external, await File.ReadAllBytesAsync(path));
        Assert.Equal(BlobStore.HashOf(external), result.Snapshot.VersionTag);
    }

    [Fact] public async Task FailedWriteRetainsPreviouslyPublishedEffectivePreferences()
    {
        using var d = new TempDir(); var writer = new AtomicFileWriter(); var store = Make(d, writer);
        var first = await store.ReadAsync();
        var saved = await store.SaveAsync(first.Preferences with { Theme = "dark" }, first.VersionTag, false);
        Assert.Equal(PreferencesSaveStatus.Success, saved.Status);
        var external = saved.Snapshot.Preferences with { Theme = "light" };
        var externalStore = Make(d);
        var externalSave = await externalStore.SaveAsync(external, saved.Snapshot.VersionTag, false);
        Assert.Equal(PreferencesSaveStatus.Success, externalSave.Status);
        writer.BeforeReplace = () => throw new IOException("injected failure");
        var failed = await store.SaveAsync(saved.Snapshot.Preferences with { Units = "in" }, externalSave.Snapshot.VersionTag, false);
        Assert.Equal(PreferencesSaveStatus.IoError, failed.Status);
        Assert.Equal("dark", failed.Snapshot.Preferences.Theme);
        Assert.Equal(saved.Snapshot.VersionTag, failed.Snapshot.VersionTag);
    }

    [Fact] public async Task InvalidNumericPreferenceIsRejectedWithoutWriting()
    {
        using var d = new TempDir(); var store = Make(d); var first = await store.ReadAsync();
        var invalid = first.Preferences with { Grid = first.Preferences.Grid with { SpacingPt = double.NaN } };
        var result = await store.SaveAsync(invalid, first.VersionTag, false);
        Assert.Equal(PreferencesSaveStatus.ValidationError, result.Status);
        Assert.False(File.Exists(d.File("preferences.json")));
    }

    [Fact] public async Task FailedBackupCreationPreservesInvalidOriginalAndEffectiveDefaults()
    {
        using var d = new TempDir(); var path = d.File("preferences.json");
        var original = Encoding.UTF8.GetBytes("{broken");
        await File.WriteAllBytesAsync(path, original);
        var store = Make(d); var before = await store.ReadAsync();
        store.BeforeBackup = () => throw new IOException("injected backup creation failure");
        var failed = await store.SaveAsync(before.Preferences with { Theme = "dark" }, before.VersionTag, true);
        Assert.Equal(PreferencesSaveStatus.IoError, failed.Status);
        Assert.Equal(before, failed.Snapshot);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(d.Path, "*.bak"));
    }
}
