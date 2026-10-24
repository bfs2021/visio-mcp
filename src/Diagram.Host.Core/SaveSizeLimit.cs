using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

/// <summary>
/// The error for a document that is too large to save in a format with a size budget. It says how big the file would be, what the limit is,
/// and which pictures take the room, so a person or an agent knows what to remove instead of seeing a generic internal error.
/// </summary>
public static class SaveSizeLimit
{
    const int Largest = 5;

    public static Result<T> Fail<T>(string format, long bytes, long limit, DiagramDocument document, Func<string, long?> sizeOfAsset)
    {
        var placements = document.Pages.SelectMany(p => p.Elements).OfType<ImageElement>().GroupBy(i => i.AssetId).ToDictionary(g => g.Key, g => g.Count());
        var pictures = document.Assets.Where(a => placements.ContainsKey(a.Id))
            .Select(a => (Asset: a, Bytes: sizeOfAsset(a.Sha256) ?? 0, Placed: placements[a.Id]))
            .OrderByDescending(p => p.Bytes).ThenBy(p => p.Asset.Id, StringComparer.Ordinal).ToList();
        var pictureBytes = pictures.Where(p => pictures.FindIndex(q => q.Asset.Sha256 == p.Asset.Sha256) == pictures.IndexOf(p)).Sum(p => p.Bytes); // one copy of a picture used under two names counts once
        var biggest = pictures.Take(Largest).ToList();

        var message = $"the document would be {Size(bytes)} as {format}, which is {Size(bytes - limit)} over the {Size(limit)} limit, so it was not saved.";
        if (biggest.Count > 0 && pictureBytes > 0)
            message += $" Its {pictures.Count} picture{(pictures.Count == 1 ? "" : "s")} hold {Size(pictureBytes)}; the largest {(biggest.Count == 1 ? "is" : "are")} "
                + string.Join(", ", biggest.Select(p => $"{p.Asset.Name} ({Size(p.Bytes)})")) + ". Remove or replace some pictures with smaller ones, or split the document, and save again.";
        else message += " Split the document into smaller ones and save again.";

        return Result<T>.Fail("limit_exceeded", message, details: new JsonObject
        {
            ["format"] = format, ["sizeBytes"] = bytes, ["limitBytes"] = limit, ["overByBytes"] = bytes - limit,
            ["pictureCount"] = pictures.Count, ["pictureBytes"] = pictureBytes,
            ["largestPictures"] = new JsonArray(biggest.Select(p => (JsonNode)new JsonObject { ["assetId"] = p.Asset.Id, ["name"] = p.Asset.Name, ["bytes"] = p.Bytes, ["placements"] = p.Placed }).ToArray()),
        });
    }

    static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MiB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KiB" : $"{bytes} bytes";
}
