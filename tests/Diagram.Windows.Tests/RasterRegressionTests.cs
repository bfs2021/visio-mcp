using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>Same-renderer image regression: fixed dimensions, RGB tolerance 24, symmetric 1px neighbourhood.
/// Global (0.5%), worst 32px tile (20%) and foreground coverage (35%) must pass. Never rebaselines.</summary>
internal static class RasterRegression
{
    internal sealed record Result(bool Pass, int Width, int Height, double MismatchFraction, double WorstTileFraction, double WorstForegroundTileFraction, string Policy);
    internal static BitmapSource Decode(byte[] bytes)
    {
        using var s = new MemoryStream(bytes);
        return BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }
    internal static Result Compare(byte[] expected, byte[] actual) => Compare(Decode(expected), Decode(actual));
    internal static Result Compare(BitmapSource expected, BitmapSource actual)
    {
        const string policy = "same dimensions; RGB tolerance 24; symmetric 1px neighbourhood; global <=0.005, worst 32px tile <=0.20, foreground tile <=0.35 (at least 8 ink pixels; ink differs from white by >24)";
        int w = expected.PixelWidth, h = expected.PixelHeight;
        if (w != actual.PixelWidth || h != actual.PixelHeight) return new(false, w, h, 1, 1, 1, policy);
        byte[] Pixels(BitmapSource image) { var b = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var data = new byte[w * h * 4]; b.CopyPixels(data, w * 4, 0); return data; }
        var a = Pixels(expected); var b = Pixels(actual); int tilesWide = (w + 31) / 32;
        var tiles = new int[tilesWide * ((h + 31) / 32)]; var inkTiles = new int[tiles.Length]; var badInkTiles = new int[tiles.Length]; int bad = 0;
        int Channel(byte[] data, int p, int c) => (data[p + c] * data[p + 3] + 255 * (255 - data[p + 3]) + 127) / 255;
        bool Ink(byte[] data, int p) => (255 - Channel(data, p, 0) > 24 || 255 - Channel(data, p, 1) > 24 || 255 - Channel(data, p, 2) > 24);
        bool Close(byte[] source, int p, byte[] target, int q) { if (Ink(source, p) != Ink(target, q)) return false; for (int c = 0; c < 3; c++) if (Math.Abs(Channel(source, p, c) - Channel(target, q, c)) > 24) return false; return true; }
        bool Match(byte[] source, byte[] target, int x, int y)
        {
            int p = (y * w + x) * 4;
            if (Close(source, p, target, p)) return true;
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++) if (Close(source, p, target, (yy * w + xx) * 4)) return true;
            return false;
        }
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int tile = (y / 32) * tilesWide + x / 32, p = (y * w + x) * 4;
            bool ink = Ink(a, p) || Ink(b, p); if (ink) inkTiles[tile]++;
            if (!Match(a, b, x, y) || !Match(b, a, x, y)) { bad++; tiles[tile]++; if (ink) badInkTiles[tile]++; }
        }
        double worst = 0, worstInk = 0;
        for (int i = 0; i < tiles.Length; i++) worst = Math.Max(worst, (double)tiles[i] / (Math.Min(32, w - (i % tilesWide) * 32) * Math.Min(32, h - (i / tilesWide) * 32)));
        for (int i = 0; i < tiles.Length; i++) if (inkTiles[i] >= 8) worstInk = Math.Max(worstInk, (double)badInkTiles[i] / inkTiles[i]);
        var fraction = (double)bad / (w * h);
        return new(fraction <= 0.005 && worst <= 0.20 && worstInk <= 0.35, w, h, fraction, worst, worstInk, policy);
    }
}

public class RasterRegressionTests
{
    [Fact] public void RejectsDeletionOfASinglePixelPaleDividerAtFixtureResolution()
    {
        var pixels = Enumerable.Repeat((byte)255, 1600 * 1246 * 4).ToArray();
        var blank = BitmapSource.Create(1600, 1246, 96, 96, PixelFormats.Bgra32, null, pixels, 6400);
        for (int x = 94; x < 766; x++) { int p = (190 * 1600 + x) * 4; pixels[p] = 240; pixels[p + 1] = 232; pixels[p + 2] = 226; }
        var divider = BitmapSource.Create(1600, 1246, 96, 96, PixelFormats.Bgra32, null, pixels, 6400);
        Assert.False(RasterRegression.Compare(divider, blank).Pass);
    }
    static BitmapSource Picture(int shift = 0, int colour = 0, bool remove = false, int width = 128)
    {
        var data = Enumerable.Repeat((byte)255, width * 128 * 4).ToArray();
        if (!remove) for (int y = 40; y < 88; y++) for (int x = 40 + shift; x < 88 + shift; x++) for (int c = 0; c < 3; c++) data[(y * width + x) * 4 + c] = (byte)colour;
        return BitmapSource.Create(width, 128, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
    }
    [Fact] public void AllowsMinorRasterNoiseAndOnePixelRoundingButRejectsMissingOrMovedContent()
    {
        Assert.True(RasterRegression.Compare(Picture(), Picture(colour: 12)).Pass);
        Assert.True(RasterRegression.Compare(Picture(), Picture(shift: 1)).Pass);
        Assert.False(RasterRegression.Compare(Picture(), Picture(shift: 8)).Pass);
        Assert.False(RasterRegression.Compare(Picture(), Picture(remove: true)).Pass);
        Assert.False(RasterRegression.Compare(Picture(), Picture(width: 129)).Pass);
    }
}
