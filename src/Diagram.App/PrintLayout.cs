using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Xml.Linq;

namespace Diagram.App;

public sealed class PrintLayoutException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PrintPageRange
{
    public static IReadOnlyList<int> Parse(string range, int pageCount)
    {
        if (pageCount <= 0)
            throw new PrintLayoutException("invalid_page_count", "The drawing has no printable pages.");
        if (pageCount > 32)
            throw new PrintLayoutException("limit_exceeded", "Print at most 32 drawing pages at a time.");
        if (string.IsNullOrWhiteSpace(range))
            throw new PrintLayoutException("invalid_range", "Enter one or more page numbers, such as 1,3-5.");

        var selected = new bool[pageCount + 1];
        foreach (var rawPart in range.Split(',', StringSplitOptions.None))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                throw new PrintLayoutException("invalid_range", "The page range contains an empty item.");

            var hyphen = part.IndexOf('-');
            int first, last;
            if (hyphen < 0)
            {
                if (!TryPageNumber(part, out first))
                    throw new PrintLayoutException("invalid_range", $"'{part}' is not a valid page number.");
                last = first;
            }
            else
            {
                if (hyphen != part.LastIndexOf('-') ||
                    !TryPageNumber(part[..hyphen].Trim(), out first) ||
                    !TryPageNumber(part[(hyphen + 1)..].Trim(), out last))
                    throw new PrintLayoutException("invalid_range", $"'{part}' is not a valid page range.");
                if (first > last)
                    throw new PrintLayoutException("reversed_range", $"Page range '{part}' ends before it starts.");
            }

            if (first < 1 || last > pageCount)
                throw new PrintLayoutException("page_out_of_range", $"Page range '{part}' must be between 1 and {pageCount}.");

            for (var page = first; page <= last; page++) selected[page] = true;
        }

        var result = new List<int>();
        for (var page = 1; page <= pageCount; page++) if (selected[page]) result.Add(page);
        return result.AsReadOnly();
    }

    static bool TryPageNumber(string text, out int page) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page);
}

public sealed record PrintMargins(double LeftPt, double TopPt, double RightPt, double BottomPt);

public sealed record PrintSourcePage(string PageId, int PageNumber, double WidthPt, double HeightPt, string Svg);

public sealed record PrintLayoutOptions(
    double PaperWidthPt,
    double PaperHeightPt,
    string Orientation,
    string Mode,
    PrintMargins RequestedMargins,
    PrintMargins HardwareMinimumMargins);

public sealed record PrintSheet(
    int SheetNumber,
    double PaperWidthPt,
    double PaperHeightPt,
    IReadOnlyList<int> SourcePageNumbers,
    string Svg);

public static class PrintLayoutPlanner
{
    const int MaximumSheets = 32;
    static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";

    sealed record PlannedPage(PrintSourcePage Page, int Columns, int Rows);

