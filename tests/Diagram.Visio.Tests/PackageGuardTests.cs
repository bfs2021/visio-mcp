using System.IO.Compression;
using Diagram.Visio;
using Xunit;

public class PackageGuardTests
{
    static readonly byte[] Types = Fx.Utf8("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""");
    static byte[] Good(params (string, byte[])[] more) => Fx.Zip([("[Content_Types].xml", Types), .. more]);
    static Diagram.Host.Core.Result<GuardedPackage> Open(byte[] zip, ImportOptions? o = null) => PackageGuard.Open(new MemoryStream(zip), o ?? new ImportOptions());

    [Fact]
    public void AnOrdinaryPackageOpensAndNothingIsDecompressedUntilAskedFor()
    {
        using var p = Open(Good(("visio/document.xml", Fx.Utf8("<a/>")))).Value;
        Assert.Equal(0, p.PartsRead);
        Assert.True(p.ReadXml("visio/document.xml").IsOk);
        Assert.Equal(1, p.PartsRead);
    }

    [Fact]
    public void NotAZipIsInvalid()
    {
        var r = Open(Fx.Utf8("this is not a zip file"));
        Assert.Equal("invalid_request", r.Error!.Code);
        Assert.Equal("invalid_request", Open(Fx.Zip(("visio/document.xml", Fx.Utf8("<a/>")))).Error!.Code); // no content types part
    }

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("visio/../../evil.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("C:/windows/evil.xml")]
    [InlineData("visio\\..\\evil.xml")]
    [InlineData("visio/./x.xml")]
    public void TraversalAndAbsoluteNamesAreRefusedBeforeAnythingIsRead(string name)
    {
        var zip = Fx.Zip(("[Content_Types].xml", Types), (name, Fx.Utf8("<a/>")));
        var r = Open(zip);
        Assert.Equal("invalid_request", r.Error!.Code);
        Assert.Contains("unsafe part name", r.Error.Message);
    }

    [Fact]
    public void DuplicateNamesAreRefused()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var n in new[] { "[Content_Types].xml", "visio/a.xml", "visio/A.xml" }) { using var s = z.CreateEntry(n).Open(); s.Write(Types); }
        }
        Assert.Equal("invalid_request", Open(ms.ToArray()).Error!.Code);
    }

    [Fact]
    public void ADecompressionBombIsStoppedByTheBytesActuallyRead()
    {
        // 40 MiB of zeros compresses to a few kilobytes
        var zeros = new byte[40 * 1024 * 1024];
        var zip = Good(("visio/pages/page1.xml", zeros));
        Assert.True(zip.Length < 200_000);
        var small = new ImportOptions(MaxExpandedBytes: 8 * 1024 * 1024);
        var declared = Open(zip, small);
        Assert.Equal("limit_exceeded", declared.Error!.Code); // the declared sizes already exceed the budget

        // a package that lies about its sizes is caught while reading
        var lying = Lie(zip);
        using var p = Open(lying, small).Value;
        var r = p.Read("visio/pages/page1.xml");
        // the reader stops at what the header claims or fails: either way no more than the budget is ever held in memory
        Assert.True(r.IsOk ? r.Value.Length <= 8 * 1024 * 1024 : r.Error!.Code is "limit_exceeded" or "invalid_request");
    }

    [Fact]
    public void ARatioBombThatFitsTheBudgetIsStillRefused()
    {
        var zeros = new byte[20 * 1024 * 1024];
        using var p = Open(Good(("visio/pages/page1.xml", zeros))).Value;
        Assert.Equal("limit_exceeded", p.Read("visio/pages/page1.xml").Error!.Code);
    }

    [Fact]
    public void TooManyPartsAndOversizePackagesAreRefused()
    {
        var many = Fx.Zip(Enumerable.Range(0, 30).Select(i => ($"visio/p{i}.xml", Fx.Utf8("<a/>"))).Append(("[Content_Types].xml", Types)).ToArray());
        Assert.Equal("limit_exceeded", Open(many, new ImportOptions(MaxEntries: 10)).Error!.Code);
        Assert.Equal("limit_exceeded", Open(Good(), new ImportOptions(MaxPackageBytes: 100)).Error!.Code);
    }

    [Fact]
    public void DtdsAndEntitiesAreNeverExpanded()
    {
        var xxe = """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><r>&x;</r>""";
        var bomb = """<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]><lolz>&b;&b;&b;&b;&b;</lolz>""";
        using var p = Open(Good(("visio/a.xml", Fx.Utf8(xxe)), ("visio/b.xml", Fx.Utf8(bomb)))).Value;
        var a = p.ReadXml("visio/a.xml");
        Assert.Equal("invalid_request", a.Error!.Code);
        Assert.DoesNotContain("win.ini", a.Error.Message.Replace("DTD is prohibited", ""), StringComparison.Ordinal);
        Assert.Equal("invalid_request", p.ReadXml("visio/b.xml").Error!.Code);
    }

    [Fact]
    public void MacrosAndEmbeddedObjectsAreListedButNeverOpened()
    {
        using var p = Open(Good(("visio/vbaProject.bin", Fx.Utf8("MACRO")), ("visio/embeddings/oleObject1.bin", Fx.Utf8("OLE")), ("visio/document.xml", Fx.Utf8("<a/>")))).Value;
        Assert.Contains(p.Diagnostics, d => d.Code == "inert_part_ignored" && d.Detail == "visio/vbaProject.bin");
        Assert.Contains(p.Diagnostics, d => d.Code == "inert_part_ignored" && d.Detail == "visio/embeddings/oleObject1.bin");
        Assert.Equal("path_not_permitted", p.Read("visio/vbaProject.bin").Error!.Code);
        Assert.Equal("path_not_permitted", p.Read("visio/embeddings/oleObject1.bin").Error!.Code);
        Assert.Equal(0, p.PartsRead);
    }

    [Theory]
    [InlineData("visio/pages/page1.xml", "media/image1.png", false, "visio/pages/media/image1.png")]
    [InlineData("visio/pages/page1.xml", "../media/image1.png", false, "visio/media/image1.png")]
    [InlineData("visio/pages/page1.xml", "/visio/media/image1.png", false, "visio/media/image1.png")]
    [InlineData("visio/pages/page1.xml", "https://example.com/x.png", true, null)]
    public void RelationshipTargetsResolveInsideThePackageOrNotAtAll(string owner, string target, bool external, string? expected)
    {
        var r = PackageGuard.ResolveTarget(owner, target, external);
        Assert.True(r.IsOk);
        Assert.Equal(expected, r.Value);
    }

    [Theory]
    [InlineData("../../../outside.png")]
    [InlineData("file:///C:/secret.png")]
    [InlineData("\\\\server\\share\\x.png")]
    public void RelationshipTargetsThatEscapeArePackageErrors(string target)
        => Assert.False(PackageGuard.ResolveTarget("visio/pages/page1.xml", target, false).IsOk);

    /// <summary>Rewrites the central directory so a part claims to be tiny, as a hostile archive would.</summary>
    static byte[] Lie(byte[] zip)
    {
        var copy = (byte[])zip.Clone();
        // central directory file header signature 0x02014B50; uncompressed size is at offset 24 within it
        for (var i = 0; i < copy.Length - 4; i++)
            if (copy[i] == 0x50 && copy[i + 1] == 0x4B && copy[i + 2] == 0x01 && copy[i + 3] == 0x02) BitConverter.GetBytes(10u).CopyTo(copy, i + 24);
        return copy;
    }
}
