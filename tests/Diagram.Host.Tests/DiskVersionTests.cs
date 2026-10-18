using System.Text;
using Diagram.Host.Core;
using Xunit;
namespace Diagram.Host.Tests;
public class DiskVersionTests
{
    [Fact]
    public async Task HashDetectsSameSizeWriteEvenWithPreservedTimestampAndDeletion()
    {
        using var dir = new TempDir(); var path = dir.File("shared.json");
        await File.WriteAllTextAsync(path, "aaaa"); var before = await DiskVersion.ReadAsync(path, default);
        await File.WriteAllTextAsync(path, "bbbb"); File.SetLastWriteTimeUtc(path, before.LastWriteUtc!.Value);
        var after = await DiskVersion.ReadAsync(path, default);
        Assert.Equal(before.Length, after.Length); Assert.Equal(before.LastWriteUtc, after.LastWriteUtc); Assert.NotEqual(before, after);
        File.Delete(path); Assert.False((await DiskVersion.ReadAsync(path, default)).Exists);
    }
    [Fact]
    public async Task OpenBaselineNamesBytesReadRatherThanFileChangedBeforePublication()
    {
        using var dir = new TempDir(); var blobs = new BlobStore(dir.File("blobs")); var editor = new FakeEditor(Docs.Make([]));
        var coordinator = new SessionCoordinator(editor, blobs, new AssetPreparer(blobs), new AtomicFileWriter());
        var path = dir.File("drawing.json"); Assert.True((await coordinator.SaveJsonAsync(path, editor.Scope, default)).IsOk);
        var original = await File.ReadAllBytesAsync(path);
        editor.BeforeReplace = _ => File.AppendAllText(path, "\n ");
        var opened = await coordinator.OpenJsonAsync(path, new() { DocumentId = editor.Document.Id, SessionId = editor.SessionId, BaseRevision = editor.Revision }, default);
        Assert.True(opened.IsOk); Assert.Equal(BlobStore.HashOf(original), opened.Value.Version!.Sha256);
        Assert.NotEqual(opened.Value.Version, await DiskVersion.ReadAsync(path, default));
    }
    [Fact]
    public async Task FinalGuardPreservesSecondExternalWriteAndDoesNotMarkLocalEditsSaved()
    {
        using var dir = new TempDir(); var blobs = new BlobStore(dir.File("blobs")); var editor = new FakeEditor(Docs.Make([])); var writer = new AtomicFileWriter();
        var coordinator = new SessionCoordinator(editor, blobs, new AssetPreparer(blobs), writer); var path = dir.File("drawing.json");
        var first = await coordinator.SaveJsonAsync(path, editor.Scope, default); editor.Edit();
        var external = Encoding.UTF8.GetBytes("external at commit"); writer.BeforeReplace = () => File.WriteAllBytes(path, external);
        await Assert.ThrowsAsync<DiskChangedException>(() => coordinator.SaveJsonAsync(path, editor.Scope, default, first.Value.Version));
        Assert.Equal(external, await File.ReadAllBytesAsync(path)); Assert.True(editor.Dirty);
        Assert.Single(Directory.GetFiles(dir.Path, "*.json"));
    }
    [Fact]
    public async Task LockedFileIsNotMistakenForMissingOrUnchanged()
    {
        using var dir = new TempDir(); var path=dir.File("locked.json"); await File.WriteAllTextAsync(path,"x");
        using var locked = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => DiskVersion.ReadAsync(path,default));
    }
}