    public static IReadOnlyList<PrintSheet> Build(IReadOnlyList<PrintSourcePage> pages, PrintLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        if (pages.Count == 0) Invalid("The page range selected no drawing pages.");
        if (pages.Count > MaximumSheets) Limit("Print at most 32 drawing pages at a time.");
        if (options.RequestedMargins is null || options.HardwareMinimumMargins is null) Invalid("Print margins are missing.");
        if (options.Orientation is not ("portrait" or "landscape"))
            throw new PrintLayoutException("unsupported_setting", "Choose portrait or landscape orientation.");
        if (options.Mode is not ("fit" or "tile"))
            throw new PrintLayoutException("unsupported_setting", "Choose Fit or Tile layout.");
        if (!PositiveFinite(options.PaperWidthPt) || !PositiveFinite(options.PaperHeightPt)) Invalid("Paper dimensions must be positive finite values.");

        var paperWidth = options.PaperWidthPt;
        var paperHeight = options.PaperHeightPt;
        if (options.Orientation == "landscape" && paperWidth < paperHeight || options.Orientation == "portrait" && paperWidth > paperHeight)
            (paperWidth, paperHeight) = (paperHeight, paperWidth);

        ValidateMargins(options.RequestedMargins);
        ValidateMargins(options.HardwareMinimumMargins);
        var margins = new PrintMargins(
            Math.Max(options.RequestedMargins.LeftPt, options.HardwareMinimumMargins.LeftPt),
            Math.Max(options.RequestedMargins.TopPt, options.HardwareMinimumMargins.TopPt),
            Math.Max(options.RequestedMargins.RightPt, options.HardwareMinimumMargins.RightPt),
            Math.Max(options.RequestedMargins.BottomPt, options.HardwareMinimumMargins.BottomPt));
        var printableWidth = paperWidth - margins.LeftPt - margins.RightPt;
        var printableHeight = paperHeight - margins.TopPt - margins.BottomPt;
        if (!PositiveFinite(printableWidth) || !PositiveFinite(printableHeight)) Invalid("Margins leave no printable area on the selected paper.");

        var pageNumbers = new HashSet<int>();
        var planned = new List<PlannedPage>(pages.Count);
        long totalSheets = 0;
        foreach (var page in pages)
        {
            if (page is null || string.IsNullOrWhiteSpace(page.PageId) || string.IsNullOrWhiteSpace(page.Svg) ||
                page.PageNumber < 1 || !pageNumbers.Add(page.PageNumber) ||
                !PositiveFinite(page.WidthPt) || !PositiveFinite(page.HeightPt))
                Invalid("A source page has invalid identity, dimensions, or SVG content.");

            var columns = options.Mode == "tile" ? TileCount(page.WidthPt, printableWidth) : 1;
            var rows = options.Mode == "tile" ? TileCount(page.HeightPt, printableHeight) : 1;
            var count = (long)columns * rows;
            totalSheets += count;
            if (totalSheets > MaximumSheets) Limit("The selected layout exceeds the 32-sheet print limit.");
            planned.Add(new PlannedPage(page, columns, rows));
        }

        var result = new List<PrintSheet>((int)totalSheets);
        foreach (var plan in planned)
        {
            if (options.Mode == "fit")
            {
                var scale = Math.Min(printableWidth / plan.Page.WidthPt, printableHeight / plan.Page.HeightPt);
                var width = plan.Page.WidthPt * scale;
                var height = plan.Page.HeightPt * scale;
                var x = margins.LeftPt + (printableWidth - width) / 2;
                var y = margins.TopPt + (printableHeight - height) / 2;
                result.Add(Compose(result.Count + 1, paperWidth, paperHeight, margins, printableWidth, printableHeight, plan.Page, x, y, width, height));
                continue;
            }

            for (var row = 0; row < plan.Rows; row++)
            for (var column = 0; column < plan.Columns; column++)
            {
                var x = margins.LeftPt - column * printableWidth;
                var y = margins.TopPt - row * printableHeight;
                result.Add(Compose(result.Count + 1, paperWidth, paperHeight, margins, printableWidth, printableHeight, plan.Page, x, y, plan.Page.WidthPt, plan.Page.HeightPt));
            }
        }
        return result.AsReadOnly();
    }

    static int TileCount(double extent, double printableExtent)
    {
        var tiles = Math.Ceiling(extent / printableExtent);
        if (!double.IsFinite(tiles) || tiles < 1 || tiles > MaximumSheets) Limit("The selected layout exceeds the 32-sheet print limit.");
        return (int)tiles;
    }

    static PrintSheet Compose(int sheetNumber, double paperWidth, double paperHeight, PrintMargins margins, double printableWidth, double printableHeight,
        PrintSourcePage page, double x, double y, double width, double height)
    {
        var source = Convert.ToBase64String(Encoding.UTF8.GetBytes(page.Svg));
        var svg = new XElement(SvgNamespace + "svg",
            new XAttribute("width", Format(paperWidth) + "pt"),
            new XAttribute("height", Format(paperHeight) + "pt"),
            new XAttribute("viewBox", $"0 0 {Format(paperWidth)} {Format(paperHeight)}"),
            new XElement(SvgNamespace + "defs",
                new XElement(SvgNamespace + "clipPath",
                    new XAttribute("id", "printable-area"),
                    new XAttribute("clipPathUnits", "userSpaceOnUse"),
                    new XElement(SvgNamespace + "rect",
                        new XAttribute("x", Format(margins.LeftPt)),
                        new XAttribute("y", Format(margins.TopPt)),
                        new XAttribute("width", Format(printableWidth)),
                        new XAttribute("height", Format(printableHeight))))),
            new XElement(SvgNamespace + "image",
                new XAttribute("x", Format(x)),
                new XAttribute("y", Format(y)),
                new XAttribute("width", Format(width)),
                new XAttribute("height", Format(height)),
                new XAttribute("preserveAspectRatio", "none"),
                new XAttribute("clip-path", "url(#printable-area)"),
                new XAttribute("href", "data:image/svg+xml;base64," + source)));
        return new PrintSheet(sheetNumber, paperWidth, paperHeight, Array.AsReadOnly([page.PageNumber]), svg.ToString(SaveOptions.DisableFormatting));
    }

    static string Format(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
    static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;
    static void ValidateMargins(PrintMargins value)
    {
        if (new[] { value.LeftPt, value.TopPt, value.RightPt, value.BottomPt }.Any(x => !double.IsFinite(x) || x < 0))
            Invalid("Print margins must be finite non-negative values.");
    }
    [DoesNotReturn]
    static void Invalid(string message) => throw new PrintLayoutException("invalid_geometry", message);
    [DoesNotReturn]
    static void Limit(string message) => throw new PrintLayoutException("limit_exceeded", message);
}
