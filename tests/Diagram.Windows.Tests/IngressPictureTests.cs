using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.App;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Xunit;

namespace Diagram.Windows.Tests;

public sealed class IngressPictureTests
{
    static byte[] Png(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    static byte[] Jpeg(int width, int height) => [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0];

    static byte[] Bmp(int width, int height)
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        return bytes;
    }

    static async Task<JsonElement> Import(Harness h, string path, string requestId)
    {
        var scope = h.Router.CurrentScope!;
        var ticket = Assert.Single(h.Window.Services.IngressTickets.MintDropBatch(h.Window.Services.IngressWindowId, BridgeRouter.User, [new IngressFile(path)]).Items);
        var response = await h.Window.Services.HandleAsync(new RequestEnvelope
        {
            RequestId = requestId, Method = IngressHostMethods.ImportImage, DocumentId = scope.DocumentId, SessionId = scope.SessionId,
            Params = new JsonObject { ["ticketId"] = ticket.TicketId, ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId },
        }, Harness.Ct());
        Assert.Null(response.Error);
        return response.Result!.Value;
    }

    [Fact]
    public async Task DroppedRasterPicturesAreValidatedAndDeduplicatedIntoTheLibraryWithoutEditingTheDrawing()
    {
        await using var h = await Harness.StartAsync();
        var cases = new (string Name, byte[] Bytes, int Width, int Height)[]
        {
            ("pixel.png", Png(40, 20), 40, 20),
            ("photo.jpg", Jpeg(30, 10), 30, 10),
            ("chart.bmp", Bmp(16, 12), 16, 12),
        };
        var scope = h.Router.CurrentScope!;
        var before = (await h.Send("doc.status", scope: scope)).Result!.Value.GetProperty("revision").GetInt32();
        foreach (var (name, bytes, width, height) in cases)
        {
            var path = Path.Combine(h.Root, name);
            await File.WriteAllBytesAsync(path, bytes);
            var imported = await Import(h, path, "image-" + name);
            Assert.Equal(name, imported.GetProperty("asset").GetProperty("name").GetString());
            Assert.Equal(width, imported.GetProperty("width").GetInt32());
            Assert.Equal(height, imported.GetProperty("height").GetInt32());
            Assert.True(imported.GetProperty("created").GetBoolean());
        }
        var duplicatePath = Path.Combine(h.Root, cases[0].Name);
        var duplicate = await Import(h, duplicatePath, "image-duplicate");
        Assert.False(duplicate.GetProperty("created").GetBoolean());
        Assert.Equal(3, (await h.Window.Library.ListAsync(null, null, Harness.Ct())).Count);
        Assert.Equal(before, (await h.Send("doc.status", scope: scope)).Result!.Value.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task DroppedSvgIsSanitizedAndMismatchedImageContentIsRejected()
    {
        await using var h = await Harness.StartAsync();
        var svg = Path.Combine(h.Root, "safe.svg");
        await File.WriteAllTextAsync(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><script>alert(1)</script><rect width=\"4\" height=\"4\" onclick=\"alert(2)\"/></svg>", Encoding.UTF8);
        var imported = await Import(h, svg, "image-svg");
        var asset = imported.GetProperty("asset");
        Assert.True(imported.GetProperty("notes").GetArrayLength() > 0);
        var stored = await File.ReadAllTextAsync(Path.Combine(h.Window.Options.BlobDir, asset.GetProperty("sha256").GetString()!));
        Assert.DoesNotContain("<script", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", stored, StringComparison.OrdinalIgnoreCase);

        var mismatch = Path.Combine(h.Root, "wrong.png");
        await File.WriteAllBytesAsync(mismatch, Jpeg(10, 10));
        var scope = h.Router.CurrentScope!;
        var ticket = Assert.Single(h.Window.Services.IngressTickets.MintDropBatch(h.Window.Services.IngressWindowId, BridgeRouter.User, [new IngressFile(mismatch)]).Items);
        var rejected = await h.Window.Services.HandleAsync(new RequestEnvelope
        {
            RequestId = "image-mismatch", Method = IngressHostMethods.ImportImage, DocumentId = scope.DocumentId, SessionId = scope.SessionId,
            Params = new JsonObject { ["ticketId"] = ticket.TicketId, ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId },
        }, Harness.Ct());
        Assert.Equal("invalid_request", rejected.Error?.Code);
        Assert.Equal("invalid_request", h.Window.Services.IngressTickets.Consume(h.Window.Services.IngressWindowId, BridgeRouter.User, ticket.TicketId).Error?.Code);
        Assert.Single(await h.Window.Library.ListAsync(null, null, Harness.Ct()));

        var tooLarge = Path.Combine(h.Root, "too-large.png");
        var oversized = new byte[checked((int)InputLimits.RasterBytes + 1)];
        Png(1, 1).CopyTo(oversized, 0);
        await File.WriteAllBytesAsync(tooLarge, oversized);
        var largeTicket = Assert.Single(h.Window.Services.IngressTickets.MintDropBatch(h.Window.Services.IngressWindowId, BridgeRouter.User, [new IngressFile(tooLarge)]).Items);
        var limited = await h.Window.Services.HandleAsync(new RequestEnvelope
        {
            RequestId = "image-too-large", Method = IngressHostMethods.ImportImage, DocumentId = scope.DocumentId, SessionId = scope.SessionId,
            Params = new JsonObject { ["ticketId"] = largeTicket.TicketId, ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId },
        }, Harness.Ct());
        Assert.Equal("limit_exceeded", limited.Error?.Code);
        Assert.Single(await h.Window.Library.ListAsync(null, null, Harness.Ct()));
    }

    [Fact]
    public async Task ImageTicketImportRequiresTheVisibleEditorAndAnImageTicket()
    {
        await using var h = await Harness.StartAsync();
        var path = Path.Combine(h.Root, "not-image.diagram.json");
        await File.WriteAllTextAsync(path, "{}");
        var scope = h.Router.CurrentScope!;
        var ticket = Assert.Single(h.Window.Services.IngressTickets.MintDropBatch(h.Window.Services.IngressWindowId, BridgeRouter.User, [new IngressFile(path)]).Items);
        var wrongKind = await h.Window.Services.HandleAsync(new RequestEnvelope
        {
            RequestId = "image-wrong-kind", Method = IngressHostMethods.ImportImage, DocumentId = scope.DocumentId, SessionId = scope.SessionId,
            Params = new JsonObject { ["ticketId"] = ticket.TicketId, ["documentId"] = scope.DocumentId, ["sessionId"] = scope.SessionId },
        }, Harness.Ct());
        Assert.Equal("invalid_request", wrongKind.Error?.Code);
        Assert.Empty(await h.Window.Library.ListAsync(null, null, Harness.Ct()));

        var image = Path.Combine(h.Root, "scoped.png");
        await File.WriteAllBytesAsync(image, Png(1, 1));
        var imageTicket = Assert.Single(h.Window.Services.IngressTickets.MintDropBatch(h.Window.Services.IngressWindowId, BridgeRouter.User, [new IngressFile(image)]).Items);
        var wrongScope = await h.Window.Services.HandleAsync(new RequestEnvelope
        {
            RequestId = "image-wrong-scope", Method = IngressHostMethods.ImportImage, DocumentId = "agent-document", SessionId = scope.SessionId,
            Params = new JsonObject { ["ticketId"] = imageTicket.TicketId, ["documentId"] = "agent-document", ["sessionId"] = scope.SessionId },
        }, Harness.Ct());
        Assert.Equal("document_mismatch", wrongScope.Error?.Code);
        Assert.Single(h.Window.Services.IngressTickets.DescribePending(h.Window.Services.IngressWindowId, BridgeRouter.User));
    }

    [Fact]
    public async Task NativeDropCaptureReturnsTheVisiblePageAndUsesItsCentreOutsideTheCanvas()
    {
        await using var h = await Harness.StartAsync();
        var scope = h.Router.CurrentScope!;
        var captured = await h.Router.SendAsync(new RequestEnvelope
        {
            RequestId = "capture-native-drop", Method = "app.captureIngressTarget", DocumentId = scope.DocumentId, SessionId = scope.SessionId,
            Params = new JsonObject { ["clientX"] = 10, ["clientY"] = 10 },
        }, Harness.Ct(), BridgeRouter.User);
        Assert.Null(captured.Error);

        var page = (await h.Send("doc.snapshot", scope: scope)).Result!.Value.GetProperty("document").GetProperty("pages")[0];
        var target = captured.Result!.Value;
        Assert.Equal(scope.DocumentId, target.GetProperty("documentId").GetString());
        Assert.Equal(scope.SessionId, target.GetProperty("sessionId").GetString());
        Assert.Equal(page.GetProperty("id").GetString(), target.GetProperty("pageId").GetString());
        Assert.Equal(0, target.GetProperty("baseRevision").GetInt32());
        Assert.Equal(page.GetProperty("widthPt").GetDouble() / 2, target.GetProperty("x").GetDouble(), 1);
        Assert.Equal(page.GetProperty("heightPt").GetDouble() / 2, target.GetProperty("y").GetDouble(), 1);
    }
}
