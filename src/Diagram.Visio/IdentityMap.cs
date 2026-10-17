using System.Security.Cryptography;
using System.Text;
using Diagram.Visio;

namespace Diagram.Visio;

/// <summary>
/// Keeps element identity stable across a native round trip and repairs it deterministically when a file cannot be trusted: valid, unique
/// ids are kept; missing, invalid or copied ids get a name-based UUID derived from the exact file bytes, so importing the same unchanged
/// file twice yields the same ids.
/// </summary>
public sealed class IdentityMap
{
    // A fixed application namespace for version 5 UUIDs.
    static readonly Guid Namespace = new("6f1c2b0e-4d8a-5c3b-9a17-0d2e7f6a8b11");

    readonly string packageHash;
    readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> notes = new();
    int preserved, generated, repaired;

    public IdentityMap(string packageSha256) { packageHash = packageSha256; }

    public static bool IsUuid(string? s) => s is not null && Guid.TryParseExact(s, "D", out _) && s.Length == 36;

    /// <summary>Name-based (version 5) UUID, stable for the same input.</summary>
    public static string V5(string name)
    {
        var ns = Namespace.ToByteArray();
        SwapGuidOrder(ns);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[ns.Length + nameBytes.Length];
        ns.CopyTo(data, 0); nameBytes.CopyTo(data, ns.Length);
        var hash = SHA1.HashData(data);
        var b = new byte[16];
        Array.Copy(hash, b, 16);
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        SwapGuidOrder(b);
        return new Guid(b).ToString("D");
    }

    static void SwapGuidOrder(byte[] b)
    {
        (b[0], b[3]) = (b[3], b[0]); (b[1], b[2]) = (b[2], b[1]); (b[4], b[5]) = (b[5], b[4]); (b[6], b[7]) = (b[7], b[6]);
    }

    /// <summary>The id to use for a shape: the file's own if it is a unique UUID, otherwise a derived one.</summary>
    public string Resolve(string? fromFile, string locator, string what)
    {
        if (IsUuid(fromFile))
        {
            var id = fromFile!.ToLowerInvariant();
            if (seen.Add(id)) { preserved++; return id; }
            var fixedId = Fresh($"{locator}/copy");
            repaired++;
            notes.Add($"{what} at {locator} repeats the id {id} (a copied shape); it was given a new id.");
            return fixedId;
        }
        var made = Fresh(locator);
        if (fromFile is null) generated++; else { repaired++; notes.Add($"{what} at {locator} had an id that is not a UUID; it was given a new one."); }
        return made;
    }

    string Fresh(string locator)
    {
        var n = 0;
        string id;
        do { id = V5($"{packageHash}/{locator}/{n++}"); } while (!seen.Add(id));
        return id;
    }

    public IdentityReport Report() => new(preserved, generated, repaired, notes);
}
