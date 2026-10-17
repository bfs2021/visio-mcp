using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Visio;
using Xunit;

public class ImageTests
{
    static void Near(double expected, double? actual, double tol = 0.01) => Assert.True(actual is { } a && Math.Abs(a - expected) <= tol, $"expected {expected} but was {actual}");

    static object Asset(string id, string name, string mime, string sha) => new { id, name, mimeType = mime, sha256 = sha, tags = new[] { "t" }, provenance = "test" };
    static JsonNode Image(int n, string assetId, double x, double y, double w, double h, string fit = "contain", bool preserve = true, double opacity = 1, double rot = 0) =>
        JsonSerializer.SerializeToNode(new { id = Doc.Id(n), kind = "image", alias = $"img{n}", layerIds = Array.Empty<string>(), zIndex = n, locked = false, hidden = false, bounds = new { x, y, width = w, height = h }, rotationDeg = rot, metadata = new { }, assetId, fit, opacity, preserveAspectRatio = preserve })!;

    static (ExportSnapshot Snap, Blobs Blobs, string Sha) One(byte[] bytes, string mime, JsonNode image)
    {
        var blobs = new Blobs();
        var sha = blobs.Add(bytes);
        var doc = Doc.Make([Doc.Page([image])], [Asset("asset:a", "a", mime, sha)]);
        return (Doc.Snapshot(doc), blobs, sha);
    }

    static XElement Picture(byte[] package) => Fx.Part(package, "visio/pages/page1.xml").Descendants().First(e => e.Name.LocalName == "Shape");
    static string? Cell(XElement s, string n) => s.Elements().FirstOrDefault(c => c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == n)?.Attribute("V")?.Value;

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpeg")]
    [InlineData("image/bmp", "bmp")]
    public void EachFormatBecomesAnIndependentPictureAndTheBytesComeBackUnchanged(string mime, string ext)
    {
        var bytes = mime switch { "image/png" => Fx.Png(32, 16), "image/jpeg" => Fx.Jpeg(32, 16), _ => Fx.Bmp(32, 16) };
        var (snap, blobs, sha) = One(bytes, mime, Image(1, "asset:a", 100, 100, 160, 80));
        var (exp, back) = RT.Trip(snap, blobs);
        var shape = Picture(exp.Package);
        Assert.Equal("Foreign", (string?)shape.Attribute("Type")); // a Visio picture, not a drawing of one
        Assert.Contains(Fx.Names(exp.Package), n => n.StartsWith("visio/media/") && n.EndsWith("." + ext));
        var blob = Assert.Single(back.Blobs);
        Assert.Equal(sha, blob.Sha256);
        Assert.Equal(bytes, blob.Bytes);
        Assert.Equal(mime, blob.MimeType);
        var asset = Assert.Single(back.Document.Assets);
        Assert.Equal("asset:a", asset.Id);
        Assert.Equal(sha, asset.Sha256);
        Assert.Contains("t", asset.Tags);
        Assert.Equal("test", asset.Provenance);
        var img = RT.El(back.Document, Doc.Id(1));
        Assert.Equal("image", img["kind"]!.GetValue<string>());
        Assert.Equal("asset:a", img["assetId"]!.GetValue<string>());
        Assert.Equal("img1", img["alias"]!.GetValue<string>());
    }

    [Fact]
    public void ContainCoverAndStretchKeepTheirFrameAndFit()
    {
        // a 2:1 picture in a 100 x 100 frame
        foreach (var fit in new[] { "contain", "cover", "stretch" })
        {
            var (snap, blobs, _) = One(Fx.Png(32, 16), "image/png", Image(1, "asset:a", 100, 100, 100, 100, fit));
            var (exp, back) = RT.Trip(snap, blobs);
            var img = RT.El(back.Document, Doc.Id(1));
            Assert.Equal(fit, img["fit"]!.GetValue<string>());
            var b = img["bounds"]!;
            Near(100, b["x"]!.GetValue<double>()); Near(100, b["y"]!.GetValue<double>()); Near(100, b["width"]!.GetValue<double>()); Near(100, b["height"]!.GetValue<double>());
            var shape = Picture(exp.Package);
            var w = double.Parse(Cell(shape, "Width")!, System.Globalization.CultureInfo.InvariantCulture) * 72;
            var h = double.Parse(Cell(shape, "Height")!, System.Globalization.CultureInfo.InvariantCulture) * 72;
            switch (fit)
            {
                case "contain": Near(100, w); Near(50, h); break;   // the whole picture, undistorted, inside the frame
                case "cover": Near(100, w); Near(100, h);           // the frame is filled and the picture is cropped by its own size cells
                    Near(200, double.Parse(Cell(shape, "ImgWidth")!, System.Globalization.CultureInfo.InvariantCulture) * 72);
                    Near(-50, double.Parse(Cell(shape, "ImgOffsetX")!, System.Globalization.CultureInfo.InvariantCulture) * 72); break;
                default: Near(100, w); Near(100, h); break;
            }
        }
    }

