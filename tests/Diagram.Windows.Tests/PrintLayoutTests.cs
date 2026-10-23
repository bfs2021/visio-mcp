using Diagram.App;
using System.Xml.Linq;
using Xunit;

namespace Diagram.Windows.Tests;

public class PrintLayoutTests
{
    static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    static PrintMargins NoMargins => new(0, 0, 0, 0);
    static PrintSourcePage Page(int number, double width, double height) => new($"page-{number}", number, width, height, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><text>page</text></svg>");
    static PrintLayoutOptions Options(double paperWidth = 300, double paperHeight = 400, string orientation = "portrait", string mode = "fit", PrintMargins? requested = null, PrintMargins? minimum = null) => new(paperWidth, paperHeight, orientation, mode, requested ?? NoMargins, minimum ?? NoMargins);
    static XElement PaperImage(PrintSheet sheet) => XDocument.Parse(sheet.Svg).Root!.Element(Svg + "image")!;

    static IReadOnlyList<int> Parse(string range, int pageCount)
        => PrintPageRange.Parse(range, pageCount);

    static string ParseErrorCode(string range, int pageCount)
        => Assert.Throws<PrintLayoutException>(() => PrintPageRange.Parse(range, pageCount)).Code;

    [Fact]
    public void ParsesOneBasedPagesAndInclusiveRangesInDocumentOrder()
    {
        var pages = Parse("1, 3-5", 5);
        Assert.Equal(new[] { 1, 3, 4, 5 }, pages);
    }

    [Fact]
    public void RemovesDuplicatesAndOrdersOverlappingRangesByDocumentPage()
    {
        var pages = Parse("5, 2-4, 3,1", 5);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, pages);
    }

    [Theory]
    [InlineData("", "invalid_range")]
    [InlineData("  ", "invalid_range")]
    [InlineData("1,,2", "invalid_range")]
    [InlineData(",1", "invalid_range")]
    [InlineData("1,", "invalid_range")]
    [InlineData("abc", "invalid_range")]
    [InlineData("1--2", "invalid_range")]
    [InlineData("1-2-3", "invalid_range")]
    [InlineData("2-x", "invalid_range")]
    [InlineData("5-3", "reversed_range")]
    [InlineData("0", "page_out_of_range")]
    [InlineData("6", "page_out_of_range")]
    [InlineData("2-6", "page_out_of_range")]
    public void RejectsInvalidRangesWithStableErrorCodes(string range, string expectedCode)
    {
        Assert.Equal(expectedCode, ParseErrorCode(range, 5));
    }

    [Fact]
    public void RejectsDocumentsWithoutPrintablePages()
    {
        Assert.Equal("invalid_page_count", ParseErrorCode("1", 0));
    }

    [Fact]
    public void RejectsPageCountsAboveTheExistingExportLimit()
    {
        Assert.Equal("limit_exceeded", ParseErrorCode("1", 33));
    }

