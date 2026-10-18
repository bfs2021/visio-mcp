using Diagram.Visio;
using Xunit;

public class IconGeometryTests
{
    [Fact]
    public void AdjacentDecimalCoordinatesMatchTheWebParser()
    {
        var result = GeometryMap.ForSvgPath("M .5.5 L 50.5.5");
        Assert.True(result.IsOk);
        Assert.Equal(new Frac(0.005, 0.005), Assert.IsType<MoveRow>(result.Value[0].Rows[0]).At);
        Assert.Equal(new Frac(0.505, 0.005), Assert.IsType<LineRow>(result.Value[0].Rows[1]).To);
    }

    [Theory]
    [InlineData("M 0 0 L 1e999 2")]
    [InlineData("M 0 0 l 1000001 2")]
    [InlineData("M 900000 0 l 900000 0")]
    [InlineData("L 0 0 M 1 1 L 2 2")]
    [InlineData("M 0 0 L 1 1 Z 2 2")]
    [InlineData("M 0 0 M 1 1")]
    [InlineData("M 0 0 Z")]
    public void InvalidPathsAreRejectedBeforeExport(string path) => Assert.False(GeometryMap.ForSvgPath(path).IsOk);
}



public class CatalogRoundTripTests
{
    [Fact]
    public void EveryBundledIconRemainsEditableAndPinnedThroughNativeExport()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Diagram.Windows.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var catalog = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "contracts/icons/catalog.json")));
        var data = catalog.RootElement;
        var shapes = data.GetProperty("icons").EnumerateArray().Select((icon, i) => Doc.Shape(i + 1, 40 + i % 5 * 120, 40 + i / 5 * 112, 64, 64, new
        {
            geometry = new { svgPath = icon.GetProperty("path").GetString() },
            style = new { fill = Doc.None, stroke = Doc.Stroke, strokeWidthPt = 4.0, dash = "solid", lineCap = "round", lineJoin = "round" },
            metadata = new Dictionary<string, string> { ["icon.id"] = icon.GetProperty("id").GetString()!, ["icon.catalogVersion"] = data.GetProperty("version").GetString()!, ["icon.geometrySha256"] = icon.GetProperty("sha256").GetString()!, ["icon.license"] = data.GetProperty("licenceText").GetString()! },
        })).ToArray();
        var (exported, back) = RT.Trip(Doc.Snapshot(Doc.Page(shapes, new { widthPt = 680, heightPt = 760 })));
        Assert.Equal(26, back.Document.Pages[0].Elements.Count);
        Assert.DoesNotContain(exported.Diagnostics, d => d.Severity == "warning");
        var xml = Fx.Part(exported.Package, "visio/pages/page1.xml");
        Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName == "ForeignData");
        foreach (var (icon, i) in data.GetProperty("icons").EnumerateArray().Select((icon, i) => (icon, i)))
        {
            Assert.True(GeometryMap.ForSvgPath(icon.GetProperty("path").GetString()!).IsOk);
            var shape = RT.El(back.Document, Doc.Id(i + 1));
            Assert.Equal(icon.GetProperty("path").GetString(), shape["geometry"]!["svgPath"]!.GetValue<string>());
            Assert.Equal(icon.GetProperty("id").GetString(), shape["metadata"]!["icon.id"]!.GetValue<string>());
            Assert.Equal(icon.GetProperty("sha256").GetString(), shape["metadata"]!["icon.geometrySha256"]!.GetValue<string>());
            Assert.Equal(data.GetProperty("licenceText").GetString(), shape["metadata"]!["icon.license"]!.GetValue<string>());
        }
        var evidence = Path.Combine(root.FullName, "artifacts/r03-icons"); Directory.CreateDirectory(evidence);
        File.WriteAllBytes(Path.Combine(evidence, "icon-gallery.vsdx"), exported.Package);
    }
}
