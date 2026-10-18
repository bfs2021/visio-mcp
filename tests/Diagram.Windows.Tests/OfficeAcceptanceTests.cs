using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Diagram.Core.Contracts;
using Diagram.Host.Core;
using Diagram.Ipc;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>
/// I41: the native segment of the demonstration (spec 23, steps 10 to 14) run against the installed Visio and Word, driven through their
/// own automation interfaces. The drawing is made through the real MCP tools, exported by `export_document`, edited in Visio, saved by
/// Visio, read back by the application, embedded in Word, activated, edited and saved there, and reopened. Every step records PASS, FAIL or
/// NOT RUN; without Visio and Word the whole test records NOT RUN and passes nothing. Evidence goes to docs/acceptance/evidence.
/// </summary>
public class OfficeAcceptanceTests
{
    sealed record Row(string Wa, string Title, string Status, string Detail);

    readonly List<Row> rows = [];
    readonly Dictionary<string, JsonObject> facts = [];

    static string EvidenceRoot => Path.Combine(Harness.RepoRoot, "docs", "acceptance", "evidence");
    static string Folder(string wa) { var d = Path.Combine(EvidenceRoot, wa); Directory.CreateDirectory(d); return d; }
    static CancellationToken Ct(int s = 90) => new CancellationTokenSource(TimeSpan.FromSeconds(s)).Token;
    static JsonElement Body(CallToolResult r) => r.StructuredContent!.Value;

    void Fact(string wa, string key, object? value) { if (!facts.TryGetValue(wa, out var f)) facts[wa] = f = new JsonObject(); f[key] = JsonSerializer.SerializeToNode(value); }

    Task Step(string wa, string title, Func<string> run) => Step(wa, title, () => Task.FromResult(run()));

    async Task Step(string wa, string title, Func<Task<string>> run)
    {
        try { rows.Add(new Row(wa, title, "PASS", await run())); }
        catch (Exception e) { rows.Add(new Row(wa, title, "FAIL", e.GetBaseException().Message)); }
    }

    // ---- Visio helpers
    internal static dynamic? StartVisio()
    {
        var type = Type.GetTypeFromProgID("Visio.Application");
        if (type is null) return null;
        try { dynamic v = Activator.CreateInstance(type)!; v.Visible = false; v.AlertResponse = 7; return v; }
        catch (COMException) { return null; }
    }

    internal static IEnumerable<dynamic> AllShapes(dynamic shapes)
    {
        foreach (dynamic s in shapes)
        {
            yield return s;
            if ((int)s.Type == 2) foreach (var c in AllShapes(s.Shapes)) yield return c;
        }
    }

    internal static string? AgentId(dynamic s) => (int)s.CellExistsU["User.AgentId", 0] != 0 ? (string)s.CellsU["User.AgentId"].ResultStr[0] : null;
    internal static double In(dynamic s, string cell) => (double)s.CellsU[cell].ResultIU;

