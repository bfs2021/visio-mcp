using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

/// <summary>Read-only, version-pinned views of approved reference bytes and committed canvas regions.</summary>
public sealed class ReferenceComparisonService(MainWindow window)
{
    readonly SemaphoreSlim gate = new(1, 1);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public sealed record PixelCrop([property: JsonRequired] int X, [property: JsonRequired] int Y,
        [property: JsonRequired] int Width, [property: JsonRequired] int Height);
    sealed class ImageLimitException(string message) : Exception(message);
    sealed record Options
    {
        public string ReferenceAssetId { get; init; } = "";
        public string ReferenceSha256 { get; init; } = "";
        public string ReferenceScope { get; init; } = "library";
        public PixelCrop? CropPx { get; init; }
        public double Scale { get; init; } = 8;
        public string? PageId { get; init; }
        public JsonObject? Bounds { get; init; }
        public string[]? ElementIds { get; init; }
        public double PaddingPt { get; init; } = 6;
        public int PanelSizePx { get; init; } = 512;
        public string Mode { get; init; } = "side_by_side";
    }

    public async Task<ResponseEnvelope> HandleAsync(RequestEnvelope request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await HandleCoreAsync(request, ct); }
        finally { gate.Release(); }
    }

    async Task<ResponseEnvelope> HandleCoreAsync(RequestEnvelope request, CancellationToken ct)
    {
        ResponseEnvelope Error(string code, string message) => new() { RequestId = request.RequestId, Error = new AppError { Code = code, Message = message, Retryable = false } };
        ResponseEnvelope FromError(AppError error) => new() { RequestId = request.RequestId, Error = error };
        var compare = request.Method == "reference.compare";
        if (!compare && request.Method != "reference.render") return Error("method_not_found", "unknown reference method");
        Options p;
        try { p = request.Params.Deserialize<Options>(Json) ?? throw new JsonException(); }
        catch (JsonException) { return Error("invalid_request", "invalid reference options; source crop coordinates must be integer pixels"); }
        if (string.IsNullOrEmpty(p.ReferenceAssetId) || p.ReferenceAssetId.Length > 256 || p.ReferenceSha256 is null || !Regex.IsMatch(p.ReferenceSha256, "^[0-9a-f]{64}$") || p.ReferenceScope is not ("library" or "document"))
            return Error("invalid_request", "referenceAssetId, a lowercase SHA-256 referenceSha256 and referenceScope library or document are required");
        if (p.CropPx is not { } crop || crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0)
            return Error("invalid_request", "cropPx needs nonnegative x/y and positive width/height in integer source pixels");
        if (!double.IsFinite(p.Scale) || p.Scale is < 1 or > 16) return Error("invalid_request", "scale must be between 1 and 16");
        if (compare && (!Guid.TryParse(p.PageId, out _) || (p.Bounds is null) == (p.ElementIds is null)
            || p.ElementIds is { } ids && (ids.Length is < 1 or > 500 || ids.Any(i => string.IsNullOrEmpty(i) || i.Length > 256))
            || p.PanelSizePx is < 64 or > 2048 || p.Mode is not ("side_by_side" or "overlay") || !double.IsFinite(p.PaddingPt) || p.PaddingPt is < 0 or > 2000))
            return Error("invalid_request", "comparison needs pageId and exactly one of bounds (points) or elementIds; panelSizePx 64–2048, mode side_by_side or overlay, paddingPt 0–2000");

        var scope = window.Router.CurrentScope;
        if (scope is null) return Error("not_ready", "the editor has not signalled ready");
        if (request.DocumentId != scope.DocumentId) return Error("document_mismatch", "the document is not the open one");
        if (request.SessionId != scope.SessionId) return Error("session_mismatch", "the session is not current");
        var snapshot = await window.Router.SnapshotAsync(scope, ct);
        if (!snapshot.IsOk) return FromError(snapshot.Error!);
        var revision = snapshot.Value.Revision;
        if (p.ReferenceScope == "library")
        {
            var asset = await window.Library.GetAsync(p.ReferenceAssetId, ct);
            if (asset is null || asset.Sha256 != p.ReferenceSha256 && !asset.History.Contains(p.ReferenceSha256))
                return Error("not_found", "the library asset has no approved version with that hash; call list_assets");
        }
        else if (!snapshot.Value.Document.Assets.Any(a => a.Id == p.ReferenceAssetId && a.Sha256 == p.ReferenceSha256))
            return Error("not_found", "the document has no asset with that ID and pinned hash; call list_assets");

        try
        {
            var bytes = (await window.Blobs.ReadAsync(p.ReferenceSha256, ct)).ToArray();
            // Resolve dimensions from the pinned bytes, not current library metadata (which may describe a newer version).
            var mime = new[] { "image/png", "image/jpeg", "image/bmp" }.FirstOrDefault(m => RasterHeader.Read(m, bytes) is not null);
            if (mime is null) return Error("invalid_request", "reference crops currently support PNG, JPEG and BMP raster assets; SVG source pixels are not defined");
            var approved = AssetPreparer.ApproveBytes(bytes, mime);
            if (!approved.IsOk) return FromError(approved.Error!);
            var dimensions = RasterHeader.Read(mime, bytes)!.Value;
            if ((long)crop.X + crop.Width > dimensions.Width || (long)crop.Y + crop.Height > dimensions.Height)
                return Error("invalid_request", "cropPx extends outside the source image; crops are not silently clipped");
            if (!compare && (Math.Ceiling(crop.Width * p.Scale) > 4096 || Math.Ceiling(crop.Height * p.Scale) > 4096 || Math.Ceiling(crop.Width * p.Scale) * Math.Ceiling(crop.Height * p.Scale) > 16_000_000))
                return Error("limit_exceeded", "the enlarged crop exceeds 4096 pixels per side or 16 megapixels");

            JsonElement? canvas = null;
            if (compare)
            {
                var render = new JsonObject { ["scope"] = "region", ["pageId"] = p.PageId, ["format"] = "png", ["mode"] = "clean", ["paddingPt"] = p.PaddingPt, ["maxWidth"] = p.PanelSizePx, ["maxHeight"] = p.PanelSizePx };
                if (p.Bounds is not null) render["bounds"] = p.Bounds.DeepClone();
                else render["elementIds"] = JsonSerializer.SerializeToNode(p.ElementIds);
                var result = await window.Router.RenderAsync(scope, render, ct);
                if (!result.IsOk) return FromError(result.Error!);
                canvas = result.Value;
                if (canvas.Value.GetProperty("revision").GetInt32() != revision)
                    return Error("revision_conflict", "the document changed while the reference was being resolved; repeat the read");
            }
            ct.ThrowIfCancellationRequested();
            var body = await window.Dispatcher.InvokeAsync(() => Compose(bytes, p, dimensions, canvas, ct), DispatcherPriority.Background, ct);
            body["documentId"] = scope.DocumentId; body["sessionId"] = scope.SessionId; body["revision"] = revision;
            return new ResponseEnvelope { RequestId = request.RequestId, Result = JsonSerializer.SerializeToElement(body) };
        }
        catch (ImageLimitException e) { return Error("limit_exceeded", e.Message); }
        catch (IOException) { return Error("io_error", "the approved reference image could not be read or decoded"); }
        catch (Exception e) when (e is FileFormatException or NotSupportedException or ArgumentException or FormatException or InvalidOperationException or OverflowException)
        { return Error("invalid_request", "the reference or rendered image could not be decoded within the supported image limits"); }
    }

    static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        bitmap.Freeze(); return bitmap;
    }

    static JsonObject Box(Rect r) => new() { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };
    static Rect Fit(double width, double height, Rect area)
    {
        var scale = Math.Min(area.Width / width, area.Height / height);
        return new Rect(area.X + (area.Width - width * scale) / 2, area.Y + (area.Height - height * scale) / 2, width * scale, height * scale);
    }
    static JsonObject Mapping(double scale, double cropX, double cropY, Rect display) => new()
    { ["scale"] = scale, ["translateX"] = display.X - cropX * scale, ["translateY"] = display.Y - cropY * scale };

    static JsonObject Compose(byte[] bytes, Options p, (int Width, int Height) sourceDimensions, JsonElement? canvas, CancellationToken ct)
    {
        var crop = p.CropPx!;
        var source = Decode(bytes);
        if (source.PixelWidth != sourceDimensions.Width || source.PixelHeight != sourceDimensions.Height) throw new ArgumentException("image dimensions changed on decoding");
        var cropped = new CroppedBitmap(source, new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
        var visual = new DrawingVisual();
        int width, height;
        Rect referenceDisplay;
        JsonObject? canvasMeta = null;
        var notes = new JsonArray("Read-only visual inspection; magnification does not recover missing source detail. No automatic accuracy score is calculated.", "Mappings use xOut = scale*xSource + translateX, yOut = scale*ySource + translateY; all display coordinates are output pixels.");
        if (canvas is null)
        {
            width = (int)Math.Ceiling(crop.Width * p.Scale); height = (int)Math.Ceiling(crop.Height * p.Scale);
            referenceDisplay = new Rect(0, 0, crop.Width * p.Scale, crop.Height * p.Scale);
            DrawNearest(visual, cropped, referenceDisplay, width, height, ct);
        }
        else
        {
            var size = p.PanelSizePx;
            var overlay = p.Mode == "overlay";
            width = overlay ? size + 32 : size * 2 + 48; height = size + 56;
            var refArea = new Rect(16, 40, size, size);
            var canvasArea = overlay ? refArea : new Rect(size + 32, 40, size, size);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
                void Caption(string s, double x) => dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), size < 256 ? 12 : 14, Brushes.Black, 1), new System.Windows.Point(x, 12));
                Caption(overlay ? size < 256 ? "Overlay" : "Overlay: reference + 50% canvas" : size < 256 ? "Reference" : "Reference (source pixels)", 16);
                if (!overlay) Caption(size < 256 ? "Canvas" : "Canvas (document points)", size + 32);
            }
            referenceDisplay = Fit(crop.Width, crop.Height, refArea);
            DrawNearest(visual, cropped, referenceDisplay, width, height, ct);
            var c = canvas.Value;
            var canvasImage = Decode(Convert.FromBase64String(c.GetProperty("base64").GetString()!));
            var b = c.GetProperty("cropPt");
            double x = b.GetProperty("x").GetDouble(), y = b.GetProperty("y").GetDouble(), w = b.GetProperty("width").GetDouble(), h = b.GetProperty("height").GetDouble();
            // The browser fits its SVG into rounded pixel dimensions with xMidYMid meet.
            // Keep that raster's aspect and account for its internal margins in the point transform.
            var rasterDisplay = Fit(canvasImage.PixelWidth, canvasImage.PixelHeight, canvasArea);
            var renderScale = Math.Min(canvasImage.PixelWidth / w, canvasImage.PixelHeight / h);
            var rasterToOutput = rasterDisplay.Width / canvasImage.PixelWidth;
            var canvasDisplay = new Rect(rasterDisplay.X + (canvasImage.PixelWidth - w * renderScale) / 2 * rasterToOutput,
                rasterDisplay.Y + (canvasImage.PixelHeight - h * renderScale) / 2 * rasterToOutput, w * renderScale * rasterToOutput, h * renderScale * rasterToOutput);
            DrawImage(visual, canvasImage, rasterDisplay, BitmapScalingMode.HighQuality, overlay ? 0.5 : 1);
            canvasMeta = new JsonObject
            {
                ["pageId"] = p.PageId, ["revision"] = c.GetProperty("revision").GetInt32(), ["cropPt"] = JsonNode.Parse(b.GetRawText()),
                ["renderWidthPx"] = canvasImage.PixelWidth, ["renderHeightPx"] = canvasImage.PixelHeight,
                ["displayRectPx"] = Box(canvasDisplay), ["rasterDisplayRectPx"] = Box(rasterDisplay),
                ["pointsToOutput"] = Mapping(canvasDisplay.Width / w, x, y, canvasDisplay),
                ["renderPixelsPerPoint"] = renderScale,
            };
            if (Math.Abs((double)crop.Width / crop.Height - w / h) > 0.01) notes.Add("The crop aspect ratios differ: each is centred and fitted without stretching. Refine the crops to align features.");
            if (overlay) notes.Add("Overlay draws the reference first, then the canvas at 50% opacity. Different themes can obscure features; use side_by_side to inspect original colours.");
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        ct.ThrowIfCancellationRequested();
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        if (output.Length > 16 * 1024 * 1024) throw new ImageLimitException("encoded PNG exceeds the 16 MiB output limit");
        ct.ThrowIfCancellationRequested();
        var reference = new JsonObject
        {
            ["assetId"] = p.ReferenceAssetId, ["scope"] = p.ReferenceScope, ["sha256"] = p.ReferenceSha256,
            ["sourceWidthPx"] = sourceDimensions.Width, ["sourceHeightPx"] = sourceDimensions.Height,
            ["cropPx"] = JsonSerializer.SerializeToNode(crop, Json), ["displayRectPx"] = Box(referenceDisplay),
            ["sourceToOutput"] = Mapping(referenceDisplay.Width / crop.Width, crop.X, crop.Y, referenceDisplay), ["interpolation"] = "nearest",
        };
        return new JsonObject
        {
            ["mimeType"] = "image/png", ["base64"] = Convert.ToBase64String(output.ToArray()), ["byteLength"] = output.Length,
            ["widthPx"] = width, ["heightPx"] = height, ["mode"] = canvas is null ? "reference" : p.Mode,
            ["reference"] = reference, ["canvas"] = canvasMeta, ["notes"] = notes,
        };
    }

    static void DrawImage(DrawingVisual parent, ImageSource source, Rect rect, BitmapScalingMode scaling, double opacity = 1)
    {
        var child = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(child, scaling);
        using (var dc = child.RenderOpen()) { dc.PushOpacity(opacity); dc.DrawImage(source, rect); dc.Pop(); }
        parent.Children.Add(child);
    }

    /// <summary>Sample source pixels explicitly: WPF's drawing sampler can blend CroppedBitmap pixels even with NearestNeighbor set.</summary>
    static void DrawNearest(DrawingVisual parent, BitmapSource source, Rect display, int outputWidth, int outputHeight, CancellationToken ct)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var output = new byte[checked(outputWidth * outputHeight * 4)];
        var row = new byte[checked(source.PixelWidth * 4)];
        int lastY = -1;
        var scale = display.Width / source.PixelWidth;
        for (var y = Math.Max(0, (int)Math.Floor(display.Y)); y < Math.Min(outputHeight, (int)Math.Ceiling(display.Bottom)); y++)
        {
            ct.ThrowIfCancellationRequested();
            var sy = (int)Math.Floor((y + 0.5 - display.Y) / scale);
            if (sy < 0 || sy >= source.PixelHeight) continue;
            if (sy != lastY) { converted.CopyPixels(new Int32Rect(0, sy, source.PixelWidth, 1), row, row.Length, 0); lastY = sy; }
            for (var x = Math.Max(0, (int)Math.Floor(display.X)); x < Math.Min(outputWidth, (int)Math.Ceiling(display.Right)); x++)
            {
                var sx = (int)Math.Floor((x + 0.5 - display.X) / scale);
                if (sx < 0 || sx >= source.PixelWidth) continue;
                Buffer.BlockCopy(row, sx * 4, output, (y * outputWidth + x) * 4, 4);
            }
        }
        var bitmap = BitmapSource.Create(outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32, null, output, outputWidth * 4);
        DrawImage(parent, bitmap, new Rect(0, 0, outputWidth, outputHeight), BitmapScalingMode.NearestNeighbor);
    }
}
