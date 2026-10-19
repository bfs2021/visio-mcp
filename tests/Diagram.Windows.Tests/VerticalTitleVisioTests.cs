using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Diagram.Windows.Tests;

/// <summary>R14: swim-lane titles turned to read up a narrow header, and a text box with its own margins, fit in the editor's measurement and in Visio's own text layout.</summary>
public class VerticalTitleVisioTests
{
    [Fact]
    public async Task VerticalLaneTitlesAndMarginsFitInTheEditorAndInVisio()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150)); var ct = deadline.Token;
        var evidence = Path.Combine(Harness.RepoRoot, "artifacts/r14-text-fit"); Directory.CreateDirectory(evidence);
        var pipe = "r14-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync(pipe: pipe, exportRoot: evidence);
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "r14", Command = "dotnet", Arguments = [Path.Combine(Harness.RepoRoot, "src/Diagram.Mcp/bin", cfg, "net10.0/Diagram.Mcp.dll")],
            EnvironmentVariables = new Dictionary<string, string?> { ["DIAGRAM_PIPE"] = pipe },
        }), cancellationToken: ct);
        var summary = (await client.CallToolAsync("get_document_summary", cancellationToken: ct)).StructuredContent!.Value;
        Dictionary<string, object?> Identity() => new() { ["documentId"] = summary.GetProperty("documentId").GetString(), ["sessionId"] = summary.GetProperty("sessionId").GetString() };
        async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
        {
            var r = await client.CallToolAsync(name, args, cancellationToken: ct); Assert.True(r.IsError != true, string.Join(" ", r.Content.OfType<TextContentBlock>().Select(c => c.Text)) + r.StructuredContent?.ToString()); return r;
        }
        var pageId = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0].GetProperty("id").GetString()!;
        int revision = 0;
        async Task Apply(object operations)
        {
            var a = Identity(); a["baseRevision"] = revision; a["transactionId"] = Guid.NewGuid().ToString(); a["operations"] = JsonSerializer.SerializeToElement(operations);
            revision = (await Call("apply_operations", a)).StructuredContent!.Value.GetProperty("transaction").GetProperty("revision").GetInt32();
        }

        // lanes whose headers are too narrow for a horizontal title but hold a vertical one
        var args = Identity(); args["pageId"] = pageId; args["templateId"] = "swimlane"; args["x"] = 20; args["y"] = 20; args["layout"] = new { widthPt = 560, target = "visio" };
        args["content"] = new
        {
            title = "PSGL Dataflow", laneTitles = "vertical", laneHeaderWidthPt = 40,
            lanes = new object[] { new { title = "Corona Sensical", heightPt = 130 }, new { title = "Data lake", heightPt = 90 }, new { title = "Reporting", heightPt = 90 } },
            phases = new[] { new { title = "Day 1" }, new { title = "Day 2" } },
        };
        var proposal = (await Call("get_layout_component", args)).StructuredContent!.Value;
        await Apply(proposal.GetProperty("operations"));
        await Apply(new object[]
        {
            new { op = "createShape", pageId, alias = "indented", bounds = new { x = 120, y = 240, width = 150, height = 36 }, text = new { value = "Left margin 30pt", fontSizePt = 11, horizontalAlign = "left", insetsPt = new { top = 0, right = 0, bottom = 0, left = 30 } } },
        });

        // the editor's own measurement says every title fits
        var ids = (await h.Send("doc.snapshot")).Result!.Value.GetProperty("document").GetProperty("pages")[0].GetProperty("elements").EnumerateArray()
            .Where(e => e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.Object && t.GetProperty("value").GetString()!.Length > 0).Select(e => e.GetProperty("id").GetString()!).ToArray();
        var layoutArgs = Identity(); layoutArgs["ids"] = ids; layoutArgs["target"] = "visio";
        var layout = (await Call("get_text_layout", layoutArgs)).StructuredContent!.Value;
        var unfit = layout.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("status").GetString() != "fits").ToList();
        Assert.Empty(unfit);
        var rendered = await Call("render_page", new Dictionary<string, object?>(Identity()) { ["pageId"] = pageId, ["maxWidth"] = 900, ["maxHeight"] = 700 });
        await File.WriteAllBytesAsync(Path.Combine(evidence, "editor.png"), Assert.IsType<ImageContentBlock>(rendered.Content.First()).DecodedData.ToArray(), ct);

        var path = Path.Combine(evidence, "r14.vsdx"); File.Delete(path);
        var export = Identity(); export["format"] = "vsdx"; export["path"] = "r14.vsdx"; await Call("export_document", export);
        using (var s = File.OpenRead(path))
        {
            var imported = new NativeVsdxImporter().Import(s, new ImportOptions()); Assert.True(imported.IsOk, imported.Error?.Message);
            var elements = JsonSerializer.SerializeToNode(imported.Value.Document, ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray();
            Assert.Equal(3, elements.Count(e => e!["text"]?["orientation"]?.GetValue<string>() == "vertical"));
            Assert.Equal(30, elements.Single(e => e!["alias"]?.GetValue<string>() == "indented")!["text"]!["insetsPt"]!["left"]!.GetValue<double>(), 2);
        }

        dynamic? visio = OfficeAcceptanceTests.StartVisio();
        if (visio is null) { await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio unavailable: native fit not checked", ct); return; }
        var report = new List<string>();
        try
        {
            dynamic document = visio.Documents.Open(path);
            try
            {
                dynamic page = document.Pages.Item(1);
                page.Export(Path.Combine(evidence, "visio.png"));
                var checkedTitles = 0;
                void Check(dynamic shape)
                {
                    if ((int)shape.Type == 2) { foreach (dynamic child in shape.Shapes) Check(child); return; }
                    string text = shape.Text; if (string.IsNullOrEmpty(text)) return;
                    if ((short)shape.CellExistsU["TxtWidth", 0] == 0 || (short)shape.CellExistsU["TxtAngle", 0] == 0 || (double)shape.CellsU["TxtAngle"].ResultIU < 1) return; // only the turned titles
                    shape.AddNamedRow(242, "FitW", 0); shape.CellsU["User.FitW"].FormulaU = "TEXTWIDTH(TheText)";
                    shape.AddNamedRow(242, "FitH", 0); shape.CellsU["User.FitH"].FormulaU = "TEXTHEIGHT(TheText,TxtWidth)";
                    double needW = (double)shape.CellsU["User.FitW"].ResultIU * 72, needH = (double)shape.CellsU["User.FitH"].ResultIU * 72;
                    double blockW = (double)shape.CellsU["TxtWidth"].ResultIU * 72 - ((double)shape.CellsU["LeftMargin"].ResultIU + (double)shape.CellsU["RightMargin"].ResultIU) * 72;
                    double blockH = (double)shape.CellsU["TxtHeight"].ResultIU * 72 - ((double)shape.CellsU["TopMargin"].ResultIU + (double)shape.CellsU["BottomMargin"].ResultIU) * 72;
                    report.Add($"{text}: text {needW:F1}x{needH:F1}pt in {blockW:F1}x{blockH:F1}pt");
                    Assert.True(needW <= blockW + 0.5 && needH <= blockH + 0.5, $"Visio does not fit '{text}': {needW:F1}x{needH:F1} in {blockW:F1}x{blockH:F1}");
                    checkedTitles++;
                }
                foreach (dynamic top in page.Shapes) Check(top);
                Assert.Equal(3, checkedTitles);
            }
            finally { document.Saved = true; document.Close(); }
        }
        finally { visio.Quit(); }
        await File.WriteAllTextAsync(Path.Combine(evidence, "verification.txt"), "Visio laid out each vertical lane title inside its turned text block (measured with TEXTWIDTH/TEXTHEIGHT):\n" + string.Join("\n", report), ct);
    }
}