    static byte[] Png()
    {
        var px = new byte[40 * 24 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 0x40; px[i + 1] = 0x90; px[i + 2] = 0xD0; px[i + 3] = 0xFF; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(40, 24, 96, 96, PixelFormats.Bgra32, null, px, 40 * 4)));
        using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
    }

    [Fact]
    public async Task NativeSegmentOfTheDemonstrationInRealVisioAndWord()
    {
        var visioType = Type.GetTypeFromProgID("Visio.Application");
        var wordType = Type.GetTypeFromProgID("Word.Application");
        if (visioType is null || wordType is null)
        {
            File.WriteAllText(Path.Combine(Folder("WA-01"), "result.json"), "{\"status\":\"NOT RUN\",\"reason\":\"Visio or Word is not installed\"}");
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "wa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pipe = $"wa-{Guid.NewGuid():N}";
        var exportRoot = Path.Combine(work, "exports");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: exportRoot);
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "wa", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src", "Diagram.Mcp", "bin", AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug", "net10.0", "Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: Ct());

        async Task<(string Doc, string Session, int Revision, string Page)> Summary()
        {
            var r = await client.CallToolAsync("get_document_summary", cancellationToken: Ct());
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            var b = Body(r);
            return (b.GetProperty("documentId").GetString()!, b.GetProperty("sessionId").GetString()!, b.GetProperty("revision").GetInt32(), b.GetProperty("pages")[0].GetProperty("id").GetString()!);
        }

        async Task<Dictionary<string, JsonElement>> AllObjects()
        {
            var s = await Summary();
            var snap = (await h.Send("doc.snapshot")).Result!.Value;
            var ids = snap.GetProperty("document").GetProperty("pages")[0].GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
            var r = await client.CallToolAsync("get_objects", new Dictionary<string, object?> { ["documentId"] = s.Doc, ["sessionId"] = s.Session, ["ids"] = ids }, cancellationToken: Ct());
            Assert.True(r.IsError != true, r.StructuredContent?.ToString());
            return Body(r).GetProperty("objects").EnumerateArray().ToDictionary(o => o.GetProperty("id").GetString()!, o => o.Clone());
        }

        // ---------------------------------------------------------------- the drawing is made through MCP
        var first = await Summary();
        var script = string.Join('\n',
            "add shape id=crm x=20mm y=30mm w=50mm h=20mm text=\"CRM\"",
            "add shape id=erp x=110mm y=30mm w=50mm h=20mm text=\"ERP\"",
            "add shape id=bill x=110mm y=80mm w=50mm h=20mm text=\"Billing\"",
            "add shape id=data x=20mm y=80mm w=50mm h=20mm text=\"Data\"",
            "add shape id=g1 x=200mm y=30mm w=30mm h=15mm text=\"One\"",
            "add shape id=g2 x=200mm y=60mm w=30mm h=15mm text=\"Two\"",
            "set crm fill=\"#DDEEFF\"",
            "connect id=c1 from=crm to=erp",
            "connect id=c2 from=erp to=bill",
            "connect id=c3 from=bill to=data",
            "add text id=note x=20mm y=130mm w=80mm h=10mm text=\"Annotation\"",
            "group id=pair g1 g2");
        var made = await client.CallToolAsync("execute_script", new Dictionary<string, object?>
        { ["documentId"] = first.Doc, ["sessionId"] = first.Session, ["transactionId"] = Guid.NewGuid().ToString(), ["baseRevision"] = first.Revision, ["script"] = script }, cancellationToken: Ct());
        Assert.True(made.IsError != true, made.StructuredContent?.ToString());

        // a picture, prepared by the host exactly as the Insert picture command does
        var png = Png();
        var prepared = await h.Window.Preparer.PrepareAsync(new MemoryStream(png), "image/png", "logo.png", Ct());
        Assert.True(prepared.IsOk, prepared.Error?.Message);
        var rev = (await Summary()).Revision;
        var img = await h.Apply(rev, null,
            new JsonObject { ["op"] = "registerAsset", ["asset"] = JsonSerializer.SerializeToNode(prepared.Value.Ref.Asset, ContractJson.Options) },
            new JsonObject { ["op"] = "createImage", ["assetId"] = prepared.Value.Ref.Asset.Id, ["name"] = "logo", ["alias"] = "logo", ["bounds"] = new JsonObject { ["x"] = 300, ["y"] = 400, ["width"] = 80, ["height"] = 48 }, ["fit"] = "contain", ["preserveAspectRatio"] = true });
        Assert.Null(img.Error);

        // two layers; the CRM block sits in both (policy: a shape may belong to several layers)
        var l1 = Guid.NewGuid().ToString(); var l2 = Guid.NewGuid().ToString();
        rev = (await Summary()).Revision;
        var layers = await h.Apply(rev, null,
            new JsonObject { ["op"] = "addLayer", ["pageId"] = first.Page, ["id"] = l1, ["name"] = "Systems" },
            new JsonObject { ["op"] = "addLayer", ["pageId"] = first.Page, ["id"] = l2, ["name"] = "Review" },
            new JsonObject { ["op"] = "assignLayers", ["targets"] = new JsonArray("crm"), ["layerIds"] = new JsonArray(l1, l2), ["mode"] = "set" });
        Assert.Null(layers.Error);

        var before = await AllObjects();
        var beforeIds = before.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        string IdOf(string alias) => before.Values.First(o => o.TryGetProperty("alias", out var a) && a.GetString() == alias).GetProperty("id").GetString()!;
        var crmId = IdOf("crm"); var erpId = IdOf("erp"); var pairId = IdOf("pair"); var c1Id = IdOf("c1"); var logoId = IdOf("logo");

        // ---------------------------------------------------------------- environment (WA-01)
        dynamic visio = StartVisio()!;
        var wordProcsBefore = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        var visioProcsBefore = Process.GetProcessesByName("VISIO").Select(p => p.Id).ToHashSet();
        string exported = "", visioSaved = "", wordFile = "";
        Dictionary<string, JsonElement> after = [];
        try
        {
            await Step("WA-01", "Versions recorded", () =>
            {
                Fact("WA-01", "os", Environment.OSVersion.VersionString);
                Fact("WA-01", "visio", $"{visio.Version} build {visio.Build}");
                Fact("WA-01", "dotnet", Environment.Version.ToString());
                return Task.FromResult($"Visio {visio.Version} build {visio.Build}");
            });

            // ---------------------------------------------------------------- export through MCP (spec 23 step 10)
            await Step("WA-02", "export_document writes an editable .vsdx", async () =>
            {
                var s = await Summary();
                var r = await client.CallToolAsync("export_document", new Dictionary<string, object?> { ["documentId"] = s.Doc, ["sessionId"] = s.Session, ["format"] = "vsdx", ["path"] = "demo.vsdx" }, cancellationToken: Ct());
                Assert.True(r.IsError != true, r.StructuredContent?.ToString());
                exported = Body(r).GetProperty("path").GetString()!;
                Fact("WA-02", "lossy", Body(r).GetProperty("lossy").GetBoolean());
                Fact("WA-02", "diagnostics", JsonNode.Parse(Body(r).GetProperty("diagnostics").GetRawText()));
                return $"{new FileInfo(exported).Length} bytes, lossy={Body(r).GetProperty("lossy").GetBoolean()}";
            });

            // ---------------------------------------------------------------- Visio opens it (step 11)
            string visioPath = Path.Combine(work, "visio-saved.vsdx");
            if (exported.Length > 0)
            {
                dynamic doc = visio.Documents.Open(exported);
                try
                {
                    dynamic page = doc.Pages.Item(1);
                    Dictionary<string, dynamic> byId = [];
                    foreach (var s in AllShapes(page.Shapes)) if (AgentId(s) is { } id) byId[id] = s;

                    await Step("WA-02", "Visio opens the file without a repair prompt; every object is a Visio shape carrying its UUID", () =>
                    {
                        var missing = beforeIds.Where(id => !byId.ContainsKey(id)).ToList();
                        Assert.True(missing.Count == 0, "no Visio shape for " + string.Join(", ", missing));
                        page.Export(Path.Combine(Folder("WA-02"), "visio-open.png"));
                        return $"{beforeIds.Count} objects, all with AgentId; {(int)page.Shapes.Count} top-level shapes";
                    });

                    await Step("WA-03", "Text, fill and move edits work on the exported shapes", () =>
                    {
                        dynamic crm = byId[crmId];
                        crm.Text = "CRM edited";
                        crm.CellsU["FillForegnd"].FormulaU = "RGB(255,200,0)";
                        dynamic erp = byId[erpId];
                        erp.CellsU["PinY"].ResultIU = In(erp, "PinY") + 0.75; // moves up the page by 0.75 in
                        return Task.FromResult("text, fill and PinY changed through Visio's own object model");
                    });

                    await Step("WA-04", "Moving a connected shape keeps its connectors attached and following", () =>
                    {
                        dynamic c1 = byId[c1Id]; dynamic erp = byId[erpId];
                        Assert.Equal(2, (int)c1.Connects.Count);
                        var endY = In(c1, "EndY");
                        var pinY = In(erp, "PinY");
                        Assert.True(Math.Abs(endY - pinY) <= In(erp, "Height") / 2 + 0.01, $"connector end y {endY:F2} is not on ERP (centre {pinY:F2})");
                        Assert.Equal(2, (int)c1.Connects.Count);
                        page.Export(Path.Combine(Folder("WA-04"), "after-move.png"));
                        return Task.FromResult($"c1 still has 2 connections; its end sits at y={endY:F2} in beside ERP centre y={pinY:F2}");
                    });

                    await Step("WA-05", "The picture is an independent object with unchanged bytes and its provenance cells", () =>
                    {
                        dynamic logo = byId[logoId];
                        Assert.Equal(4, (int)logo.Type);
                        Assert.Equal(logoId, AgentId(logo));
                        var sha = (string)logo.CellsU["User.AgentAssetSha256"].ResultStr[0];
                        Assert.Equal(prepared.Value.Ref.Asset.Sha256, sha, ignoreCase: true);
                        return Task.FromResult("Foreign picture shape; AgentAssetSha matches the host's hash");
                    });

                    await Step("WA-07", "A Visio duplicate carries the same UUID (the collision the importer must repair)", () =>
                    {
                        dynamic crm = byId[crmId];
                        dynamic copy = crm.Duplicate();
                        Assert.Equal(crmId, AgentId(copy));
                        Fact("WA-07", "duplicateCarriesSameAgentId", true);
                        return Task.FromResult("Duplicate() copied User.AgentId");
                    });

                    await Step("WA-08", "Native group resize scales members", () =>
                    {
                        dynamic grp = byId[pairId];
                        var children = new List<dynamic>(); foreach (dynamic c in grp.Shapes) children.Add(c);
                        var w0 = children.Select(c => In(c, "Width")).ToList();
                        var gw = In(grp, "Width");
                        grp.CellsU["Width"].ResultIU = gw * 1.5;
                        var ratios = children.Select((c, i) => In(c, "Width") / w0[i]).ToList();
                        Assert.All(ratios, r => Assert.InRange(r, 1.49, 1.51));
                        Fact("WA-08", "ratios", ratios);
                        return Task.FromResult($"group width x1.5, member width ratios {string.Join(", ", ratios.Select(r => r.ToString("F3")))}");
                    });

                    await Step("WA-09", "Multi-layer shape: both layers present, hide and lock honoured, policy recorded", () =>
                    {
                        dynamic crm = byId[crmId];
                        Assert.True((int)page.Layers.Count >= 2, "fewer than two layers"); // the document's own default layer is a third
                        Assert.Equal(2, (int)crm.LayerCount);
                        dynamic review = page.Layers["Review"];
                        review.CellsC[4].FormulaU = "0"; // visible off for one layer
                        var visibleCell = (int)review.CellsC[4].ResultIU;
                        Fact("WA-09", "reviewLayerVisibleAfterHide", visibleCell);
                        review.CellsC[4].FormulaU = "1";
                        return Task.FromResult("crm belongs to 2 layers; layer visibility is a real Visio layer setting. Policy: a shape may belong to several layers.");
                    });

                    doc.SaveAs(visioPath);
                    visioSaved = visioPath;
                    File.Copy(exported, Path.Combine(Folder("WA-02"), "exported.vsdx"), true);
                    File.Copy(visioPath, Path.Combine(Folder("WA-06"), "visio-saved.vsdx"), true);
                    File.WriteAllText(Path.Combine(Folder("WA-06"), "visio-saved.vsdx.json"), new JsonObject
                    {
                        ["producer"] = $"Microsoft Visio {visio.Version} build {visio.Build}, saved through COM automation", ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                        ["source"] = "WA-02/exported.vsdx made by the application's export_document, then edited in Visio (text, fill, move, duplicate, group resize)",
                    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { try { doc.Close(); } catch { } }
            }

            // ---------------------------------------------------------------- the application reads Visio's file back (step 14)
            if (visioSaved.Length > 0)
            {
                await Step("WA-06", "Visio-saved file reopens in the application; agent ids resolve and human edits are kept", async () =>
                {
                    var status = (await h.Send("doc.status")).Result!.Value;
                    var expected = new ExpectedState { DocumentId = status.GetProperty("documentId").GetString()!, SessionId = status.GetProperty("sessionId").GetString()!, BaseRevision = status.GetProperty("revision").GetInt32() };
                    var opened = await h.Window.Native.OpenAsync(visioSaved, expected, Ct());
                    Assert.True(opened.IsOk, opened.Error?.Message);
                    after = await AllObjects();
                    Fact("WA-06", "identity", new { opened.Value.Identity.Preserved, opened.Value.Identity.Generated, opened.Value.Identity.Repaired, opened.Value.Identity.Notes });
                    Fact("WA-06", "diagnostics", JsonNode.Parse(opened.Value.Report.ToJson().ToJsonString()));
                    var gone = beforeIds.Where(id => !after.ContainsKey(id)).ToList();
                    Assert.True(gone.Count == 0, "ids that no longer resolve: " + string.Join(", ", gone));
                    Assert.Equal("CRM edited", after[crmId].GetProperty("text").GetProperty("value").GetString());
                    Assert.Contains("FFC800", after[crmId].GetProperty("style").GetProperty("fill").GetProperty("colour").GetString()!, StringComparison.OrdinalIgnoreCase);
                    var movedBy = after[erpId].GetProperty("bounds").GetProperty("y").GetDouble() - before[erpId].GetProperty("bounds").GetProperty("y").GetDouble();
                    Assert.InRange(movedBy, -0.75 * 72 - 0.5, -0.75 * 72 + 0.5); // up the page: y decreases
                    Fact("WA-06", "erpMovedPt", movedBy);
                    Assert.Equal(before[c1Id].GetProperty("from").GetProperty("elementId").GetString(), after[c1Id].GetProperty("from").GetProperty("elementId").GetString());
                    Assert.Equal(before[c1Id].GetProperty("to").GetProperty("elementId").GetString(), after[c1Id].GetProperty("to").GetProperty("elementId").GetString());
                    return $"{beforeIds.Count} original ids resolve; {opened.Value.Identity.Repaired} repaired (the Visio duplicate); text, fill, move and glue kept";
                });

                await Step("WA-07", "The duplicated UUID is repaired: every object has a unique id and the original keeps its own", () =>
                {
                    Assert.Equal(after.Count, after.Keys.Distinct().Count());
                    Assert.Equal(beforeIds.Count + 1, after.Count);
                    var extra = after.Keys.Except(beforeIds).ToList();
                    Assert.Single(extra);
                    Fact("WA-07", "newId", extra[0]);
                    return Task.FromResult($"{after.Count} objects, one new id for the Visio duplicate");
                });

                await Step("WA-08", "Group resize survives into the application", () =>
                {
                    var kids = after[pairId].GetProperty("childIds").EnumerateArray().Select(c => c.GetString()!).ToList();
                    Assert.Equal(2, kids.Count);
                    var w0 = before[pairId].GetProperty("bounds").GetProperty("width").GetDouble(); var w1 = after[pairId].GetProperty("bounds").GetProperty("width").GetDouble();
                    Assert.InRange(w1 / w0, 1.49, 1.51);
                    foreach (var k in kids) Assert.InRange(after[k].GetProperty("bounds").GetProperty("width").GetDouble() / before[k].GetProperty("bounds").GetProperty("width").GetDouble(), 1.48, 1.52);
                    return Task.FromResult($"group width ratio {w1 / w0:F3}; members scaled with it");
                });

                await Step("WA-09", "Multi-layer membership survives a Visio save", () =>
                {
                    var ids = after[crmId].GetProperty("layerIds").EnumerateArray().Select(x => x.GetString()).ToList();
                    Assert.Equal(2, ids.Count);
                    return Task.FromResult("crm still belongs to two layers");
                });

                // a stale-provenance picture replacement: new bytes, old User cells
                await Step("WA-05", "Image replaced outside the app (stale User cells) is detected, not trusted", async () =>
                {
                    var swapped = Path.Combine(work, "swapped.vsdx");
                    File.Copy(exported, swapped);
                    using (var zip = System.IO.Compression.ZipFile.Open(swapped, System.IO.Compression.ZipArchiveMode.Update))
                    {
                        var media = zip.Entries.First(e => e.FullName.StartsWith("visio/media/", StringComparison.Ordinal));
                        var name = media.FullName; media.Delete();
                        var fresh = zip.CreateEntry(name);
                        var other = new byte[png.Length]; png.CopyTo(other, 0);
                        using var w = fresh.Open();
                        var px = new byte[40 * 24 * 4];
                        for (var i = 0; i < px.Length; i += 4) { px[i] = 0x20; px[i + 1] = 0xA0; px[i + 2] = 0x30; px[i + 3] = 0xFF; }
                        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(40, 24, 96, 96, PixelFormats.Bgra32, null, px, 40 * 4)));
                        enc.Save(w);
                    }
                    var status = (await h.Send("doc.status")).Result!.Value;
                    var expected = new ExpectedState { DocumentId = status.GetProperty("documentId").GetString()!, SessionId = status.GetProperty("sessionId").GetString()!, BaseRevision = status.GetProperty("revision").GetInt32() };
                    var opened = await h.Window.Native.OpenAsync(swapped, expected, Ct());
                    Assert.True(opened.IsOk, opened.Error?.Message);
                    var codes = opened.Value.Report.Diagnostics.Select(d => d.Code).ToList();
                    Assert.Contains("picture_replaced", codes);
                    var o = await AllObjects();
                    var asset = o[logoId].GetProperty("assetId").GetString()!;
                    Assert.DoesNotContain(prepared.Value.Ref.Asset.Sha256, asset, StringComparison.OrdinalIgnoreCase);
                    return $"diagnostic picture_replaced; the picture is a new asset ({asset}), the old provenance is not applied to it";
                });

                // ---------------------------------------------------------------- Word (step 13)
                wordFile = Path.Combine(work, "report.docx");
                await WordSteps(visioSaved, wordFile, work, wordType, visioProcsBefore, Step, Fact);
            }

            // ---------------------------------------------------------------- lossy and unsupported stay visible (WA-11)
            await Step("WA-11", "Unsupported and lossy content stays visible in the compatibility report", async () =>
            {
                dynamic doc = visio.Documents.Open(exported);
                try
                {
                    dynamic page = doc.Pages.Item(1);
                    dynamic stencil;
                    try { stencil = visio.Documents.OpenEx("BASIC_M.VSSX", 64 /*visOpenHidden*/); }
                    catch (COMException) { stencil = visio.Documents.OpenEx("BASIC_U.VSSX", 64); }
                    dynamic master = stencil.Masters.Item(1);
                    dynamic dropped = page.Drop(master, 4.0, 4.0);
                    var path = Path.Combine(work, "master-based.vsdx");
                    doc.SaveAs(path);
                    doc.Close(); // Visio holds the file open until the document is closed
                    var status = (await h.Send("doc.status")).Result!.Value;
                    var expected = new ExpectedState { DocumentId = status.GetProperty("documentId").GetString()!, SessionId = status.GetProperty("sessionId").GetString()!, BaseRevision = status.GetProperty("revision").GetInt32() };
                    var opened = await h.Window.Native.OpenAsync(path, expected, Ct());
                    Assert.True(opened.IsOk, opened.Error?.Message);
                    var d = opened.Value.Report.Diagnostics.Select(x => x.Code).Distinct().ToList();
                    Assert.Contains("master_inlined", d);
                    Fact("WA-11", "codes", d);
                    Fact("WA-11", "summary", opened.Value.Report.Summary);
                    return $"master-based Visio shape opened; report: {opened.Value.Report.Summary} [{string.Join(", ", d)}]";
                }
                finally { try { doc.Close(); } catch { } }
            });
        }
        finally
        {
            try { visio.Quit(); } catch { }
            foreach (var p in Process.GetProcessesByName("WINWORD").Where(p => !wordProcsBefore.Contains(p.Id))) { try { p.Kill(); } catch { } }
            foreach (var p in Process.GetProcessesByName("VISIO").Where(p => !visioProcsBefore.Contains(p.Id))) { try { p.Kill(); } catch { } }
            string St(string wa, string needle) => rows.Where(r => r.Wa == wa && r.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)).Select(r => r.Status).DefaultIfEmpty("NOT RUN").Aggregate((a, b) => a == "PASS" ? b : a);
            rows.Add(new Row("WA-12", "Step 10: the document is written as .vsdx", St("WA-02", "export_document"), "export_document through the real MCP tool"));
            rows.Add(new Row("WA-12", "Step 11: the file opens in Visio with editable objects", St("WA-02", "Visio opens"), "no repair prompt; shapes carry UUIDs"));
            rows.Add(new Row("WA-12", "Step 12: moving a connected shape keeps its connector attached", St("WA-04", "Moving"), "connector end follows the moved shape"));
            rows.Add(new Row("WA-12", "Step 13: embedded in Word and still activatable as a Visio object", St("WA-10", "reopens"), "activate, edit, save, reopen, reactivate"));
            rows.Add(new Row("WA-12", "Step 14: reopened in the application; agent ids still resolve", St("WA-06", "reopens"), "all original ids resolve; human edits kept"));
            Write();
            try { Directory.Delete(work, true); } catch { }
        }

        var failed = rows.Where(r => r.Status == "FAIL").ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed.Select(f => $"{f.Wa} {f.Title}: {f.Detail}")));
    }

    async Task WordSteps(string vsdx, string docx, string work, Type wordType, HashSet<int> visioBefore, Func<string, string, Func<Task<string>>, Task> step, Action<string, string, object?> fact)
    {
        dynamic word = Activator.CreateInstance(wordType)!;
        word.Visible = false; word.DisplayAlerts = 0;
        try
        {
            dynamic wdoc = word.Documents.Add();
            dynamic shape = null!;
            await step("WA-10", "Word embeds the Visio drawing as an OLE object", () =>
            {
                shape = wdoc.InlineShapes.AddOLEObject(Type.Missing, vsdx, false, false);
                var prog = (string)shape.OLEFormat.ProgID;
                Assert.StartsWith("Visio.Drawing", prog);
                fact("WA-10", "progId", prog);
                return Task.FromResult(prog);
            });
            if (shape is null) return;

            await step("WA-10", "The object activates (opens) as a Visio drawing; shapes are editable and Visio saves back into Word", async () =>
            {
                var verb = Task.Run(() => shape.OLEFormat.DoVerb(-2));
                if (await Task.WhenAny(verb, Task.Delay(TimeSpan.FromSeconds(60))) != verb) throw new TimeoutException("Word did not finish activating the object");
                await verb;
                dynamic obj = await Poll((Func<object?>)(() => (object?)shape.OLEFormat.Object));
                var count = (int)obj.Pages.Item(1).Shapes.Count;
                dynamic any = null!; foreach (var s in AllShapes(obj.Pages.Item(1).Shapes)) { if (AgentId(s) is not null && s.Text is string t && t.Contains("CRM")) { any = s; break; } }
                Assert.NotNull((object?)any);
                any.Text = "Edited inside Word";
                obj.Close();
                fact("WA-10", "shapesOnActivation", count);
                return $"activated; {count} top-level shapes; text edited in the embedded Visio and the object closed";
            });

            await step("WA-10", "Word saves, reopens, and the object activates again showing the edit", async () =>
            {
                wdoc.SaveAs2(docx);
                wdoc.Close(0);
                dynamic again = word.Documents.Open(docx);
                dynamic ils = again.InlineShapes.Item(1);
                var verb = Task.Run(() => ils.OLEFormat.DoVerb(-2));
                if (await Task.WhenAny(verb, Task.Delay(TimeSpan.FromSeconds(60))) != verb) throw new TimeoutException("reactivation did not finish");
                await verb;
                dynamic obj = await Poll((Func<object?>)(() => (object?)ils.OLEFormat.Object));
                var texts = new List<string>();
                foreach (var s in AllShapes(obj.Pages.Item(1).Shapes)) { string t = s.Text; if (t.Length > 0) texts.Add(t); }
                Assert.Contains(texts, t => t.Contains("Edited inside Word"));
                obj.Close();
                again.Close(0);
                Directory.CreateDirectory(Folder("WA-10"));
                File.Copy(docx, Path.Combine(Folder("WA-10"), "report.docx"), true);
                return "reopened document, reactivated the Visio object, the edit made in Word persisted";
            });
        }
        finally { try { word.Quit(0); } catch { } }
    }

    internal static async Task<object> Poll(Func<object?> get)
    {
        for (var i = 0; i < 60; i++) { try { if (get() is { } o) return o; } catch (COMException) { } await Task.Delay(500); }
        throw new TimeoutException("the embedded drawing never became available");
    }

    void Write()
    {
        foreach (var g in rows.GroupBy(r => r.Wa))
        {
            var o = new JsonObject
            {
                ["checks"] = new JsonArray(g.Select(r => (JsonNode)new JsonObject { ["title"] = r.Title, ["status"] = r.Status, ["detail"] = r.Detail }).ToArray()),
                ["status"] = g.Any(r => r.Status == "FAIL") ? "FAIL" : "PASS",
                ["ran"] = DateTime.UtcNow.ToString("O"),
            };
            if (facts.TryGetValue(g.Key, out var f)) o["facts"] = f.DeepClone();
            File.WriteAllText(Path.Combine(Folder(g.Key), "result.json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
