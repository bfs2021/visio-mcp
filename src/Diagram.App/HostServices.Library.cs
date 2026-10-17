using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Microsoft.Win32;

namespace Diagram.App;

/// <summary>The picture library as the page uses it: list, add from a file, use in a document, replace a source, rename, tag, remove.</summary>
public sealed partial class HostServices
{
    static JsonObject Entry(LibraryAsset a) => new()
    {
        ["id"] = a.Id, ["name"] = a.Name, ["tags"] = new JsonArray(a.Tags.Select(t => (JsonNode)t).ToArray()), ["mimeType"] = a.MimeType, ["sha256"] = a.Sha256,
        ["width"] = a.Width, ["height"] = a.Height, ["version"] = a.Version, ["scope"] = "library", ["updatedAt"] = a.UpdatedAt.ToString("O"),
    };

    static string[]? Strings(JsonNode? n) => n is JsonArray arr ? arr.Select(x => x?.GetValue<string>() ?? "").ToArray() : null;

    async Task<ResponseEnvelope> LibraryAsync(RequestEnvelope request, CancellationToken ct)
    {
        var lib = window.Library;
        var p = request.Params;
        switch (request.Method)
        {
            case "host.library.list":
            {
                var list = await lib.ListAsync(p["query"]?.GetValue<string>(), Strings(p["tags"]), ct);
                return Ok(request, new JsonObject { ["assets"] = new JsonArray(list.Select(a => (JsonNode)Entry(a)).ToArray()) });
            }
            case "host.library.import":
            {
                var path = Ask("libraryImport", () =>
                {
                    var d = new OpenFileDialog { Title = "Add to picture library", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.svg)|*.png;*.jpg;*.jpeg;*.bmp;*.svg" };
                    return d.ShowDialog(window) == true ? d.FileName : null;
                });
                if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
                var prepared = await PrepareFile(path, ct);
                if (!prepared.IsOk) return Err(request, prepared.Error!);
                var imported = await lib.ImportAsync(prepared.Value, Strings(p["tags"]), ct);
                if (!imported.IsOk) return Err(request, imported.Error!);
                var i = imported.Value;
                return Ok(request, new JsonObject
                {
                    ["asset"] = Entry(i.Asset), ["created"] = i.Created, ["idWasTaken"] = i.IdWasTaken,
                    ["notes"] = new JsonArray(i.Notes.Select(n => (JsonNode)n).ToArray()),
                });
            }
            case "host.library.use":
            {
                var id = p["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) return Err(request, "invalid_request", "id is required");
                var r = await window.Resolver.ResolveAsync(id, "library", p["hash"]?.GetValue<string>(), ct);
                if (!r.IsOk) return Err(request, r.Error!);
                return Ok(request, new JsonObject
                {
                    ["ref"] = JsonSerializer.SerializeToNode(r.Value.Ref, ContractJson.Options), ["notes"] = new JsonArray(), ["width"] = r.Value.Width, ["height"] = r.Value.Height,
                });
            }
            case "host.library.replaceSource":
            {
                var id = p["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) return Err(request, "invalid_request", "id is required");
                var path = Ask("libraryReplace", () =>
                {
                    var d = new OpenFileDialog { Title = "Choose the new picture", Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.svg)|*.png;*.jpg;*.jpeg;*.bmp;*.svg" };
                    return d.ShowDialog(window) == true ? d.FileName : null;
                });
                if (path is null) return Ok(request, new JsonObject { ["cancelled"] = true });
                var prepared = await PrepareFile(path, ct);
                if (!prepared.IsOk) return Err(request, prepared.Error!);
                var r = await lib.ReplaceSourceAsync(id, prepared.Value, ct);
                if (!r.IsOk) return Err(request, r.Error!);
                return Ok(request, new JsonObject { ["asset"] = Entry(r.Value) });
            }
            case "host.library.setMetadata":
            {
                var id = p["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) return Err(request, "invalid_request", "id is required");
                var r = await lib.SetMetadataAsync(id, p["name"]?.GetValue<string>(), Strings(p["tags"]), ct);
                return r.IsOk ? Ok(request, new JsonObject { ["asset"] = Entry(r.Value) }) : Err(request, r.Error!);
            }
            case "host.library.delete":
            {
                var id = p["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) return Err(request, "invalid_request", "id is required");
                var r = await lib.DeleteAsync(id, ct);
                return r.IsOk ? Ok(request, new JsonObject { ["deleted"] = id }) : Err(request, r.Error!);
            }
            default: return Err(request, "method_not_found", $"unknown host method {request.Method}");
        }
    }

    async Task<Result<PreparedUpload>> PrepareFile(string path, CancellationToken ct)
    {
        var safe = PathPolicy.Validate(path);
        if (!safe.IsOk) return Result<PreparedUpload>.Fail(safe.Error!);
        if (!ImageTypes.TryGetValue(Path.GetExtension(safe.Value), out var mime)) return Result<PreparedUpload>.Fail("invalid_request", "that file type is not a supported picture");
        try
        {
            await using var stream = File.OpenRead(safe.Value);
            return await window.Preparer.PrepareAsync(stream, mime, Path.GetFileName(safe.Value), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Result<PreparedUpload>.Fail("io_error", "the file could not be read", retryable: true); }
    }
}
