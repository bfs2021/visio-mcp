using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

namespace Diagram.Visio.Tests;

/// <summary>R16: the T-junctions in the owner's Lego Infrastructure drawing come in as joins. Needs the owner's file, so it does nothing where it is absent.</summary>
public class LegoJunctionTests
{
    const string File_ = @"C:\Dev\vsdx\Lego Infrastructure - v2.vsdx";

    [Fact]
    public void TheBusJunctionsInTheLegoDrawingAreJoinedToTheLinesTheyMeet()
    {
        if (!File.Exists(File_)) return; // the owner's own file is not part of the repository
        using var s = File.OpenRead(File_);
        var r = new NativeVsdxImporter().Import(s, new ImportOptions());
        Assert.True(r.IsOk, r.Error?.Message);
        var els = System.Text.Json.JsonSerializer.SerializeToNode(r.Value.Document, ContractJson.Options)!["pages"]![0]!["elements"]!.AsArray();
        var connectors = els.Where(e => e!["kind"]!.GetValue<string>() == "connector").Select(e => e!.AsObject()).ToDictionary(e => e["id"]!.GetValue<string>());
        var joins = connectors.Values.SelectMany(c => new[] { "from", "to" }.Where(k => c[k]!["join"] is not null).Select(k => (Connector: c, End: k, Join: c[k]!["join"]!))).ToList();

        Assert.Equal(5, joins.Count);                                                                      // the Lego T-junctions
        Assert.Equal(5, r.Value.Diagnostics.Count(d => d.Code == "join_inferred" && d.Severity == "info"));
        // four branches meet one line, and that line itself meets another: a bus into the next
        var targets = joins.GroupBy(j => j.Join["connectorId"]!.GetValue<string>()).OrderByDescending(g => g.Count()).ToList();
        Assert.Equal(4, targets[0].Count());
        Assert.Contains(joins, j => j.Connector["id"]!.GetValue<string>() == targets[0].Key);
        foreach (var (c, end, join) in joins)
        {
            var target = connectors[join["connectorId"]!.GetValue<string>()];
            Assert.NotEqual(c["id"]!.GetValue<string>(), target["id"]!.GetValue<string>());
            var pos = join["position"]!.GetValue<double>();
            Assert.InRange(pos, 0.02, 0.98);
            Assert.Equal("none", c[end]!["glue"]!.GetValue<string>());                                     // a join is a free end placed on the line
        }
        Assert.DoesNotContain(r.Value.Diagnostics, d => d.Code.StartsWith("join") && d.Severity == "warning");
    }
}
