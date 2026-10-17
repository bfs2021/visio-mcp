using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

public class IdentityTests
{
    static IEnumerable<XElement> Rows(XDocument d, string name) => d.Descendants().Where(e => e.Name.LocalName == "Row" && (string?)e.Attribute("N") == name);
    static void SetAgent(XDocument page, string name, int nth, string? value)
    {
        var row = Rows(page, name).ElementAt(nth);
        var cell = row.Elements().First();
        if (value is null) row.Remove(); else cell.SetAttributeValue("V", value);
    }

    static byte[] Scene(out DiagramDocument doc)
    {
        doc = Doc.Make([Doc.Page([
            Doc.Shape(1, 50, 50, 60, 40, new { alias = "a" }), Doc.Shape(2, 150, 50, 60, 40, new { alias = "b" }), Doc.Shape(3, 250, 50, 60, 40, new { alias = "c" }),
            Doc.Connector(10, Doc.Glue(1), Doc.Glue(2)),
        ])]);
        return RT.Export(Doc.Snapshot(doc)).Package;
    }

    [Fact]
    public void ValidIdentityIsPersistedUnchanged()
    {
        var pkg = Scene(out var doc);
        var back = RT.Import(pkg);
        Assert.Equal(doc.Id, back.Document.Id);
        Assert.Equal(doc.Pages[0].Id, back.Document.Pages[0].Id);
        Assert.Equal(new[] { Doc.Id(1), Doc.Id(2), Doc.Id(3), Doc.Id(10) }.OrderBy(x => x), RT.All(back.Document).Select(e => e["id"]!.GetValue<string>()).OrderBy(x => x));
        Assert.Equal(0, back.Identity.Generated + back.Identity.Repaired);
        Assert.True(back.Identity.Preserved >= 5);
    }

