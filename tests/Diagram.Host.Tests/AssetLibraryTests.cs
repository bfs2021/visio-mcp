using System.Text;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Host.Tests;

public class AssetLibraryTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly BlobStore Blobs;
        public readonly AssetPreparer Preparer;
        public readonly AssetLibrary Library;
        public readonly AssetResolver Resolver;
        public Env() { Blobs = new BlobStore(Dir.File("blobs")); Preparer = new AssetPreparer(Blobs); Library = new AssetLibrary(Dir.File("library"), Blobs); Resolver = new AssetResolver(Library, Preparer); }
        public async Task<PreparedUpload> Prepare(byte[] bytes, string mime, string name)
        {
            var r = await Preparer.PrepareAsync(new MemoryStream(bytes), mime, name, default);
            Assert.True(r.IsOk, r.Error?.Message);
            return r.Value;
        }
        public void Dispose() => Dir.Dispose();
    }

    static byte[] Png(int w, int h) => Bytes.PngHeader(w, h);

    [Fact]
    public async Task SourceReplaceDoesNotModifyOpenDocument()
    {
        using var env = new Env();
        var v1 = await env.Prepare(Png(10, 10), "image/png", "logo.png");
        var imported = (await env.Library.ImportAsync(v1, ["brand"], default)).Value;
        Assert.Equal("asset:logo", imported.Asset.Id);

        // a document pins what the library says today
        var pinned = (await env.Resolver.ResolveAsync("asset:logo", "library", null, default)).Value.Ref.Asset;

        var v2 = await env.Prepare(Png(20, 20), "image/png", "logo.png");
        var replaced = (await env.Library.ReplaceSourceAsync("asset:logo", v2, default)).Value;
        Assert.Equal(2, replaced.Version);
        Assert.Equal(v2.Ref.Asset.Sha256, replaced.Sha256);

        // what the document pinned is unchanged and its bytes are still there
        Assert.Equal(v1.Ref.Asset.Sha256, pinned.Sha256);
        Assert.True(env.Blobs.Contains(pinned.Sha256));
        var again = (await env.Resolver.ResolveAsync("asset:logo", "library", v1.Ref.Asset.Sha256, default)).Value.Ref.Asset;
        Assert.Equal(v1.Ref.Asset.Sha256, again.Sha256); // the old version is reachable by its hash
        var current = (await env.Resolver.ResolveAsync("asset:logo", "library", null, default)).Value.Ref.Asset;
        Assert.Equal(v2.Ref.Asset.Sha256, current.Sha256);
        var unknown = await env.Resolver.ResolveAsync("asset:logo", "library", new string('a', 64), default);
        Assert.Equal("not_found", unknown.Error!.Code);
    }

    [Fact]
    public async Task SanitisedHashStable()
    {
        using var env = new Env();
        var dirty = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><script>alert(1)</script><rect width=\"10\" height=\"10\"/></svg>");
        var a = await env.Prepare(dirty, "image/svg+xml", "mark.svg");
        var b = await env.Prepare(dirty, "image/svg+xml", "mark.svg");
        Assert.Equal(a.Ref.Asset.Sha256, b.Ref.Asset.Sha256); // the hash is of the sanitised bytes, so it does not move
        Assert.DoesNotContain("script", Encoding.UTF8.GetString(env.Blobs.Read(a.Ref.Asset.Sha256)));
        var first = (await env.Library.ImportAsync(a, null, default)).Value;
        var second = (await env.Library.ImportAsync(b, null, default)).Value;
        Assert.True(first.Created);
        Assert.False(second.Created); // the same bytes under the same name are not added twice
        Assert.Single(await env.Library.ListAsync(null, null, default));
    }

    [Fact]
    public async Task ListReturnsScopeAndVersion()
    {
        using var env = new Env();
        await env.Library.ImportAsync(await env.Prepare(Png(10, 10), "image/png", "Acme Logo.png"), ["brand", "Logo"], default);
        await env.Library.ImportAsync(await env.Prepare(Png(12, 8), "image/png", "diagram.png"), ["figure"], default);
        var all = await env.Library.ListAsync(null, null, default);
        Assert.Equal(["asset:acme-logo", "asset:diagram"], all.Select(a => a.Id));
        Assert.All(all, a => { Assert.Equal("library", a.Scope); Assert.Equal(1, a.Version); });
        Assert.Equal(["brand", "logo"], all[0].Tags);
        Assert.Equal((10, 10), (all[0].Width, all[0].Height));
        Assert.Single(await env.Library.ListAsync("acme", null, default));
        Assert.Single(await env.Library.ListAsync(null, ["FIGURE"], default));
        Assert.Empty(await env.Library.ListAsync("nothing", null, default));
    }

    [Fact]
    public async Task DurableBlobBeforeCatalogue()
    {
        using var env = new Env();
        var prepared = await env.Prepare(Png(10, 10), "image/png", "logo.png");
        env.Library.BeforeCatalogueReplace = () => throw new IOException("disk full");
        await Assert.ThrowsAsync<IOException>(() => env.Library.ImportAsync(prepared, null, default));
        Assert.True(env.Blobs.Contains(prepared.Ref.Asset.Sha256)); // the bytes were safe first
        Assert.Empty(await env.Library.ListAsync(null, null, default)); // and nothing names them until the catalogue is written
        env.Library.BeforeCatalogueReplace = null;
        Assert.True((await env.Library.ImportAsync(prepared, null, default)).IsOk);
        Assert.Single(await env.Library.ListAsync(null, null, default));
    }

    [Fact]
    public async Task ACatalogueEntryWhoseBytesAreGoneIsNotOffered()
    {
        using var env = new Env();
        var p = await env.Prepare(Png(10, 10), "image/png", "logo.png");
        await env.Library.ImportAsync(p, null, default);
        File.Delete(Path.Combine(env.Dir.File("blobs"), p.Ref.Asset.Sha256));
        Assert.Empty(await env.Library.ListAsync(null, null, default));
        Assert.Equal("not_found", (await env.Resolver.ResolveAsync("asset:logo", "library", null, default)).Error!.Code);
    }

    [Fact]
    public async Task ADifferentPictureUnderATakenNameGetsTheFullHashIdAndNothingIsOverwritten()
    {
        using var env = new Env();
        var a = await env.Prepare(Png(10, 10), "image/png", "logo.png");
        var b = await env.Prepare(Png(30, 30), "image/png", "logo.png");
        await env.Library.ImportAsync(a, null, default);
        var second = (await env.Library.ImportAsync(b, null, default)).Value;
        Assert.True(second.IdWasTaken);
        Assert.Equal($"asset:logo~{b.Ref.Asset.Sha256}", second.Asset.Id);
        Assert.Equal(a.Ref.Asset.Sha256, (await env.Library.GetAsync("asset:logo", default))!.Sha256);
    }

    [Fact]
    public async Task RenameTagsAndDeleteLeaveTheBytesForDocumentsThatPinThem()
    {
        using var env = new Env();
        var p = await env.Prepare(Png(10, 10), "image/png", "logo.png");
        await env.Library.ImportAsync(p, null, default);
        var meta = (await env.Library.SetMetadataAsync("asset:logo", "Brand mark", ["Brand", "brand", " "], default)).Value;
        Assert.Equal("Brand mark", meta.Name);
        Assert.Equal(["brand"], meta.Tags);
        Assert.Equal("invalid_request", (await env.Library.SetMetadataAsync("asset:logo", "", null, default)).Error!.Code);
        Assert.True((await env.Library.DeleteAsync("asset:logo", default)).IsOk);
        Assert.Equal("not_found", (await env.Library.DeleteAsync("asset:logo", default)).Error!.Code);
        Assert.True(env.Blobs.Contains(p.Ref.Asset.Sha256));
    }

    [Fact]
    public async Task DocumentScopeIsNotResolvedByTheHost()
    {
        using var env = new Env();
        var r = await env.Resolver.ResolveAsync("asset:x", "document", null, default);
        Assert.Equal("invalid_request", r.Error!.Code);
    }
}