    [Fact]
    public void ARotatedPictureAndAHiddenOneKeepTheirState()
    {
        var img = Image(1, "asset:a", 100, 100, 160, 80, rot: 33);
        img["hidden"] = true; img["locked"] = true;
        var (snap, blobs, _) = One(Fx.Png(32, 16), "image/png", img);
        var back = RT.Trip(snap, blobs).Back;
        var e = RT.El(back.Document, Doc.Id(1));
        Near(33, e["rotationDeg"]!.GetValue<double>());
        Assert.True(e["hidden"]!.GetValue<bool>() && e["locked"]!.GetValue<bool>());
    }

    [Fact]
    public void PictureOpacityIsNotMappedButIsRememberedAndReported()
    {
        var (snap, blobs, _) = One(Fx.Png(32, 16), "image/png", Image(1, "asset:a", 100, 100, 160, 80, opacity: 0.4));
        var (exp, back) = RT.Trip(snap, blobs);
        Assert.Contains(exp.Diagnostics, d => d.Code == "image_opacity_not_mapped");
        Near(0.4, RT.El(back.Document, Doc.Id(1))["opacity"]!.GetValue<double>(), 1e-9);
    }

    [Fact]
    public void ADuplicatedAssetIsStoredOncePerPackage()
    {
        var blobs = new Blobs(); var sha = blobs.Add(Fx.Png(8, 8));
        var doc = Doc.Make([Doc.Page([Image(1, "asset:a", 50, 50, 80, 80), Image(2, "asset:a", 200, 50, 80, 80)])], [Asset("asset:a", "a", "image/png", sha)]);
        var (exp, back) = RT.Trip(Doc.Snapshot(doc), blobs);
        Assert.Equal(1, Fx.Names(exp.Package).Count(n => n.StartsWith("visio/media/")));
        Assert.Single(back.Document.Assets);
        Assert.Equal(2, RT.All(back.Document).Count(e => e["assetId"]?.GetValue<string>() == "asset:a"));
        Assert.Single(back.Blobs);
    }

    // ---------------------------------------------------------------- edits made in Visio

    [Fact]
    public void ReplacingTheImageInVisioAdoptsTheNewBytesAndIgnoresTheStaleRecord()
    {
        var (snap, blobs, sha) = One(Fx.Png(32, 16, 30, 90, 200), "image/png", Image(1, "asset:a", 100, 100, 160, 80));
        var exp = RT.Export(snap, blobs);
        var media = Fx.Names(exp.Package).Single(n => n.StartsWith("visio/media/"));
        var replacement = Fx.Png(32, 16, 200, 30, 30);
        var edited = Fx.Replace(exp.Package, media, replacement);
        var back = RT.Import(edited);
        var blob = Assert.Single(back.Blobs);
        Assert.Equal(replacement, blob.Bytes);
        Assert.NotEqual(sha, blob.Sha256);
        var asset = Assert.Single(back.Document.Assets);
        Assert.Equal(blob.Sha256, asset.Sha256); // the asset is the image that is really there, not what the cells claim
        Assert.NotEqual("asset:a", asset.Id);
        Assert.Contains(back.Diagnostics, d => d.Code == "picture_replaced");
        Assert.Equal(asset.Id, RT.El(back.Document, Doc.Id(1))["assetId"]!.GetValue<string>());
    }