    [Fact]
    public void FitCentersWithoutDistortingAndUsesEffectivePerEdgeMargins()
    {
        var sheets = PrintLayoutPlanner.Build([Page(1, 100, 100)], Options(
            requested: new PrintMargins(10, 10, 10, 10),
            minimum: new PrintMargins(15, 20, 25, 30)));
        var sheet = Assert.Single(sheets);
        var image = PaperImage(sheet);
        Assert.Equal(300, sheet.PaperWidthPt);
        Assert.Equal(400, sheet.PaperHeightPt);
        Assert.Equal("1", Assert.Single(sheet.SourcePageNumbers).ToString());
        Assert.Equal(260, double.Parse(image.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(260, double.Parse(image.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(15, double.Parse(image.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(65, double.Parse(image.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        var source = image.Attribute("href")!.Value;
        Assert.StartsWith("data:image/svg+xml;base64,", source);
        Assert.Contains("<text>page</text>", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(source[(source.IndexOf(',') + 1)..])));
    }

    [Fact]
    public void LandscapeSwapsMediaDimensionsAndFitPreservesAspectRatio()
    {
        var sheet = Assert.Single(PrintLayoutPlanner.Build([Page(1, 600, 200)], Options(300, 500, "landscape")));
        var image = PaperImage(sheet);
        Assert.Equal(500, sheet.PaperWidthPt);
        Assert.Equal(300, sheet.PaperHeightPt);
        Assert.Equal(500, double.Parse(image.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(166.66666667, double.Parse(image.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(0, double.Parse(image.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(66.66666667, double.Parse(image.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture), 6);
    }

    [Fact]
    public void TileUsesNativePageScaleAndEnumeratesSheetsInRowMajorOrder()
    {
        var sheets = PrintLayoutPlanner.Build([Page(7, 361, 261)], Options(200, 150, "landscape", "tile", new PrintMargins(10, 10, 10, 10)));
        Assert.Equal(9, sheets.Count);
        Assert.Equal(Enumerable.Range(1, 9), sheets.Select(x => x.SheetNumber));
        Assert.All(sheets, x => Assert.Equal(new[] { 7 }, x.SourcePageNumbers));
        var first = PaperImage(sheets[0]);
        var second = PaperImage(sheets[1]);
        var fourth = PaperImage(sheets[3]);
        Assert.Equal(10, double.Parse(first.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(10, double.Parse(first.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(-170, double.Parse(second.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(10, double.Parse(second.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(10, double.Parse(fourth.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(-120, double.Parse(fourth.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OnePointOverflowCreatesAnAdditionalTile()
    {
        var sheets = PrintLayoutPlanner.Build([Page(1, 101, 100)], Options(100, 100, mode: "tile"));
        Assert.Equal(2, sheets.Count);
        Assert.All(sheets, x => Assert.Equal(new[] { 1 }, x.SourcePageNumbers));
    }

    [Fact]
    public void MixedSourcePagesCreateOneFitSheetPerSelectedPageInInputOrder()
    {
        var sheets = PrintLayoutPlanner.Build([Page(4, 600, 200), Page(2, 100, 400)], Options());
        Assert.Equal(new[] { 1, 2 }, sheets.Select(x => x.SheetNumber));
        Assert.Equal(new[] { new[] { 4 }, new[] { 2 } }, sheets.Select(x => x.SourcePageNumbers));
        Assert.All(sheets, x => { Assert.Equal(300, x.PaperWidthPt); Assert.Equal(400, x.PaperHeightPt); });
    }

    [Fact]
    public void RejectsTileExpansionAboveSheetLimitBeforeReturningAnySheets()
    {
        var error = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 800, 800)], Options(100, 100, mode: "tile")));
        Assert.Equal("limit_exceeded", error.Code);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(double.NaN, 100)]
    [InlineData(double.PositiveInfinity, 100)]
    public void RejectsInvalidSourceDimensions(double width, double height)
    {
        var error = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, width, height)], Options()));
        Assert.Equal("invalid_geometry", error.Code);
    }

    [Fact]
    public void RejectsZeroPrintableAreaAndNegativeMargins()
    {
        var area = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 10, 10)], Options(20, 20, requested: new PrintMargins(10, 0, 10, 0))));
        Assert.Equal("invalid_geometry", area.Code);
        var margin = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 10, 10)], Options(requested: new PrintMargins(-1, 0, 0, 0))));
        Assert.Equal("invalid_geometry", margin.Code);
    }

    [Fact]
    public void RejectsInvalidMediaMarginsOrientationAndLayout()
    {
        var media = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 10, 10)], Options(double.NaN, 200)));
        Assert.Equal("invalid_geometry", media.Code);
        var margins = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 10, 10)], Options(requested: new PrintMargins(0, 0, double.PositiveInfinity, 0))));
        Assert.Equal("invalid_geometry", margins.Code);
        var orientation = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 10, 10)], Options(orientation: "diagonal")));
        Assert.Equal("unsupported_setting", orientation.Code);
        var layout = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 10, 10)], Options(mode: "stretch")));
        Assert.Equal("unsupported_setting", layout.Code);
    }

    [Fact]
    public void RejectsDuplicatePageNumbersAndSourcePageLimitOverflow()
    {
        var duplicate = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build([Page(1, 10, 10), Page(1, 20, 20)], Options()));
        Assert.Equal("invalid_geometry", duplicate.Code);
        var tooMany = Enumerable.Range(1, 33).Select(i => Page(i, 10, 10)).ToArray();
        var limit = Assert.Throws<PrintLayoutException>(() => PrintLayoutPlanner.Build(tooMany, Options()));
        Assert.Equal("limit_exceeded", limit.Code);
    }
}
