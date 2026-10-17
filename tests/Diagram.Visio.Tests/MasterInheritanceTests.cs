using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Xunit;

public class MasterInheritanceTests
{
    /// <summary>Visio stores a stencil shape as its position plus a link to the master: size and outline must come from the master.</summary>
    [Fact]
    public void StencilShapesGetTheirSizeAndOutlineFromTheMaster()
    {
        var bytes = File.ReadAllBytes(Path.Combine(IdentityTests.Fixtures, "visio-saved", "stencil-masters.vsdx"));
        var r = RT.Import(bytes);
        var shapes = r.Document.Pages[0].Elements.Select(e => System.Text.Json.JsonSerializer.SerializeToNode(e, ContractJson.Options)!.AsObject()).ToList();
        Assert.Equal(2, shapes.Count);
        foreach (var s in shapes)
        {
            Assert.Equal(1.5748031496 * 72, s["bounds"]!["width"]!.GetValue<double>(), 2);
            Assert.Equal(1.1811023622 * 72, s["bounds"]!["height"]!.GetValue<double>(), 2);
        }
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "unsupported_shape");
        Assert.Single(r.Diagnostics, d => d.Code == "master_inlined"); // once per master, not per shape
        Assert.NotEqual(shapes[0]["id"]!.GetValue<string>(), shapes[1]["id"]!.GetValue<string>());
    }
}
