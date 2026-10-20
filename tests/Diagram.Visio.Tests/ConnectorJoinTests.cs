using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

namespace Diagram.Visio.Tests;

/// <summary>R16: a branch joined to a connector, and a connector that hops, survive the trip through Visio, with the limits reported.</summary>
public class ConnectorJoinTests
{
    static object Join(int connectorN, double position) => new { glue = "none", point = new { x = 220.0, y = 120.0 }, join = new { connectorId = Doc.Id(connectorN), position } };

    static ExportSnapshot Scene(object? tapOver = null, object? busOver = null) => Doc.Snapshot(Doc.Make([Doc.Page(new object[]
    {
        Doc.Shape(1, 40, 100, 60, 40, new { alias = "a" }),
        Doc.Shape(2, 340, 100, 60, 40, new { alias = "b" }),
        Doc.Shape(3, 190, 300, 60, 40, new { alias = "c" }),
        Doc.Connector(10, Doc.Glue(1), Doc.Glue(2), "straight", busOver),
        Doc.Connector(11, Doc.Glue(3), Join(10, 0.5), "straight", tapOver),
    })]));

    static string Cell(System.Xml.Linq.XElement shape, string n) => (string?)shape.Descendants().First(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == n).Attribute("V") ?? "";

    [Fact]
    public void ABranchComesBackJoinedToItsConnectorAtTheSamePlace()
    {
        var (exported, back) = RT.Trip(Scene());
        Assert.Contains(exported.Diagnostics, d => d.Code == "join_not_native" && d.Severity == "info");
        Assert.DoesNotContain(exported.Diagnostics, d => d.Severity == "warning");
        var end = RT.El(back.Document, Doc.Id(11))["to"]!;
        Assert.Equal("none", end["glue"]!.GetValue<string>());
        Assert.Equal(Doc.Id(10), end["join"]!["connectorId"]!.GetValue<string>());
        Assert.Equal(0.5, end["join"]!["position"]!.GetValue<double>(), 6);
        Assert.Equal(220, end["point"]!["x"]!.GetValue<double>(), 2);   // the end is written where the editor kept it, on the line
        Assert.Equal(120, end["point"]!["y"]!.GetValue<double>(), 2);
        Assert.Null(RT.El(back.Document, Doc.Id(10))["from"]!["join"]);
    }

    [Fact]
    public void AJoinToAConnectorThatIsGoneOrWouldLoopIsDroppedAndReported()
    {
        var (exported, _) = RT.Trip(Scene());
        var gone = Fx.Edit(exported.Package, "visio/pages/page1.xml", d =>
        {
            foreach (var row in d.Descendants().Where(e => e.Name.LocalName == "Row" && (string?)e.Attribute("N") == "AgentJoinTo"))
                row.Elements().First(c => (string?)c.Attribute("N") == "Value").SetAttributeValue("V", $"{{\"connectorId\":\"{Doc.Id(99)}\",\"position\":0.5}}");
        });
        var again = new NativeVsdxImporter().Import(new MemoryStream(gone), new ImportOptions());
        Assert.True(again.IsOk, again.Error?.Message);
        Assert.Null(RT.El(again.Value.Document, Doc.Id(11))["to"]!["join"]);
        Assert.Contains(again.Value.Diagnostics, d => d.Code == "join_connector_missing");

        // the bus joined to the branch that joins the bus: a loop, so the second join is refused
        var loop = Fx.Edit(exported.Package, "visio/pages/page1.xml", d =>
        {
            var bus = d.Descendants().First(e => e.Name.LocalName == "Shape" && e.Descendants().Any(c => c.Name.LocalName == "Row" && (string?)c.Attribute("N") == "AgentRoute") && !e.Descendants().Any(c => (string?)c.Attribute("N") == "AgentJoinTo"));
            var section = bus.Descendants().First(e => e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "User");
            var row = new System.Xml.Linq.XElement(section.Name.Namespace + "Row", new System.Xml.Linq.XAttribute("N", "AgentJoinTo"),
                new System.Xml.Linq.XElement(section.Name.Namespace + "Cell", new System.Xml.Linq.XAttribute("N", "Value"), new System.Xml.Linq.XAttribute("V", $"{{\"connectorId\":\"{Doc.Id(11)}\",\"position\":0.5}}")));
            section.Add(row);
        });
        var looped = new NativeVsdxImporter().Import(new MemoryStream(loop), new ImportOptions());
        Assert.True(looped.IsOk, looped.Error?.Message);
        Assert.Null(RT.El(looped.Value.Document, Doc.Id(10))["to"]!["join"]);
    }

    [Fact]
    public void AHoppingConnectorIsToldToJumpAsAnArcAndTheOthersNeverToAndItComesBack()
    {
        var (exported, back) = RT.Trip(Scene(busOver: new { hops = "arc" }));
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        var shapes = xml.Descendants().Where(e => e.Name.LocalName == "Shape" && e.Elements().Any(c => (string?)c.Attribute("N") == "ConLineJumpCode")).ToList();
        Assert.Equal(2, shapes.Count);
        var codes = shapes.Select(s => (Cell(s, "ConLineJumpCode"), s.Elements().Any(c => (string?)c.Attribute("N") == "ConLineJumpStyle"))).OrderBy(x => x.Item1).ToList();
        Assert.Equal([("1", false), ("2", true)], codes);
        Assert.Equal("arc", RT.El(back.Document, Doc.Id(10))["hops"]!.GetValue<string>());
        Assert.Null(RT.El(back.Document, Doc.Id(11))["hops"]);
    }
}