    [Fact]
    public void MovingThePictureInVisioResetsFitToStretch()
    {
        var (snap, blobs, _) = One(Fx.Png(32, 16), "image/png", Image(1, "asset:a", 100, 100, 100, 100, "contain"));
        var exp = RT.Export(snap, blobs);
        var moved = Fx.Edit(exp.Package, "visio/pages/page1.xml", d =>
        {
            var pinx = d.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "PinX");
            pinx.SetAttributeValue("V", (double.Parse((string)pinx.Attribute("V")!, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        });
        var back = RT.Import(moved);
        Assert.Equal("stretch", RT.El(back.Document, Doc.Id(1))["fit"]!.GetValue<string>());
        Assert.Contains(back.Diagnostics, d => d.Code == "picture_moved");
        Near(172, RT.El(back.Document, Doc.Id(1))["bounds"]!["x"]!.GetValue<double>()); // where it is now: 100 + 25 (centred in the frame) + 72
    }

    // ---------------------------------------------------------------- svg and failures

    [Fact]
    public void AnSvgPictureTravelsAsABitmapWithItsSourceAndIsRestoredWhileTheBitmapIsIntact()
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"32\" viewBox=\"0 0 64 32\"><rect width=\"32\" height=\"32\" fill=\"#c8501e\"/></svg>";
        var blobs = new Blobs();
        var svgBytes = Fx.Utf8(svg);
        var sha = blobs.Add(svgBytes);
        blobs.Rasters[sha] = Fx.Png(64, 32, 200, 80, 30);
        var doc = Doc.Make([Doc.Page([Image(1, "asset:logo", 100, 100, 160, 80)])], [Asset("asset:logo", "logo.svg", "image/svg+xml", sha)]);
        var (exp, back) = RT.Trip(Doc.Snapshot(doc), blobs);
        Assert.Equal("Foreign", (string?)Picture(exp.Package).Attribute("Type"));
        Assert.Contains(Fx.Names(exp.Package), n => n.EndsWith(".png"));
        var asset = Assert.Single(back.Document.Assets);
        Assert.Equal("image/svg+xml", asset.MimeType);
        Assert.Equal("asset:logo", asset.Id);
        Assert.Contains(back.Blobs, b => b.MimeType == "image/svg+xml");
        Assert.Single(back.Blobs); // the bitmap stand-in is not kept once the original is back

        // if the bitmap is swapped in Visio, the SVG is no longer what is shown: the new bitmap wins
        var media = Fx.Names(exp.Package).Single(n => n.StartsWith("visio/media/"));
        var swapped = RT.Import(Fx.Replace(exp.Package, media, Fx.Png(64, 32, 1, 2, 3)));
        Assert.Equal("image/png", Assert.Single(swapped.Document.Assets).MimeType);
    }

    [Fact]
    public void AnSvgWithNoBitmapIsLeftOutAndReportedAsALoss()
    {
        var blobs = new Blobs();
        var sha = blobs.Add(Fx.Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>"));
        var doc = Doc.Make([Doc.Page([Image(1, "asset:logo", 100, 100, 160, 80), Doc.Shape(2, 10, 10, 50, 30)])], [Asset("asset:logo", "logo.svg", "image/svg+xml", sha)]);
        var exp = RT.Export(Doc.Snapshot(doc), blobs);
        Assert.Contains(exp.Diagnostics, d => d.Code == "svg_picture_not_exported" && d.Severity == "warning");
        Assert.True(exp.IsLossy);
        Assert.Single(RT.Import(exp.Package).Document.Pages[0].Elements); // only the rectangle survived
    }

    [Fact]
    public void AMissingOrCorruptBlobStopsTheExportInsteadOfWritingABrokenFile()
    {
        var (snap, _, _) = One(Fx.Png(8, 8), "image/png", Image(1, "asset:a", 100, 100, 80, 80));
        var missing = RT.Exporter.ExportAsync(snap, new Blobs(), new ExportOptions(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal("not_found", missing.Error!.Code);
        var blobs = new Blobs();
        var sha = snap.Document.Assets[0].Sha256;
        blobs.Bytes[sha] = Fx.Png(8, 8, 1, 1, 1); // the store holds bytes that do not match the hash
        var corrupt = RT.Exporter.ExportAsync(snap, blobs, new ExportOptions(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal("io_error", corrupt.Error!.Code);
    }

    [Fact]
    public void HostilePicturesInAPackageBecomePlaceholdersNotImages()
    {
        var (snap, blobs, _) = One(Fx.Png(8, 8), "image/png", Image(1, "asset:a", 100, 100, 80, 80));
        var exp = RT.Export(snap, blobs);
        var media = Fx.Names(exp.Package).Single(n => n.StartsWith("visio/media/"));
        // not an image at all
        var junk = RT.Import(Fx.Replace(exp.Package, media, Fx.Utf8("MZ this is an executable, not a picture")));
        Assert.Contains(junk.Diagnostics, d => d.Code == "unsupported_shape");
        Assert.Empty(junk.Blobs);
        Assert.Equal("shape", RT.El(junk.Document, Doc.Id(1))["kind"]!.GetValue<string>());
        Assert.True(((JsonObject)RT.El(junk.Document, Doc.Id(1))["metadata"]!).ContainsKey("visio.unsupported"));
        // a PNG header claiming a huge size is refused before any decoding
        var huge = Fx.Png(8, 8);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(100_000)).CopyTo(huge, 16);
        var tooBig = RT.Import(Fx.Replace(exp.Package, media, huge));
        Assert.Contains(tooBig.Diagnostics, d => d.Code == "unsupported_shape");
        Assert.Empty(tooBig.Blobs);
    }
}
