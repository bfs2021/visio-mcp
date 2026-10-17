using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.Host.Tests;

static class Bytes
{
    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>A PNG with a real signature and IHDR but no pixel data: enough for header-only inspection.</summary>
    public static byte[] PngHeader(int w, int h)
    {
        var ihdr = new List<byte>();
        ihdr.AddRange(Be32(w)); ihdr.AddRange(Be32(h)); ihdr.AddRange([8, 2, 0, 0, 0]);
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        png.AddRange(Be32(13)); png.AddRange("IHDR"u8.ToArray()); png.AddRange(ihdr); png.AddRange(Be32(0));
        png.AddRange(Be32(0)); png.AddRange("IEND"u8.ToArray()); png.AddRange(Be32(0));
        return png.ToArray();
    }

    public static byte[] Jpeg(int w, int h) =>
        [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9];

    public static byte[] Bmp(int w, int h)
    {
        var b = new byte[54];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(w).CopyTo(b, 18);
        BitConverter.GetBytes(h).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);
        return b;
    }

    public static byte[] Svg(string body) => Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"10\" height=\"10\">{body}</svg>");
}

static class Docs
{
    public const string PageId = "00000000-0000-4000-8000-0000000000e1";
    public const string ImageId = "00000000-0000-4000-8000-000000000103";

    public static DiagramDocument Make(IEnumerable<(string id, string mime, byte[] blob)> assets, bool withImage = true, int revision = 0)
    {
        var assetList = assets.ToList();
        var elements = new JsonArray();
        if (withImage && assetList.Count > 0)
            elements.Add(JsonNode.Parse($$"""
                {"id":"{{ImageId}}","kind":"image","layerIds":[],"zIndex":1,"locked":false,"hidden":false,
                 "bounds":{"x":10,"y":10,"width":50,"height":50},"rotationDeg":0,"metadata":{},
                 "assetId":"{{assetList[0].id}}","fit":"contain","opacity":1,"preserveAspectRatio":true}
                """));
        var doc = new JsonObject
        {
            ["schemaVersion"] = 1, ["id"] = "00000000-0000-4000-8000-0000000000d1", ["title"] = "T", ["revision"] = revision, ["metadata"] = new JsonObject(),
            ["assets"] = new JsonArray(assetList.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.id, ["name"] = a.id, ["mimeType"] = a.mime, ["sha256"] = Bytes.Sha(a.blob), ["tags"] = new JsonArray(),
            }).ToArray()),
            ["pages"] = new JsonArray(new JsonObject
            {
                ["id"] = PageId, ["name"] = "Page 1", ["widthPt"] = 841.89, ["heightPt"] = 595.28, ["background"] = new JsonObject { ["kind"] = "none" },
                ["grid"] = new JsonObject { ["visible"] = true, ["spacingPt"] = 14, ["snap"] = true }, ["guides"] = new JsonArray(), ["layers"] = new JsonArray(),
                ["elements"] = elements,
            }),
        };
        return doc.Deserialize<DiagramDocument>(ContractJson.Options)!;
    }
}

/// <summary>A stand-in for the frontend engine with the same scope/revision rules. The host never holds an editable document.</summary>
sealed class FakeEditor : IEditorChannel
{
    public DiagramDocument Document;
    public string SessionId = Guid.NewGuid().ToString();
    public int Revision;
    public int? SavedRevision;
    public string? SavedPath;
    public int ReplaceCalls;
    public Action? AfterSnapshot;
    public Action<DiagramDocument>? BeforeReplace;

    public FakeEditor(DiagramDocument document) { Document = document; Revision = document.Revision; }

    public Scope Scope => new() { DocumentId = Document.Id, SessionId = SessionId };
    public bool Dirty => SavedRevision != Revision;
    public void Edit() => Revision++;
    public void NewSession() => SessionId = Guid.NewGuid().ToString();

    Result<Unit> Check(Scope s) => s.SessionId != SessionId
        ? Result<Unit>.Fail("session_mismatch", "stale session")
        : s.DocumentId != Document.Id ? Result<Unit>.Fail("document_mismatch", "other document") : Result<Unit>.Ok(default);

    public Task<Result<Snapshot>> SnapshotAsync(Scope scope, CancellationToken ct)
    {
        var c = Check(scope);
        if (!c.IsOk) return Task.FromResult(Result<Snapshot>.Fail(c.Error!));
        var snap = new Snapshot { DocumentId = Document.Id, SessionId = SessionId, Revision = Revision, Document = Document with { Revision = Revision } };
        AfterSnapshot?.Invoke(); // the user keeps editing after the barrier
        return Task.FromResult(Result<Snapshot>.Ok(snap));
    }

    public Task<Result<Snapshot>> ReplaceAsync(DiagramDocument document, ExpectedState expected, CancellationToken ct)
    {
        BeforeReplace?.Invoke(document);
        var c = Check(new Scope { DocumentId = expected.DocumentId, SessionId = expected.SessionId });
        if (!c.IsOk) return Task.FromResult(Result<Snapshot>.Fail(c.Error!));
        if (expected.BaseRevision != Revision) return Task.FromResult(Result<Snapshot>.Fail("revision_conflict", "edited meanwhile"));
        ReplaceCalls++;
        Document = document;
        Revision = document.Revision;
        SessionId = Guid.NewGuid().ToString();
        SavedRevision = null;
        return Task.FromResult(Result<Snapshot>.Ok(new Snapshot { DocumentId = Document.Id, SessionId = SessionId, Revision = Revision, Document = Document }));
    }

    public Task<Result<Unit>> MarkSavedAsync(Scope scope, int revision, string path, CancellationToken ct)
    {
        var c = Check(scope);
        if (!c.IsOk) return Task.FromResult(c);
        SavedRevision = revision; SavedPath = path;
        return Task.FromResult(Result<Unit>.Ok(default));
    }
}

sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "diagram-host-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