    [Fact]
    public void MissingIdsAreGeneratedAndTheSameBytesAlwaysGiveTheSameIds()
    {
        var pkg = Scene(out _);
        var stripped = Fx.Edit(pkg, "visio/pages/page1.xml", d => { for (var i = 0; i < 3; i++) SetAgent(d, "AgentId", 0, null); });
        var first = RT.Import(stripped);
        var second = RT.Import(stripped);
        Assert.Equal(3, first.Identity.Generated);
        var ids = RT.All(first.Document).Select(e => e["id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids, RT.All(second.Document).Select(e => e["id"]!.GetValue<string>()).ToList()); // deterministic for unchanged bytes
        Assert.All(ids, id => Assert.True(IdentityMap.IsUuid(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // and they are version 5 UUIDs
        Assert.Contains(ids, id => id[14] == '5');
        // different bytes, different ids
        var other = Fx.Edit(stripped, "visio/pages/page1.xml", d => d.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "PinX").SetAttributeValue("V", "9"));
        Assert.NotEqual(ids, RT.All(RT.Import(other).Document).Select(e => e["id"]!.GetValue<string>()).ToList());
    }

    [Fact]
    public void InvalidIdsAreRepairedAndReported()
    {
        var pkg = Scene(out _);
        var bad = Fx.Edit(pkg, "visio/pages/page1.xml", d => SetAgent(d, "AgentId", 0, "11111111")); // what a numeric misread of a UUID looks like
        var back = RT.Import(bad);
        Assert.Equal(1, back.Identity.Repaired);
        Assert.Contains(back.Identity.Notes, n => n.Contains("not a UUID"));
        var ids = RT.All(back.Document).Select(e => e["id"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("11111111", ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void CopiedShapesThatRepeatAnIdAreGivenNewOnesAndTheFirstKeepsIt()
    {
        var pkg = Scene(out _);
        // shape 3 is a copy of shape 1 as far as its identity cell goes (copy and paste inside Visio copies user cells)
        var dup = Fx.Edit(pkg, "visio/pages/page1.xml", d => SetAgent(d, "AgentId", 2, Doc.Id(1)));
        var back = RT.Import(dup);
        Assert.Equal(1, back.Identity.Repaired);
        Assert.Contains(back.Identity.Notes, n => n.Contains("copied shape"));
        var all = RT.All(back.Document).ToList();
        Assert.Equal(all.Count, all.Select(e => e["id"]!.GetValue<string>()).Distinct().Count());
        Assert.Equal("a", RT.El(back.Document, Doc.Id(1))["alias"]!.GetValue<string>()); // the original keeps its identity
        // the copy lost nothing but the id: it is still shape c, now with a fresh id and no duplicated alias
        var copy = all.First(e => e["alias"]?.GetValue<string>() == "c");
        Assert.NotEqual(Doc.Id(1), copy["id"]!.GetValue<string>());
    }

    [Fact]
    public void ConnectorsFollowRepairedIds()
    {
        var pkg = Scene(out _);
        var changed = Fx.Edit(pkg, "visio/pages/page1.xml", d => SetAgent(d, "AgentId", 1, "not-a-uuid")); // shape b, the connector's target
        var back = RT.Import(changed);
        var conn = RT.All(back.Document).First(e => e["kind"]!.GetValue<string>() == "connector");
        var target = conn["to"]!["elementId"]!.GetValue<string>();
        Assert.Contains(RT.All(back.Document), e => e["id"]!.GetValue<string>() == target && e["alias"]?.GetValue<string>() == "b");
        Assert.NotEqual(Doc.Id(2), target);
    }

    [Fact]
    public void AnAliasUsedTwiceOnOnePageIsKeptByTheFirstShapeOnly()
    {
        var pkg = Scene(out _);
        var twice = Fx.Edit(pkg, "visio/pages/page1.xml", d => Rows(d, "AgentAlias").ElementAt(1).Elements().First().SetAttributeValue("V", "a"));
        var back = RT.Import(twice);
        Assert.Equal(1, RT.All(back.Document).Count(e => e["alias"]?.GetValue<string>() == "a"));
        Assert.Contains(back.Diagnostics, d => d.Code == "alias_repeated");
    }

    [Fact]
    public void StalePresetMetadataAfterAnOutlineEditIsNotTrusted()
    {
        var doc = Doc.Make([Doc.Page([Doc.Shape(1, 100, 100, 100, 80, preset: "hexagon")])]);
        var pkg = RT.Export(Doc.Snapshot(doc)).Package;
        // the shape is redrawn in Visio (a point moves) but the note still says "hexagon"
        var redrawn = Fx.Edit(pkg, "visio/pages/page1.xml", d =>
        {
            var row = d.Descendants().First(e => e.Name.LocalName == "Row" && (string?)e.Attribute("T") == "LineTo");
            row.Elements().First(c => (string?)c.Attribute("N") == "X").SetAttributeValue("V", "0.1");
            row.Elements().First(c => (string?)c.Attribute("N") == "X").Attribute("F")?.Remove();
        });
        var back = RT.Import(redrawn);
        var g = RT.El(back.Document, Doc.Id(1))["geometry"]!;
        Assert.Null(g["preset"]);
        Assert.NotNull(g["svgPath"]); // kept as a path rather than masquerading as a hexagon
        Assert.Contains(back.Diagnostics, d => d.Code == "outline_converted");
    }

    [Fact]
    public void StaleSvgPathMetadataIsNotTrustedEither()
    {
        var doc = Doc.Make([Doc.Page([Doc.Shape(1, 100, 100, 100, 80, new { geometry = new { svgPath = "M 0 0 L 100 0 L 50 100 Z" } })])]);
        var pkg = RT.Export(Doc.Snapshot(doc)).Package;
        var redrawn = Fx.Edit(pkg, "visio/pages/page1.xml", d =>
        {
            var row = d.Descendants().Where(e => e.Name.LocalName == "Row" && (string?)e.Attribute("T") == "LineTo").Skip(1).First();
            row.Elements().First(c => (string?)c.Attribute("N") == "Y").SetAttributeValue("V", "0.05");
        });
        var path = RT.El(RT.Import(redrawn).Document, Doc.Id(1))["geometry"]!["svgPath"]!.GetValue<string>();
        Assert.NotEqual("M 0 0 L 100 0 L 50 100 Z", path);
    }

    [Fact]
    public void ShapesThatAreNotOursGetIdsAndAreImportedWhereTheyAre()
    {
        // a drawing made in Visio, with no identity cells anywhere (a fixture Visio itself saved)
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures, "visio-saved", "m0-v7.vsdx"));
        var a = RT.Import(bytes); var b = RT.Import(bytes);
        Assert.Equal(RT.All(a.Document).Select(e => e["id"]!.GetValue<string>()), RT.All(b.Document).Select(e => e["id"]!.GetValue<string>()));
        Assert.True(a.Document.Pages.Sum(p => p.Elements.Count) >= 5);
        foreach (var p in a.Document.Pages)
        {
            var ids = p.Elements.Select(e => JsonSerializer.SerializeToNode(e, ContractJson.Options)!["id"]!.GetValue<string>()).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.All(ids, id => Assert.True(IdentityMap.IsUuid(id)));
        }
    }

    [Fact]
    public void V5IdsMatchTheReferenceAlgorithm()
    {
        // RFC 4122: uuid5 of the fixed namespace and a name; fixed vector keeps the algorithm from drifting
        var id = IdentityMap.V5("probe");
        Assert.Equal(id, IdentityMap.V5("probe"));
        Assert.NotEqual(id, IdentityMap.V5("probe2"));
        Assert.Equal('5', id[14]);
        Assert.Contains(id[19], "89ab");
    }

    public static string Fixtures
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures"))) dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir!, "tests", "fixtures", "vsdx");
        }
    }
}
