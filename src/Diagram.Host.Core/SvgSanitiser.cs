using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Diagram.Host.Core;

/// <summary>
/// Allowlist sanitiser. Active content (scripts, event handlers, foreignObject, SMIL, links) is removed and reported;
/// anything that would fetch an external resource rejects the file. The output is deterministic, so it can be hashed.
/// </summary>
public static class SvgSanitiser
{
    static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "title", "desc",
        "linearGradient", "radialGradient", "stop", "clipPath", "mask", "pattern", "symbol", "use", "style", "image", "marker",
    };
    static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "script", "foreignObject", "iframe", "object", "embed", "audio", "video", "animate", "animateMotion", "animateTransform", "set", "handler", "listener",
    };

    static readonly Regex UrlRef = new(@"url\(\s*(['""]?)(?<t>[^)'""]*)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DataImage = new(@"^data:image/(png|jpeg);base64,[A-Za-z0-9+/=\s]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record Output(byte[] Bytes, IReadOnlyList<string> Notes);

    public static Result<Output> Sanitise(byte[] input)
    {
        if (input.Length > InputLimits.SvgBytes) return Result<Output>.Fail("limit_exceeded", $"SVG is {input.Length} bytes; the limit is {InputLimits.SvgBytes}");
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, // no entities, no external DTDs
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = 20L * 1024 * 1024,
            };
            using var reader = XmlReader.Create(new MemoryStream(input), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException e) { return Result<Output>.Fail("invalid_request", "the SVG is not well-formed XML or uses a DTD: " + e.Message); }

        if (doc.Root is null || doc.Root.Name != Svg + "svg") return Result<Output>.Fail("invalid_request", "the root element is not an SVG svg element");

        var notes = new List<string>();
        var count = 0;
        try { Walk(doc.Root, 0, notes, ref count); }
        catch (SvgReject r) { return Result<Output>.Fail("invalid_request", r.Message, details: new JsonObject { ["reason"] = r.Reason }); }
        catch (SvgLimit l) { return Result<Output>.Fail("limit_exceeded", l.Message); }

        doc.Declaration = null;
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false, Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None }))
            doc.Root.WriteTo(w);
        return Result<Output>.Ok(new Output(new UTF8Encoding(false).GetBytes(sb.ToString()), notes.Distinct().ToList()));
    }

    sealed class SvgReject(string reason, string message) : Exception(message) { public string Reason { get; } = reason; }
    sealed class SvgLimit(string message) : Exception(message);

    static void Walk(XElement el, int depth, List<string> notes, ref int count)
    {
        if (++count > InputLimits.SvgElements) throw new SvgLimit($"the SVG has more than {InputLimits.SvgElements} elements");
        if (depth > InputLimits.SvgDepth) throw new SvgLimit($"the SVG is nested deeper than {InputLimits.SvgDepth}");

        // Unwrapping a link exposes its children to the same checks, so rescan until nothing changes.
        bool changed;
        do
        {
            changed = false;
            foreach (var child in el.Elements().ToList())
            {
                var name = child.Name.LocalName;
                var inSvg = child.Name.Namespace == Svg;
                if (!inSvg || Dropped.Contains(name))
                {
                    notes.Add($"removed <{name}>{(name is "script" or "foreignObject" ? " (active content)" : "")}");
                    child.Remove();
                }
                else if (name == "a")
                {
                    // Links are inert data: keep the content, drop the link.
                    notes.Add("removed hyperlink <a>");
                    child.ReplaceWith(child.Nodes().ToList());
                    changed = true;
                    break;
                }
                else if (!Allowed.Contains(name))
                {
                    notes.Add($"removed unsupported <{name}>");
                    child.Remove();
                }
            }
        } while (changed);

        CleanAttributes(el, notes);
        if (el.Name.LocalName == "style") CheckStyleText(el.Value);
        foreach (var child in el.Elements().ToList()) Walk(child, depth + 1, notes, ref count);
    }

    static void CleanAttributes(XElement el, List<string> notes)
    {
        foreach (var attr in el.Attributes().ToList())
        {
            var local = attr.Name.LocalName;
            if (attr.IsNamespaceDeclaration)
            {
                // Only the SVG and XLink namespaces are meaningful; others (e.g. xhtml, scripting) are dropped.
                if (attr.Value is "http://www.w3.org/2000/svg" or "http://www.w3.org/1999/xlink") continue;
                attr.Remove();
                continue;
            }
            if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"removed event handler {local}");
                attr.Remove();
                continue;
            }
            if (local == "href" && (attr.Name.Namespace == XNamespace.None || attr.Name.Namespace == Xlink))
            {
                CheckHref(el, attr.Value);
                continue;
            }
            if (attr.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) || attr.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                || attr.Value.Contains("expression(", StringComparison.OrdinalIgnoreCase)) CheckStyleText(attr.Value);
        }
    }

    static void CheckHref(XElement el, string value)
    {
        var v = value.Trim();
        if (v.StartsWith('#')) return; // internal reference
        if (el.Name.LocalName == "image" && DataImage.IsMatch(v)) return;
        throw new SvgReject("external_resource", $"<{el.Name.LocalName}> refers to \"{Trim(v)}\", which is not an internal reference or an embedded PNG/JPEG");
    }

    static void CheckStyleText(string css)
    {
        if (Regex.IsMatch(css, @"@import", RegexOptions.IgnoreCase)) throw new SvgReject("external_resource", "CSS @import is not allowed");
        if (Regex.IsMatch(css, @"javascript:|expression\(", RegexOptions.IgnoreCase)) throw new SvgReject("external_resource", "script URLs and CSS expressions are not allowed");
        foreach (Match m in UrlRef.Matches(css))
        {
            var t = m.Groups["t"].Value.Trim();
            if (!t.StartsWith('#')) throw new SvgReject("external_resource", $"CSS url({Trim(t)}) is not an internal reference");
        }
    }

    static string Trim(string s) => s.Length > 60 ? s[..60] + "..." : s;
}
