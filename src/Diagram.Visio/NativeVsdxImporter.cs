using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using static Diagram.Visio.Vx;

namespace Diagram.Visio;

/// <summary>
/// Reads a .vsdx into a candidate canonical document. It understands the Visio XML for the supported object kinds, so it reads files this
/// application wrote (including after Visio saved them) as well as ordinary Visio drawings built from basic shapes, connectors and
/// pictures. Anything else is brought in as a clearly marked placeholder with a diagnostic, never as something that looks supported.
/// The package is checked first and nothing in it is trusted: ids, sizes and relationships are all validated.
/// </summary>
public sealed class NativeVsdxImporter : IVsdxImporter
{
    public Task<Result<ImportResult>> ImportAsync(Stream package, ImportOptions options, CancellationToken ct) => Task.FromResult(Import(package, options));

    public Result<ImportResult> Import(Stream package, ImportOptions options)
    {
        var opened = PackageGuard.Open(package, options);
        if (!opened.IsOk) return Result<ImportResult>.Fail(opened.Error!);
        using var pkg = opened.Value;
        try { return new Reader(pkg, options).Run(); }
        catch (ImportFailure f) { return Result<ImportResult>.Fail(f.Code, f.Message); }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return Result<ImportResult>.Fail("invalid_request", "the file could not be read as a diagram: " + e.Message);
        }
    }

    sealed class ImportFailure(string code, string message) : Exception(message) { public string Code { get; } = code; }

    // ============================================================================================================
    sealed class Reader
    {
        readonly GuardedPackage pkg;
        readonly ImportOptions options;
        readonly List<Diagnostic> diag = new();
        readonly IdentityMap identity;
        readonly Dictionary<string, ImportedBlob> blobs = new();
        readonly Dictionary<string, string> faces = new();
        readonly Dictionary<string, Style> styles = new();
        readonly Dictionary<string, JsonObject> assetsBySha = new();
        readonly Dictionary<string, JsonObject> catalogue = new(); // from the document sheet, by sha of the asset as exported
        int shapeCount;

        public Reader(GuardedPackage pkg, ImportOptions options)
        {
            this.pkg = pkg; this.options = options;
            identity = new IdentityMap(pkg.Sha256);
            diag.AddRange(pkg.Diagnostics);
        }

        void Note(string code, string message, string? pageId = null, string? elementId = null, string? shape = null, string? action = null, string severity = "warning") =>
            diag.Add(new Diagnostic { Severity = severity, Code = code, Message = message, PageId = pageId, ElementId = elementId, SourceShape = shape, Action = action });

        XDocument Xml(string part) { var r = pkg.ReadXml(part); if (!r.IsOk) throw new ImportFailure(r.Error!.Code, r.Error.Message); return r.Value; }

        // ---------------------------------------------------------------- relationships
        record Rel(string Id, string Type, string? Target);

        List<Rel> RelsOf(string part)
        {
            var dir = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
            var name = part[(part.LastIndexOf('/') + 1)..];
            var relPart = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + name + ".rels";
            if (!pkg.Has(relPart)) return [];
            var list = new List<Rel>();
            foreach (var r in Xml(relPart).Descendants(Rels + "Relationship"))
            {
                var external = string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
                var target = (string?)r.Attribute("Target") ?? "";
                if (external) { Note("external_relationship_ignored", $"A link to {Shorten(target)} was not followed.", severity: "info"); list.Add(new((string)r.Attribute("Id")!, (string?)r.Attribute("Type") ?? "", null)); continue; }
                var resolved = PackageGuard.ResolveTarget(part, target, false);
                if (!resolved.IsOk) throw new ImportFailure("invalid_request", resolved.Error!.Message);
                list.Add(new((string)r.Attribute("Id")!, (string?)r.Attribute("Type") ?? "", resolved.Value));
            }
            return list;
        }
        static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;

        // ---------------------------------------------------------------- masters (stencil shapes)
        string? mastersPart;
        readonly Dictionary<string, XElement?> masterShapes = new();
        readonly HashSet<string> masterNotes = new();

        XElement? MasterRoot(string masterId)
        {
            if (masterShapes.TryGetValue(masterId, out var cached)) return cached;
            XElement? found = null;
            if (mastersPart is not null && pkg.Has(mastersPart))
            {
                var el = Xml(mastersPart).Descendants(V + "Master").FirstOrDefault(x => (string?)x.Attribute("ID") == masterId);
                var rid = (string?)el?.Element(V + "Rel")?.Attribute(R + "id");
                var part = RelsOf(mastersPart).FirstOrDefault(r => r.Id == rid)?.Target;
                if (part is not null && pkg.Has(part)) found = Xml(part).Root?.Element(V + "Shapes")?.Elements(V + "Shape").FirstOrDefault();
                if (found is not null && masterNotes.Add(masterId))
                    Note("master_inlined", $"Shapes from the Visio stencil master '{(string?)el?.Attribute("NameU") ?? masterId}' were imported as ordinary shapes; their link to the master is not kept.", severity: "info");
            }
            return masterShapes[masterId] = found;
        }

        /// <summary>A shape dropped from a stencil stores only what differs from its master. Fill in the rest from the master so size, outline and ports are known.</summary>
        void InheritFromMasters(IEnumerable<XElement> top, int depth = 0)
        {
            foreach (var s in top) Inherit(s, null, depth);
        }

        void Inherit(XElement s, XElement? parentSrc, int depth)
        {
            if (depth > 64) return;
            XElement? src = null;
            if ((string?)s.Attribute("Master") is { } m) src = MasterRoot(m);
            else if (parentSrc is not null && (string?)s.Attribute("MasterShape") is { } ms) src = parentSrc.DescendantsAndSelf(V + "Shape").FirstOrDefault(x => (string?)x.Attribute("ID") == ms);
            if (src is not null) MergeFromMaster(s, src);
            foreach (var c in s.Element(V + "Shapes")?.Elements(V + "Shape") ?? []) Inherit(c, src ?? parentSrc, depth + 1);
        }

        static void MergeFromMaster(XElement s, XElement src)
        {
            foreach (var a in new[] { "LineStyle", "FillStyle", "TextStyle" })
                if (s.Attribute(a) is null && src.Attribute(a) is { } av) s.SetAttributeValue(a, av.Value);
            var have = s.Elements(V + "Cell").Select(c => (string?)c.Attribute("N")).ToHashSet();
            var added = new List<XElement>();
            foreach (var c in src.Elements(V + "Cell"))
                if (!have.Contains((string?)c.Attribute("N"))) { var copy = new XElement(c); s.Add(copy); added.Add(copy); }
            foreach (var sec in src.Elements(V + "Section"))
            {
                var key = (string?)sec.Attribute("N"); var ix = (string?)sec.Attribute("IX");
                var mine = s.Elements(V + "Section").FirstOrDefault(x => (string?)x.Attribute("N") == key && (string?)x.Attribute("IX") == ix);
                if (mine is null) { var copy = new XElement(sec); s.Add(copy); added.Add(copy); continue; }
                foreach (var row in sec.Elements(V + "Row"))
                {
                    var t = (string?)row.Attribute("T"); var rix = (string?)row.Attribute("IX"); var rn = (string?)row.Attribute("N");
                    if (!mine.Elements(V + "Row").Any(r => (string?)r.Attribute("T") == t && (string?)r.Attribute("IX") == rix && (string?)r.Attribute("N") == rn)) { var copy = new XElement(row); mine.Add(copy); added.Add(copy); }
                }
            }
            // inherited cells hold the master's values; where they are proportional to the shape's size, restate them for this instance
            double Size(string n) => double.TryParse(Own(s, n), System.Globalization.NumberStyles.Float, Inv, out var v) ? v : 0;
            var w = Size("Width"); var h = Size("Height");
            foreach (var cell in added.SelectMany(e => e.Name == V + "Cell" ? new[] { e } : e.Descendants(V + "Cell")))
            {
                var f = (string?)cell.Attribute("F");
                if (f is null) continue;
                var m = System.Text.RegularExpressions.Regex.Match(f, @"^(Width|Height)(?:\*(-?[0-9.]+))?$");
                if (!m.Success) continue;
                var factor = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, Inv) : 1.0;
                cell.SetAttributeValue("V", ((m.Groups[1].Value == "Width" ? w : h) * factor).ToString("R", Inv));
            }
        }

        // ---------------------------------------------------------------- styles and cells
        sealed record Style(string? BasedOn, Dictionary<string, string> Cells, Dictionary<string, string> Char);

        void ReadStyles(XDocument docXml)
        {
            foreach (var f in docXml.Descendants(V + "FaceName")) faces[(string?)f.Attribute("ID") ?? "0"] = (string?)f.Attribute("Name") ?? "Segoe UI";
            foreach (var s in docXml.Descendants(V + "StyleSheet"))
            {
                var cells = s.Elements(V + "Cell").Where(c => c.Attribute("N") != null && c.Attribute("V") != null).ToDictionary(c => (string)c.Attribute("N")!, c => (string)c.Attribute("V")!, StringComparer.Ordinal);
                var ch = s.Elements(V + "Section").Where(x => (string?)x.Attribute("N") == "Character").Elements(V + "Row").FirstOrDefault()?.Elements(V + "Cell")
                    .Where(c => c.Attribute("N") != null && c.Attribute("V") != null).ToDictionary(c => (string)c.Attribute("N")!, c => (string)c.Attribute("V")!) ?? new Dictionary<string, string>();
                styles[(string?)s.Attribute("ID") ?? "0"] = new Style((string?)s.Attribute("BasedOn"), cells, ch);
            }
        }

        string? FromStyle(string? styleId, string cell, bool character = false)
        {
            for (var id = styleId; id is not null && styles.TryGetValue(id, out var st);)
            {
                var d = character ? st.Char : st.Cells;
                if (d.TryGetValue(cell, out var v)) return v;
                id = st.BasedOn;
            }
            return null;
        }

        static string? Own(XElement shape, string cell) => shape.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == cell)?.Attribute("V")?.Value;
        static string? OwnF(XElement shape, string cell) => shape.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == cell)?.Attribute("F")?.Value;

        string? Line(XElement shape, string cell) => Own(shape, cell) ?? FromStyle((string?)shape.Attribute("LineStyle"), cell);
        string? FillC(XElement shape, string cell) => Own(shape, cell) ?? FromStyle((string?)shape.Attribute("FillStyle"), cell);

        static Dictionary<string, string> UserCells(XElement shape)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sec in shape.Elements(V + "Section").Where(s => (string?)s.Attribute("N") == "User"))
                foreach (var row in sec.Elements(V + "Row"))
                {
                    var n = (string?)row.Attribute("N");
                    var v = row.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == "Value")?.Attribute("V")?.Value;
                    if (n is not null && v is not null) d[n] = v;
                }
            return d;
        }

        static readonly string[] Palette = ["#000000", "#ffffff", "#ff0000", "#00ff00", "#0000ff", "#ffff00", "#ff00ff", "#00ffff", "#800000", "#008000", "#000080", "#808000", "#800080", "#008080", "#c0c0c0", "#e6e6e6",
            "#cdcdcd", "#b3b3b3", "#9a9a9a", "#808080", "#666666", "#4d4d4d", "#333333", "#1a1a1a"];

        string Colour(string? v, string fallback, string? elementId, string? pageId)
        {
            if (v is null) return fallback;
            if (v.Length == 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit)) return v.ToLowerInvariant();
            if (int.TryParse(v, System.Globalization.NumberStyles.Integer, Inv, out var i) && i >= 0 && i < Palette.Length) return Palette[i];
            Note("colour_approximated", $"The colour '{v}' is not a plain RGB value and was replaced with {fallback}.", pageId, elementId, action: "colour approximated", severity: "info");
            return fallback;
        }

        // ---------------------------------------------------------------- run
        public Result<ImportResult> Run()
        {
            // main document part
            var root = RelsOf("_rels/.rels".Replace("_rels/.rels", "x")).Count; _ = root;
            var rootRels = new List<Rel>();
            foreach (var r in Xml("_rels/.rels").Descendants(Rels + "Relationship"))
            {
                var res = PackageGuard.ResolveTarget("x", (string?)r.Attribute("Target") ?? "", string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase));
                if (!res.IsOk) throw new ImportFailure("invalid_request", res.Error!.Message);
                rootRels.Add(new((string)r.Attribute("Id")!, (string?)r.Attribute("Type") ?? "", res.Value));
            }
            var mainPart = rootRels.FirstOrDefault(r => r.Type.EndsWith("/document", StringComparison.Ordinal))?.Target ?? "visio/document.xml";
            if (!pkg.Has(mainPart)) throw new ImportFailure("invalid_request", "the package has no Visio document part");
            var docXml = Xml(mainPart);
            ReadStyles(docXml);
            var docUser = UserCells(docXml.Descendants(V + "DocumentSheet").FirstOrDefault() ?? new XElement("x"));
            foreach (var kv in docUser.Where(k => k.Key.StartsWith("AgentAsset", StringComparison.Ordinal) && k.Key != "AgentAssetId"))
            {
                try { var j = JsonNode.Parse(kv.Value)!.AsObject(); if (j["sha"] is JsonNode sha) catalogue[sha.GetValue<string>()] = j; } catch (JsonException) { }
            }

            var docRels = RelsOf(mainPart);
            mastersPart = docRels.FirstOrDefault(r => r.Type.EndsWith("/masters", StringComparison.Ordinal))?.Target;
            var pagesPart = docRels.FirstOrDefault(r => r.Type.EndsWith("/pages", StringComparison.Ordinal))?.Target ?? throw new ImportFailure("invalid_request", "the package has no pages part");
            var pagesXml = Xml(pagesPart);
            var pageRels = RelsOf(pagesPart);
            var pageEls = pagesXml.Descendants(V + "Page").ToList();
            if (pageEls.Count == 0) throw new ImportFailure("invalid_request", "the drawing has no pages");
            if (pageEls.Count > options.MaxPages) throw new ImportFailure("limit_exceeded", $"the drawing has more than {options.MaxPages} pages");

            var pages = new JsonArray();
            for (var pi = 0; pi < pageEls.Count; pi++)
            {
                var pe = pageEls[pi];
                var rid = (string?)pe.Element(V + "Rel")?.Attribute(R + "id");
                var part = pageRels.FirstOrDefault(r => r.Id == rid)?.Target;
                if (part is null || !pkg.Has(part)) { Note("page_missing", $"Page {pi + 1} has no content part and was imported empty.", action: "page empty"); }
                pages.Add(new PageReader(this, pe, part is not null && pkg.Has(part) ? part : null, pi).Read());
            }

            var title = docUser.GetValueOrDefault("AgentTitle") ?? CoreTitle() ?? "Untitled";
            var metadata = new JsonObject();
            if (docUser.TryGetValue("AgentDocumentMetadata", out var md)) { try { foreach (var kv in JsonNode.Parse(md)!.AsObject()) metadata[kv.Key] = kv.Value?.GetValue<string>() ?? ""; } catch (Exception e) when (e is JsonException or InvalidOperationException) { } }
            var docId = IdentityMap.IsUuid(docUser.GetValueOrDefault("AgentDocumentId")) ? docUser["AgentDocumentId"].ToLowerInvariant() : IdentityMap.V5($"{pkg.Sha256}/document");
            var json = new JsonObject
            {
                ["schemaVersion"] = 1, ["id"] = docId, ["title"] = title.Length > 512 ? title[..512] : title, ["revision"] = 0, ["pages"] = pages,
                ["assets"] = new JsonArray(assetsBySha.Values.Select(a => (JsonNode)a.DeepClone()).ToArray()), ["metadata"] = metadata, ["source"] = new JsonObject { ["format"] = "vsdx" },
            };
            DiagramDocument doc;
            try { doc = json.Deserialize<DiagramDocument>(ContractJson.Options)!; }
            catch (JsonException e) { throw new ImportFailure("invalid_request", "the drawing does not fit the diagram model: " + e.Message); }
            return Result<ImportResult>.Ok(new ImportResult(doc, blobs.Values.ToList(), diag, identity.Report()));
        }

        string? CoreTitle()
        {
            if (!pkg.Has("docProps/core.xml")) return null;
            var r = pkg.ReadXml("docProps/core.xml");
            return r.IsOk ? r.Value.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value : null;
        }

        // =================================================================================================================
        sealed class PageReader(Reader rd, XElement pageEl, string? contentPart, int pageIndex)
        {
            readonly Dictionary<string, string> idByShape = new();            // visio shape id -> canonical id
            readonly Dictionary<string, XElement> shapeById = new();
            readonly Dictionary<string, List<(string Id, double X, double Y)>> portsOf = new();
            readonly Dictionary<string, string> layerIdByIndex = new();
            readonly List<JsonObject> elements = new();
            readonly Dictionary<string, JsonObject> elementById = new();
            readonly List<(JsonObject Group, XElement Shape, string[] ConnectorMembers)> groups = new();
            readonly Dictionary<string, Dictionary<string, string>> userByShape = new();
            readonly Dictionary<string, (string Page, string Text)> placeholders = new();
            string pageId = "";
            double pageHeightPt;
            int zCounter;
            List<Rel> rels = [];
            readonly HashSet<string> aliases = new(StringComparer.Ordinal);

            void Note(string code, string message, string? elementId = null, string? shape = null, string? action = null, string severity = "warning") =>
                rd.Note(code, message, pageId, elementId, shape, action, severity);

            public JsonObject Read()
            {
                var sheet = pageEl.Element(V + "PageSheet") ?? new XElement(V + "PageSheet");
                var user = UserCells(sheet);
                pageId = rd.identity.Resolve(user.GetValueOrDefault("AgentPageId"), $"page{pageIndex}", "page");
                var widthIn = D(Own(sheet, "PageWidth"), 11.692913385826772); var heightIn = D(Own(sheet, "PageHeight"), 8.267716535433072);
                var wPt = Math.Clamp(widthIn * PtPerInch, 1, 14400); pageHeightPt = Math.Clamp(heightIn * PtPerInch, 1, 14400);
                if (widthIn * PtPerInch > 14400 || heightIn * PtPerInch > 14400) Note("page_size_clamped", "The page is larger than the editor allows and was reduced.", action: "page size clamped");

                var grid = new JsonObject { ["visible"] = true, ["spacingPt"] = Math.Max(0.5, D(Own(sheet, "XGridSpacing"), 0.19685) * PtPerInch), ["snap"] = true };
                if (user.TryGetValue("AgentGrid", out var g)) { try { var j = JsonNode.Parse(g)!.AsObject(); grid["visible"] = j["visible"]?.GetValue<bool>() ?? true; grid["spacingPt"] = Math.Max(0.5, j["spacingPt"]?.GetValue<double>() ?? 14.17); grid["snap"] = j["snap"]?.GetValue<bool>() ?? true; } catch (Exception e) when (e is JsonException or InvalidOperationException) { } }
                JsonNode background = new JsonObject { ["kind"] = "solid", ["colour"] = "#ffffff", ["opacity"] = 1 };
                if (user.TryGetValue("AgentBackground", out var bgs)) { try { var j = JsonNode.Parse(bgs)!.AsObject(); background = j["none"] is not null ? new JsonObject { ["kind"] = "none" } : new JsonObject { ["kind"] = "solid", ["colour"] = j["colour"]?.GetValue<string>() ?? "#ffffff", ["opacity"] = j["opacity"]?.GetValue<double>() ?? 1 }; } catch (Exception e) when (e is JsonException or InvalidOperationException) { } }

                var layers = ReadLayers(sheet, user);
                if (contentPart is not null) ReadContent(contentPart, user);
                return new JsonObject
                {
                    ["id"] = pageId, ["name"] = Trim((string?)pageEl.Attribute("Name") ?? (string?)pageEl.Attribute("NameU") ?? $"Page {pageIndex + 1}", 256), ["widthPt"] = wPt, ["heightPt"] = pageHeightPt,
                    ["background"] = background, ["grid"] = grid, ["guides"] = guides, ["layers"] = layers, ["elements"] = new JsonArray(elements.Select(e => (JsonNode)e).ToArray()),
                };
            }

            static string Trim(string s, int max) => s.Length > max ? s[..max] : s.Length == 0 ? "Untitled" : s;
            readonly JsonArray guides = new();
            readonly List<JsonObject> connectorEls = new();

            JsonArray ReadLayers(XElement sheet, Dictionary<string, string> user)
            {
                var result = new JsonArray();
                var rows = sheet.Elements(V + "Section").FirstOrDefault(s => (string?)s.Attribute("N") == "Layer")?.Elements(V + "Row").ToList() ?? [];
                var saved = new List<(string Id, string Name)>();
                if (user.TryGetValue("AgentLayers", out var al)) { try { foreach (var n in JsonNode.Parse(al)!.AsArray()) saved.Add((n!["id"]!.GetValue<string>(), n["name"]!.GetValue<string>())); } catch (Exception e) when (e is JsonException or InvalidOperationException) { } }
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    string? C(string n) => r.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == n)?.Attribute("V")?.Value;
                    var name = Trim(C("Name") ?? C("NameUniv") ?? $"Layer {i + 1}", 256);
                    // match by name first (Visio re-indexes rows), then by position; a duplicate name falls back to position
                    var byName = saved.Where(s => s.Name == name).ToList();
                    var match = byName.Count == 1 ? byName[0] : (i < saved.Count && saved[i].Name == name ? saved[i] : default);
                    var id = rd.identity.Resolve(match.Id, $"page{pageIndex}/layer{i}", "layer");
                    layerIdByIndex[i.ToString(Inv)] = id;
                    var layer = new JsonObject { ["id"] = id, ["name"] = name, ["visible"] = C("Visible") != "0", ["locked"] = C("Lock") == "1", ["printable"] = C("Print") != "0", ["snap"] = C("Snap") != "0", ["glue"] = C("Glue") != "0" };
                    var colour = C("Color");
                    if (colour is { Length: 7 } && colour[0] == '#' && colour.Skip(1).All(Uri.IsHexDigit)) layer["colourOverride"] = colour.ToLowerInvariant();
                    result.Add(layer);
                }
                return result;
            }

            // ------------------------------------------------------------ content
            void ReadContent(string part, Dictionary<string, string> pageUser)
            {
                var content = rd.Xml(part);
                rels = rd.RelsOf(part);
                var top = content.Root?.Element(V + "Shapes")?.Elements(V + "Shape").ToList() ?? [];
                rd.InheritFromMasters(top);
                var all = new List<XElement>();
                void Collect(XElement s) { all.Add(s); foreach (var c in s.Element(V + "Shapes")?.Elements(V + "Shape") ?? []) Collect(c); }
                foreach (var s in top) Collect(s);
                rd.shapeCount += all.Count;
                if (rd.shapeCount > rd.options.MaxShapes) throw new ImportFailure("limit_exceeded", $"the drawing has more than {rd.options.MaxShapes} shapes");

                // identity first, in document order, so that a copied id is repaired on the later shape
                foreach (var s in all)
                {
                    var sid = (string?)s.Attribute("ID") ?? "";
                    shapeById[sid] = s;
                    var u = UserCells(s); userByShape[sid] = u;
                    idByShape[sid] = rd.identity.Resolve(u.GetValueOrDefault("AgentId"), $"page{pageIndex}/shape{sid}", "shape");
                    if (u.GetValueOrDefault("AgentKind") == "guide" || (string?)s.Attribute("Type") == "Guide") continue;
                    ReadPorts(s, sid, u);
                }

                var connects = content.Root?.Element(V + "Connects")?.Elements(V + "Connect").ToList() ?? [];
                foreach (var s in top) Walk(s, null, connects);

                // a connector's fallback box needs the shapes it is glued to, which may come later in the file
                foreach (var c in connectorEls) c["bounds"] = ConnectorBounds(c);
                foreach (var (group, shape, connMembers) in groups)
                {
                    var members = (JsonArray)group["childIds"]!;
                    foreach (var cm in connMembers) if (IdentityMap.IsUuid(cm) && elementById.ContainsKey(cm.ToLowerInvariant()) && elementById[cm.ToLowerInvariant()]["kind"]!.GetValue<string>() == "connector") members.Add(cm.ToLowerInvariant());
                }
                // group bounds are derived from their members; groups stack with their topmost member
                foreach (var (group, _, _) in groups.AsEnumerable().Reverse()) FinishGroup(group);
                _ = pageUser;
            }

            // ------------------------------------------------------------ coordinates
            sealed record Xf(double PinX, double PinY, double LocX, double LocY, double Angle, double Width, double Height, Xf? Parent);

            Xf Transform(XElement s, Xf? parent) => new(D(Own(s, "PinX")), D(Own(s, "PinY")), D(Own(s, "LocPinX"), D(Own(s, "Width")) / 2), D(Own(s, "LocPinY"), D(Own(s, "Height")) / 2), D(Own(s, "Angle")), D(Own(s, "Width")), D(Own(s, "Height")), parent);

            /// <summary>A point in a shape's own local space (inches, origin bottom left) as a point on the page (inches, y up).</summary>
            static (double X, double Y) ToPage(Xf x, double lx, double ly)
            {
                var dx = lx - x.LocX; var dy = ly - x.LocY;
                var c = Math.Cos(x.Angle); var s = Math.Sin(x.Angle);
                var px = x.PinX + dx * c - dy * s; var py = x.PinY + dx * s + dy * c;
                return x.Parent is null ? (px, py) : ToPage(x.Parent, px, py);
            }

            static double TotalAngle(Xf x) => x.Angle + (x.Parent is null ? 0 : TotalAngle(x.Parent));

            (double X, double Y) PagePt((double X, double Y) inches) => (inches.X * PtPerInch, pageHeightPt - inches.Y * PtPerInch);

            static double NormaliseDeg(double d) { d %= 360; if (d > 180) d -= 360; if (d <= -180) d += 360; return Math.Abs(d) < 1e-9 ? 0 : d; }

            JsonObject Bounds(Xf x)
            {
                var (cx, cy) = PagePt(ToPage(x, x.Width / 2, x.Height / 2));
                var w = x.Width * PtPerInch; var h = x.Height * PtPerInch;
                return new JsonObject { ["x"] = cx - w / 2, ["y"] = cy - h / 2, ["width"] = w, ["height"] = h };
            }

            // ------------------------------------------------------------ walk
            void Walk(XElement s, Xf? parent, List<XElement> connects)
            {
                var sid = (string?)s.Attribute("ID") ?? "";
                var u = userByShape[sid];
                var type = (string?)s.Attribute("Type") ?? "Shape";
                if (type == "Guide" || u.GetValueOrDefault("AgentKind") == "guide") { ReadGuide(s, sid); return; }
                var xf = Transform(s, parent);
                if (D(Own(s, "FlipX")) != 0 || D(Own(s, "FlipY")) != 0) Note("flip_not_supported", "A flipped shape was imported unflipped.", idByShape[sid], sid, "flip dropped");
                if (type == "Group") { ReadGroup(s, sid, u, xf, connects); return; }
                if (type == "Foreign") { ReadPicture(s, sid, u, xf); return; }
                if (IsConnector(s)) { ReadConnector(s, sid, u, xf, connects); return; }
                ReadShape(s, sid, u, xf);
            }

            static bool IsConnector(XElement s) => Own(s, "BeginX") is not null && Own(s, "EndX") is not null || Own(s, "ObjType") == "2";

            // ------------------------------------------------------------ common properties
            JsonObject Base(XElement s, string sid, Dictionary<string, string> u, string kind, Xf xf)
            {
                var id = idByShape[sid];
                var o = new JsonObject
                {
                    ["id"] = id, ["kind"] = kind, ["layerIds"] = LayerIds(s), ["zIndex"] = 0, ["locked"] = false, ["hidden"] = false, ["bounds"] = Bounds(xf),
                    ["rotationDeg"] = NormaliseDeg(-TotalAngle(xf) * 180 / Math.PI), ["metadata"] = new JsonObject(),
                };
                var alias = u.GetValueOrDefault("AgentAlias");
                if (alias is { Length: > 0 and <= 128 })
                {
                    if (rd.catalogueSeenAlias(pageId, alias, aliases)) o["alias"] = alias;
                    else Note("alias_repeated", $"The alias \"{alias}\" is already used on this page; the later shape lost it.", id, sid, "alias dropped");
                }
                if (u.TryGetValue("AgentName", out var name) && name.Length <= 256) o["name"] = name;
                if (u.TryGetValue("AgentMetadata", out var meta)) { try { var m = new JsonObject(); foreach (var kv in JsonNode.Parse(meta)!.AsObject()) m[kv.Key] = kv.Value?.GetValue<string>() ?? ""; o["metadata"] = m; } catch (Exception e) when (e is JsonException or InvalidOperationException) { } }
                var locks = new[] { "LockMoveX", "LockMoveY", "LockWidth", "LockHeight" }.All(n => Own(s, n) == "1");
                o["locked"] = u.GetValueOrDefault("AgentLocked") == "1" || locks;
                o["hidden"] = u.GetValueOrDefault("AgentHidden") == "1";
                return o;
            }

            JsonArray LayerIds(XElement s)
            {
                var arr = new JsonArray();
                var lm = Own(s, "LayerMember");
                if (string.IsNullOrEmpty(lm)) return arr;
                foreach (var part in lm.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (layerIdByIndex.TryGetValue(part.Trim(), out var id) && !arr.Any(n => n!.GetValue<string>() == id)) arr.Add(id);
                return arr;
            }

            void Add(JsonObject el, int sz = 1)
            {
                el["zIndex"] = ++zCounter;
                elements.Add(el);
                elementById[el["id"]!.GetValue<string>()] = el;
                _ = sz;
            }

            // ------------------------------------------------------------ shapes
            void ReadShape(XElement s, string sid, Dictionary<string, string> u, Xf xf)
            {
                var hasGeometry = s.Elements(V + "Section").Any(x => (string?)x.Attribute("N") == "Geometry");
                var kindHint = u.GetValueOrDefault("AgentKind");
                var text = ReadText(s, u, idByShape[sid]);
                var fillNone = rd.FillC(s, "FillPattern") == "0";
                var lineNone = rd.Line(s, "LinePattern") == "0";
                var isText = kindHint == "text" || (kindHint is null && fillNone && lineNone && text is not null && hasGeometry);
                if (isText)
                {
                    var t = Base(s, sid, u, "text", xf);
                    t["text"] = text ?? DefaultText();
                    Add(t);
                    return;
                }
                var shape = Base(s, sid, u, "shape", xf);
                var geometry = ReadGeometryFor(s, sid, u, xf, hasGeometry);
                shape["geometry"] = geometry.Geometry;
                shape["style"] = Style(s, idByShape[sid], u);
                if (text is not null) shape["text"] = text;
                if (portsOf.TryGetValue(sid, out var ports) && ports.Count > 0) shape["ports"] = new JsonArray(ports.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["x"] = Math.Clamp(p.X, 0, 1), ["y"] = Math.Clamp(p.Y, 0, 1) }).ToArray());
                if (geometry.Placeholder) { ((JsonObject)shape["metadata"]!)["visio.unsupported"] = geometry.Why!; }
                Add(shape);
            }

            JsonObject Style(XElement s, string elementId, Dictionary<string, string> u)
            {
                var fillColour = rd.Colour(rd.FillC(s, "FillForegnd"), "#ffffff", elementId, pageId);
                var fillTrans = D(rd.FillC(s, "FillForegndTrans"), 0);
                JsonNode fill = rd.FillC(s, "FillPattern") == "0" ? new JsonObject { ["kind"] = "none" } : new JsonObject { ["kind"] = "solid", ["colour"] = fillColour, ["opacity"] = Math.Round(Math.Clamp(1 - fillTrans, 0, 1), 6) };
                var lineColour = rd.Colour(rd.Line(s, "LineColor"), "#000000", elementId, pageId);
                var lineTrans = D(rd.Line(s, "LineColorTrans"), 0);
                var pattern = (int)D(rd.Line(s, "LinePattern"), 1);
                JsonNode stroke = pattern == 0 ? new JsonObject { ["kind"] = "none" } : new JsonObject { ["kind"] = "solid", ["colour"] = lineColour, ["opacity"] = Math.Round(Math.Clamp(1 - lineTrans, 0, 1), 6) };
                var width = D(rd.Line(s, "LineWeight"), 0.01) * PtPerInch;
                if (pattern == 0 && u.TryGetValue("AgentStrokeWidthPt", out var sw)) width = D(sw, width);
                var cap = (int)D(rd.Line(s, "LineCap"), 1) switch { 0 => "round", 2 => "square", _ => "butt" };
                return new JsonObject { ["fill"] = fill, ["stroke"] = stroke, ["strokeWidthPt"] = Math.Max(0, width), ["dash"] = DashOf(pattern == 0 ? 1 : pattern), ["lineCap"] = cap, ["lineJoin"] = "miter" };
            }

            static JsonObject DefaultText() => new() { ["value"] = "", ["fontFamily"] = "Segoe UI", ["fontSizePt"] = 12, ["bold"] = false, ["italic"] = false, ["underline"] = false, ["colour"] = "#000000", ["horizontalAlign"] = "center", ["verticalAlign"] = "middle", ["wrap"] = true, ["paddingPt"] = 4 };

            JsonObject? ReadText(XElement s, Dictionary<string, string> u, string elementId)
            {
                var textEl = s.Element(V + "Text");
                var value = textEl is null ? "" : string.Concat(textEl.Nodes().OfType<XText>().Select(t => t.Value));
                if (value.EndsWith('\n')) value = value[..^1]; // Visio keeps a closing paragraph mark
                value = value.Replace("\r\n", "\n");
                if (value.Length > 65536) { value = value[..65536]; Note("text_truncated", "Text longer than the editor allows was cut.", elementId, action: "text truncated"); }
                if (value.Length == 0 && u.GetValueOrDefault("AgentTextBlock") != "1") return null;
                var ch = s.Elements(V + "Section").FirstOrDefault(x => (string?)x.Attribute("N") == "Character")?.Elements(V + "Row").FirstOrDefault();
                string? C(string n) => ch?.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == n)?.Attribute("V")?.Value ?? rd.FromStyle((string?)s.Attribute("TextStyle"), n, character: true);
                var para = s.Elements(V + "Section").FirstOrDefault(x => (string?)x.Attribute("N") == "Paragraph")?.Elements(V + "Row").FirstOrDefault()?.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == "HorzAlign")?.Attribute("V")?.Value;
                var style = (int)D(C("Style"), 0);
                var size = D(C("Size"), 12.0 / PtPerInch) * PtPerInch;
                var font = C("Font");
                var margin = D(Own(s, "LeftMargin"), 4.0 / PtPerInch) * PtPerInch;
                return new JsonObject
                {
                    ["value"] = value, ["fontFamily"] = font is not null && rd.faces.TryGetValue(font, out var face) ? Trim(face, 128) : "Segoe UI", ["fontSizePt"] = Math.Clamp(size, 1, 1000),
                    ["bold"] = (style & 1) != 0, ["italic"] = (style & 2) != 0, ["underline"] = (style & 4) != 0, ["colour"] = rd.Colour(C("Color"), "#000000", elementId, pageId),
                    ["horizontalAlign"] = para switch { "0" => "left", "2" => "right", _ => "center" }, ["verticalAlign"] = D(Own(s, "VerticalAlign"), 1) switch { 0 => "top", 2 => "bottom", _ => "middle" },
                    ["wrap"] = u.GetValueOrDefault("AgentNoWrap") != "1", ["paddingPt"] = Math.Max(0, margin),
                };
            }

            // ------------------------------------------------------------ geometry
            sealed record GeoResult(JsonObject Geometry, bool Placeholder, string? Why);

            GeoResult ReadGeometryFor(XElement s, string sid, Dictionary<string, string> u, Xf xf, bool hasGeometry)
            {
                var id = idByShape[sid];
                if (!hasGeometry)
                {
                    var master = (string?)s.Attribute("Master");
                    Note("unsupported_shape", master is not null ? "This shape comes from a Visio master (stencil) that is not supported; it is shown as a marked placeholder." : "This shape has no outline; it is shown as a marked placeholder.", id, sid, "placeholder rectangle");
                    return new GeoResult(new JsonObject { ["preset"] = "rectangle" }, true, master is not null ? "stencil master" : "no outline");
                }
                var sections = ReadSections(s, xf.Width, xf.Height, id, sid);
                var rounding = D(Own(s, "Rounding"), 0) * PtPerInch;
                var sig = GeometryMap.Signature(sections);
                var presetName = u.GetValueOrDefault("AgentPreset");
                if (presetName is not null && presetName != "svgPath" && presetName != "text" && presetName != "rounded-rectangle" && sig == GeometryMap.Signature(GeometryMap.ForPreset(presetName)) && presetName != "rectangle")
                    return new GeoResult(new JsonObject { ["preset"] = presetName }, false, null);
                if (sig == GeometryMap.Signature(GeometryMap.ForPreset("rectangle")))
                {
                    if (rounding > 0.01 || presetName == "rounded-rectangle")
                    {
                        var g = new JsonObject { ["preset"] = "rounded-rectangle" };
                        if (rounding > 0.01) g["cornerRadiusPt"] = rounding; else if (u.TryGetValue("AgentCornerRadiusPt", out var cr)) g["cornerRadiusPt"] = D(cr);
                        return new GeoResult(g, false, null);
                    }
                    return new GeoResult(new JsonObject { ["preset"] = "rectangle" }, false, null);
                }
                if (sig == GeometryMap.Signature(GeometryMap.ForPreset("ellipse"))) return new GeoResult(new JsonObject { ["preset"] = "ellipse" }, false, null);
                if (presetName == "svgPath" && u.TryGetValue("AgentGeomSig", out var stored) && stored == sig && u.TryGetValue("AgentSvgPath", out var original))
                    return new GeoResult(new JsonObject { ["svgPath"] = original }, false, null);
                // anything else: keep the outline as a restricted path, and say it was converted
                var path = GeometryMap.ToSvgPath(sections);
                if (path.Length == 0 || path.Length > 65536) { Note("unsupported_shape", "The outline of this shape is too complex; it is shown as a marked placeholder.", id, sid, "placeholder rectangle"); return new GeoResult(new JsonObject { ["preset"] = "rectangle" }, true, "outline too complex"); }
                Note("outline_converted", "The outline is not one of the editor's presets and was converted to a custom path.", id, sid, "custom path", "info");
                return new GeoResult(new JsonObject { ["svgPath"] = path }, false, null);
            }

            IReadOnlyList<GeoSection> ReadSections(XElement s, double wIn, double hIn, string elementId, string sid)
            {
                var result = new List<GeoSection>();
                var w = wIn > 0 ? wIn : 1; var h = hIn > 0 ? hIn : 1;
                foreach (var sec in s.Elements(V + "Section").Where(x => (string?)x.Attribute("N") == "Geometry"))
                {
                    if (sec.Elements(V + "Cell").Any(c => (string?)c.Attribute("N") == "NoShow" && (string?)c.Attribute("V") == "1") && !s.Elements(V + "Section").Where(x => (string?)x.Attribute("N") == "Geometry").Skip(1).Any()) { /* hidden outline still counts as geometry */ }
                    var noFill = sec.Elements(V + "Cell").Any(c => (string?)c.Attribute("N") == "NoFill" && (string?)c.Attribute("V") == "1");
                    var rows = new List<GeoRow>();
                    double lastX = 0, lastY = 0;
                    Frac Fr(double x, double y) => new(x / w, 1 - y / h);
                    foreach (var row in sec.Elements(V + "Row"))
                    {
                        if ((string?)row.Attribute("Del") == "1") continue;
                        var t = (string?)row.Attribute("T") ?? "";
                        double Cv(string n, double fb) => D(row.Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == n)?.Attribute("V")?.Value, fb);
                        switch (t)
                        {
                            case "MoveTo": lastX = Cv("X", lastX); lastY = Cv("Y", lastY); rows.Add(new MoveRow(Fr(lastX, lastY))); break;
                            case "LineTo": lastX = Cv("X", lastX); lastY = Cv("Y", lastY); rows.Add(new LineRow(Fr(lastX, lastY))); break;
                            case "RelMoveTo": lastX = Cv("X", 0) * w; lastY = Cv("Y", 0) * h; rows.Add(new MoveRow(Fr(lastX, lastY))); break;
                            case "RelLineTo": lastX = Cv("X", 0) * w; lastY = Cv("Y", 0) * h; rows.Add(new LineRow(Fr(lastX, lastY))); break;
                            case "Ellipse":
                            {
                                var cx = Cv("X", w / 2); var cy = Cv("Y", h / 2);
                                rows.Add(new EllipseRow(Fr(cx, cy).With(), Math.Abs(Cv("A", cx + w / 2) - cx) / w, Math.Abs(Cv("D", cy + h / 2) - cy) / h));
                                break;
                            }
                            case "ArcTo":
                            {
                                var x1 = Cv("X", lastX); var y1 = Cv("Y", lastY); var bulge = Cv("A", 0);
                                foreach (var p in FlattenArc(lastX, lastY, x1, y1, bulge)) rows.Add(new LineRow(Fr(p.X, p.Y)));
                                lastX = x1; lastY = y1;
                                break;
                            }
                            case "InfiniteLine": break;
                            default:
                                if (row.Elements(V + "Cell").Any(c => (string?)c.Attribute("N") == "X"))
                                {
                                    lastX = Cv("X", lastX); lastY = Cv("Y", lastY); rows.Add(new LineRow(Fr(lastX, lastY)));
                                    Note("geometry_approximated", $"A {t} segment was imported as a straight line.", elementId, sid, "approximated", "info");
                                }
                                break;
                        }
                    }
                    if (rows.Count > 0) result.Add(new GeoSection(rows, noFill));
                }
                return result;
            }

            /// <summary>Points along the circular arc from (x0,y0) to (x1,y1) whose midpoint lies `bulge` away from the chord (positive: to the right of travel).</summary>
            static IEnumerable<(double X, double Y)> FlattenArc(double x0, double y0, double x1, double y1, double bulge)
            {
                var chord = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                if (chord < 1e-9 || Math.Abs(bulge) < 1e-9) { yield return (x1, y1); yield break; }
                var radius = (chord * chord / 4 + bulge * bulge) / (2 * Math.Abs(bulge));
                var mx = (x0 + x1) / 2; var my = (y0 + y1) / 2;
                var nx = (y1 - y0) / chord; var ny = -(x1 - x0) / chord; // right-hand normal
                var cx = mx - nx * (radius - Math.Abs(bulge)) * Math.Sign(bulge); var cy = my - ny * (radius - Math.Abs(bulge)) * Math.Sign(bulge);
                var a0 = Math.Atan2(y0 - cy, x0 - cx); var a1 = Math.Atan2(y1 - cy, x1 - cx);
                var sweep = a1 - a0;
                if (bulge > 0) { while (sweep > 0) sweep -= 2 * Math.PI; } else { while (sweep < 0) sweep += 2 * Math.PI; }
                var steps = Math.Max(4, (int)(Math.Abs(sweep) / (Math.PI / 12)));
                for (var i = 1; i <= steps; i++) { var a = a0 + sweep * i / steps; yield return (cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)); }
            }

            // ------------------------------------------------------------ ports
            void ReadPorts(XElement s, string sid, Dictionary<string, string> u)
            {
                var rows = s.Elements(V + "Section").Where(x => (string?)x.Attribute("N") == "Connection").Elements(V + "Row").ToList();
                if (rows.Count == 0) return;
                var w = D(Own(s, "Width"), 1); var h = D(Own(s, "Height"), 1);
                var names = new Dictionary<int, string>();
                if (u.TryGetValue("AgentPorts", out var ap)) { try { foreach (var n in JsonNode.Parse(ap)!.AsArray()) names[n!["ix"]!.GetValue<int>()] = n["id"]!.GetValue<string>(); } catch (Exception e) when (e is JsonException or InvalidOperationException) { } }
                var list = new List<(string, double, double)>();
                for (var i = 0; i < rows.Count; i++)
                {
                    double Cv(string n, double fb) => D(rows[i].Elements(V + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == n)?.Attribute("V")?.Value, fb);
                    var ix = (int)D((string?)rows[i].Attribute("IX"), i);
                    list.Add((names.GetValueOrDefault(ix) ?? $"p{ix}", w > 0 ? Cv("X", 0) / w : 0, h > 0 ? 1 - Cv("Y", 0) / h : 0));
                }
                portsOf[sid] = list;
            }

            // ------------------------------------------------------------ pictures
            void ReadPicture(XElement s, string sid, Dictionary<string, string> u, Xf xf)
            {
                var id = idByShape[sid];
                var rid = (string?)s.Element(V + "ForeignData")?.Element(V + "Rel")?.Attribute(R + "id");
                var part = rels.FirstOrDefault(r => r.Id == rid)?.Target;
                if (part is null || !rd.pkg.Has(part)) { Placeholder(s, sid, u, xf, "a picture whose image is missing from the package"); return; }
                var bytes = rd.pkg.Read(part);
                if (!bytes.IsOk) { Placeholder(s, sid, u, xf, bytes.Error!.Message); return; }
                var mime = Sniff(bytes.Value);
                if (mime is null) { Placeholder(s, sid, u, xf, "a picture in a format that is not supported (only PNG, JPEG, BMP and SVG are)"); return; }
                var mediaSha = BlobStore.HashOf(bytes.Value);
                var intact = u.GetValueOrDefault("ImageOriginalSha256") == mediaSha;
                if (u.ContainsKey("ImageOriginalSha256") && !intact) Note("picture_replaced", "The picture's image was replaced in Visio; the new image is used and the record of the original is ignored.", id, sid, "new image adopted", "info");

                byte[] finalBytes = bytes.Value; var finalMime = mime; string? assetId = null, assetName = null; var tags = new JsonArray(); string? provenance = null;
                if (intact && u.GetValueOrDefault("AgentImageMime") == "image/svg+xml" && u.TryGetValue("SvgSource", out var svgText))
                {
                    var svgBytes = Encoding.UTF8.GetBytes(svgText);
                    if (u.TryGetValue("AgentAssetSha256", out var origSha) && BlobStore.HashOf(svgBytes) == origSha) { finalBytes = svgBytes; finalMime = "image/svg+xml"; }
                    else Note("svg_source_stale", "The stored SVG no longer matches the picture's record; the bitmap is used.", id, sid, "bitmap used", "info");
                }
                var approved = AssetPreparer.ApproveBytes(finalBytes, finalMime);
                if (!approved.IsOk) { Placeholder(s, sid, u, xf, approved.Error!.Message); return; }
                foreach (var n in approved.Value.Notes) Note("image_sanitised", n, id, sid, "sanitised", "info");
                var bytesOut = approved.Value.Bytes;
                var sha = BlobStore.HashOf(bytesOut);
                if (!rd.blobs.ContainsKey(sha)) rd.blobs[sha] = new ImportedBlob(sha, bytesOut, finalMime);
                if (!rd.assetsBySha.TryGetValue(sha, out var asset))
                {
                    if (intact && u.TryGetValue("AgentAssetSha256", out var recorded) && rd.catalogue.TryGetValue(recorded, out var cat))
                    {
                        assetId = cat["id"]?.GetValue<string>(); assetName = cat["name"]?.GetValue<string>(); provenance = cat["provenance"]?.GetValue<string>();
                        if (cat["tags"] is JsonArray ta) foreach (var t in ta) tags.Add(t!.GetValue<string>());
                    }
                    assetName ??= $"picture-{sha[..8]}";
                    assetId = assetId is { Length: > 0 and <= 256 } && !rd.assetsBySha.Values.Any(a => a["id"]!.GetValue<string>() == assetId) ? assetId : $"asset:{assetName}~{sha}";
                    asset = new JsonObject { ["id"] = assetId, ["name"] = Trim(assetName, 256), ["mimeType"] = finalMime, ["sha256"] = sha, ["tags"] = tags, ["provenance"] = provenance ?? "visio-import" };
                    rd.assetsBySha[sha] = asset;
                }

                var img = Base(s, sid, u, "image", xf);
                img["assetId"] = asset["id"]!.GetValue<string>();
                img["fit"] = "stretch"; img["opacity"] = 1; img["preserveAspectRatio"] = false;
                if (intact && u.TryGetValue("AgentFit", out var fit) && u.TryGetValue("AgentFrame", out var frame) && FrameMatches(img, xf, frame, fit, u))
                {
                    var f = frame.Split(',').Select(x => D(x)).ToArray();
                    img["bounds"] = new JsonObject { ["x"] = f[0], ["y"] = f[1], ["width"] = f[2], ["height"] = f[3] };
                    img["fit"] = fit; img["preserveAspectRatio"] = u.GetValueOrDefault("AgentPreserveAspect") != "0";
                }
                else if (intact && u.ContainsKey("AgentFit")) Note("picture_moved", "The picture was moved or resized in Visio, so its fit mode is now stretch.", id, sid, "fit reset", "info");
                if (intact && u.TryGetValue("AgentOpacity", out var op)) img["opacity"] = Math.Clamp(D(op, 1), 0, 1);
                Add(img);
            }

            /// <summary>The picture still sits where the exporter put it (within 0.05 pt), so the recorded frame and fit mode still describe it.</summary>
            bool FrameMatches(JsonObject img, Xf xf, string frame, string fit, Dictionary<string, string> u)
            {
                var f = frame.Split(',').Select(x => D(x, double.NaN)).ToArray();
                if (f.Length != 4 || f.Any(double.IsNaN)) return false;
                var b = (JsonObject)img["bounds"]!;
                double cx = b["x"]!.GetValue<double>() + b["width"]!.GetValue<double>() / 2, cy = b["y"]!.GetValue<double>() + b["height"]!.GetValue<double>() / 2;
                const double tol = 0.05;
                if (fit == "stretch" || u.GetValueOrDefault("AgentPreserveAspect") == "0" || fit == "cover") return Math.Abs(b["x"]!.GetValue<double>() - f[0]) < tol && Math.Abs(b["y"]!.GetValue<double>() - f[1]) < tol && Math.Abs(b["width"]!.GetValue<double>() - f[2]) < tol && Math.Abs(b["height"]!.GetValue<double>() - f[3]) < tol;
                // contain: the shape is the fitted box inside the frame, centred on it
                return Math.Abs(cx - (f[0] + f[2] / 2)) < tol && Math.Abs(cy - (f[1] + f[3] / 2)) < tol && b["width"]!.GetValue<double>() <= f[2] + tol && b["height"]!.GetValue<double>() <= f[3] + tol;
            }

            static string? Sniff(byte[] b)
            {
                if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
                if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
                if (b.Length > 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
                var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512));
                return head.Contains("<svg", StringComparison.Ordinal) ? "image/svg+xml" : null;
            }

            void Placeholder(XElement s, string sid, Dictionary<string, string> u, Xf xf, string why)
            {
                var id = idByShape[sid];
                Note("unsupported_shape", $"This shape is {why}; it is shown as a marked placeholder.", id, sid, "placeholder rectangle");
                var shape = Base(s, sid, u, "shape", xf);
                shape["geometry"] = new JsonObject { ["preset"] = "rectangle" };
                shape["style"] = new JsonObject { ["fill"] = new JsonObject { ["kind"] = "none" }, ["stroke"] = new JsonObject { ["kind"] = "solid", ["colour"] = "#b45309", ["opacity"] = 1 }, ["strokeWidthPt"] = 1, ["dash"] = "dashed", ["lineCap"] = "butt", ["lineJoin"] = "miter" };
                ((JsonObject)shape["metadata"]!)["visio.unsupported"] = why;
                Add(shape);
            }

            // ------------------------------------------------------------ connectors
            void ReadConnector(XElement s, string sid, Dictionary<string, string> u, Xf xf, List<XElement> connects)
            {
                var id = idByShape[sid];
                var el = Base(s, sid, u, "connector", xf);
                el["rotationDeg"] = 0.0;
                var mine = connects.Where(c => (string?)c.Attribute("FromSheet") == sid).ToList();
                JsonObject End(bool begin)
                {
                    var cx = begin ? "BeginX" : "EndX"; var cy = begin ? "BeginY" : "EndY";
                    var pagePt = PagePt(ToPage(xf.Parent ?? new Xf(0, 0, 0, 0, 0, 0, 0, null), D(Own(s, cx)), D(Own(s, cy))));
                    if (xf.Parent is null) pagePt = PagePt((D(Own(s, cx)), D(Own(s, cy))));
                    var c = mine.FirstOrDefault(x => (string?)x.Attribute("FromCell") == cx);
                    var to = (string?)c?.Attribute("ToSheet");
                    if (c is null || to is null || !idByShape.TryGetValue(to, out var targetId) || !elementKnownLater(to))
                    {
                        if (c is not null) Note("glue_target_missing", "A connector end is glued to a shape that was not imported; it is left free.", id, sid, "end left free");
                        return new JsonObject { ["glue"] = "none", ["point"] = new JsonObject { ["x"] = pagePt.X, ["y"] = pagePt.Y } };
                    }
                    var toCell = (string?)c.Attribute("ToCell") ?? ""; var toPart = (int)D((string?)c.Attribute("ToPart"), 0);
                    if (toCell.StartsWith("Connections.X", StringComparison.Ordinal) && int.TryParse(toCell["Connections.X".Length..], out var n))
                    {
                        var ix = n - 1;
                        var ports = portsOf.GetValueOrDefault(to);
                        var portId = ports is not null && ix >= 0 && ix < ports.Count ? ports[ix].Id : null;
                        if (portId is not null) return new JsonObject { ["elementId"] = targetId, ["port"] = portId, ["glue"] = "static" };
                        Note("glue_port_unknown", "A connector is glued to a connection point that could not be found; it is glued to the shape instead.", id, sid, "glue made dynamic");
                    }
                    _ = toPart;
                    return new JsonObject { ["elementId"] = targetId, ["glue"] = "dynamic" };
                }
                el["from"] = End(true); el["to"] = End(false);
                var ext = (int)D(Own(s, "ConLineRouteExt"), 0); var style = (int)D(Own(s, "ShapeRouteStyle"), 1);
                el["route"] = ext == 2 ? "curved" : ext == 1 || style == 2 ? "straight" : "orthogonal";
                var way = new JsonArray();
                if (u.TryGetValue("AgentWaypoints", out var wp)) { try { foreach (var n in JsonNode.Parse(wp)!.AsArray()) way.Add(new JsonObject { ["x"] = n!["x"]!.GetValue<double>(), ["y"] = n["y"]!.GetValue<double>() }); } catch (Exception e) when (e is JsonException or InvalidOperationException) { } }
                el["waypoints"] = way;
                var pattern = (int)D(rd.Line(s, "LinePattern"), 1);
                el["style"] = new JsonObject
                {
                    ["colour"] = rd.Colour(rd.Line(s, "LineColor"), "#000000", id, pageId), ["widthPt"] = Math.Max(0, D(rd.Line(s, "LineWeight"), 0.01) * PtPerInch), ["dash"] = DashOf(pattern == 0 ? 1 : pattern),
                    ["startArrow"] = ArrowName(rd.Line(s, "BeginArrow"), id, sid), ["endArrow"] = ArrowName(rd.Line(s, "EndArrow"), id, sid),
                };
                var label = ReadText(s, u, id);
                if (label is not null) el["label"] = label;
                el["bounds"] = ConnectorBounds(el);
                connectorEls.Add(el);
                Add(el);
            }

            bool elementKnownLater(string shapeId) => shapeById.ContainsKey(shapeId) && (string?)shapeById[shapeId].Attribute("Type") != "Guide" && !IsConnector(shapeById[shapeId]);

            string ArrowName(string? v, string id, string sid)
            {
                var code = (int)D(v, 0);
                var a = ArrowOf(code);
                if (a is null) { Note("arrow_approximated", $"Arrow style {code} is not supported and was dropped.", id, sid, "arrow dropped", "info"); return "none"; }
                return a;
            }

            JsonObject ConnectorBounds(JsonObject el)
            {
                var pts = new List<(double X, double Y)>();
                foreach (var key in new[] { "from", "to" })
                {
                    var ep = (JsonObject)el[key]!;
                    if (ep["point"] is JsonObject p) pts.Add((p["x"]!.GetValue<double>(), p["y"]!.GetValue<double>()));
                    else if (ep["elementId"] is JsonNode eid && elementById.TryGetValue(eid.GetValue<string>(), out var t)) { var b = (JsonObject)t["bounds"]!; pts.Add((b["x"]!.GetValue<double>() + b["width"]!.GetValue<double>() / 2, b["y"]!.GetValue<double>() + b["height"]!.GetValue<double>() / 2)); }
                }
                foreach (var w in (JsonArray)el["waypoints"]!) pts.Add((w!["x"]!.GetValue<double>(), w["y"]!.GetValue<double>()));
                if (pts.Count == 0) return new JsonObject { ["x"] = 0.0, ["y"] = 0.0, ["width"] = 0.0, ["height"] = 0.0 };
                var x0 = pts.Min(p => p.X); var y0 = pts.Min(p => p.Y);
                return new JsonObject { ["x"] = x0, ["y"] = y0, ["width"] = pts.Max(p => p.X) - x0, ["height"] = pts.Max(p => p.Y) - y0 };
            }

            // ------------------------------------------------------------ groups
            void ReadGroup(XElement s, string sid, Dictionary<string, string> u, Xf xf, List<XElement> connects)
            {
                var el = Base(s, sid, u, "group", xf);
                el["rotationDeg"] = 0.0;
                el["childIds"] = new JsonArray();
                var childIds = (JsonArray)el["childIds"]!;
                var before = elements.Count;
                elementById[el["id"]!.GetValue<string>()] = el;
                var placeholderIndex = elements.Count;
                elements.Add(el); // keeps the group ahead of its members; its stacking is settled once they are known
                foreach (var c in s.Element(V + "Shapes")?.Elements(V + "Shape") ?? [])
                {
                    var cid = idByShape[(string?)c.Attribute("ID") ?? ""];
                    Walk(c, xf, connects);
                    if (elementById.ContainsKey(cid)) childIds.Add(cid);
                }
                _ = before; _ = placeholderIndex;
                groups.Add((el, s, (u.GetValueOrDefault("AgentConnectorMembers") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)));
                if (childIds.Count == 0)
                {
                    elements.Remove(el); elementById.Remove(el["id"]!.GetValue<string>()); groups.RemoveAll(g => g.Group == el);
                    Note("empty_group_dropped", "A group with no importable members was dropped.", el["id"]!.GetValue<string>(), sid, "group dropped", "info");
                }
            }

            void FinishGroup(JsonObject g)
            {
                var kids = ((JsonArray)g["childIds"]!).Select(n => elementById[n!.GetValue<string>()]).ToList();
                if (kids.Count == 0) return;
                var boxes = new List<(double X0, double Y0, double X1, double Y1)>();
                foreach (var k in kids)
                {
                    var b = (JsonObject)k["bounds"]!;
                    double x = b["x"]!.GetValue<double>(), y = b["y"]!.GetValue<double>(), w = b["width"]!.GetValue<double>(), h = b["height"]!.GetValue<double>();
                    var rot = (k["rotationDeg"]?.GetValue<double>() ?? 0) * Math.PI / 180;
                    if (rot == 0 || k["kind"]!.GetValue<string>() is "connector" or "group") { boxes.Add((x, y, x + w, y + h)); continue; }
                    var cx = x + w / 2; var cy = y + h / 2;
                    var xs = new List<double>(); var ys = new List<double>();
                    foreach (var (px, py) in new[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h) })
                    {
                        var dx = px - cx; var dy = py - cy;
                        xs.Add(cx + dx * Math.Cos(rot) - dy * Math.Sin(rot)); ys.Add(cy + dx * Math.Sin(rot) + dy * Math.Cos(rot));
                    }
                    boxes.Add((xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                }
                g["bounds"] = new JsonObject { ["x"] = boxes.Min(b => b.X0), ["y"] = boxes.Min(b => b.Y0), ["width"] = boxes.Max(b => b.X1) - boxes.Min(b => b.X0), ["height"] = boxes.Max(b => b.Y1) - boxes.Min(b => b.Y0) };
                g["zIndex"] = kids.Max(k => k["zIndex"]!.GetValue<int>());
            }

            // ------------------------------------------------------------ guides
            void ReadGuide(XElement s, string sid)
            {
                var angle = D(Own(s, "Angle"), 0);
                var vertical = Math.Abs(Math.Abs(Math.Sin(angle)) - 1) < 0.01;
                var pos = vertical ? D(Own(s, "PinX")) * PtPerInch : pageHeightPt - D(Own(s, "PinY")) * PtPerInch;
                guides.Add(new JsonObject { ["id"] = idByShape[sid], ["orientation"] = vertical ? "vertical" : "horizontal", ["positionPt"] = pos });
            }
        }

        bool catalogueSeenAlias(string pageId, string alias, HashSet<string> seen) => seen.Add(alias);
    }
}

static class FracExt
{
    public static Frac With(this Frac f) => f;
}
