using VsdxProbe;
using Xunit;

public class RoundTripTests
{
    // Set VSDX_SAVED to a Visio-saved file to run read-side assertions against it.
    static string? Saved => Environment.GetEnvironmentVariable("VSDX_SAVED");
    static string Tmp() => Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.vsdx");

    static ProbeManifest Make(out string path)
    {
        path = Saved ?? Tmp();
        return Saved is null ? ProbeScene.Write(path) : ProbeScene.Read(path);
    }

    [Fact]
    public void GeneratedFileContainsIndependentShapesAndGlue()
    {
        var m = Make(out _);
        if (Saved is null)
        {
            Assert.Equal(297 / 25.4 * 72, m.PageWidthPt, 2);
            Assert.Equal(210 / 25.4 * 72, m.PageHeightPt, 2);
        }
        Assert.Equal(ProbeScene.RectAgentId.ToString(), m.RectangleAgentId);
        Assert.Equal(m.RectangleId, m.ConnectorFromShapeId);
        Assert.Equal("Probe", m.RectangleText);
        Assert.Equal(5, m.DrawableCount); // rect, ellipse, connector, text box, PNG picture
        Assert.Equal(ImagePatch.Sha256(ImagePatch.MakePng()), m.ImageHash);
    }

    [Fact]
    public void Rotation33Degrees()
    {
        if (Saved is not null) return; // Visio-saved files may legitimately have been rotated by hand.
        Assert.Equal(33.0, Make(out _).RotatedAngleDeg, 2);
    }

    [Fact]
    public void StaticPortSurvives()
    {
        Make(out var path);
        var rows = ProbeScene.ConnectionRows(path);
        Assert.True(rows["Rectangle"] >= 1, "rectangle keeps a connection point");
        Assert.True(rows["Ellipse"] >= 1, "ellipse keeps a connection point");
    }

    [Fact]
    public void LayerMembershipSurvives()
    {
        Make(out var path);
        var l = ProbeScene.Layers(path);
        Assert.Equal(new[] { "Layer A" }, l["Rectangle"]);
        Assert.Equal(new[] { "Layer B" }, l["Ellipse"]);
    }

    [Fact]
    public void MultiLayerMembershipSurvives()
    {
        // Policy under test: a shape may belong to several layers; membership must round-trip as a set.
        Make(out var path);
        var l = ProbeScene.Layers(path, 2);
        Assert.Equal(new[] { "L1", "L2" }, l["MultiLayer"].OrderBy(x => x).ToArray());
    }

    [Fact]
    public void GroupResizeKeepsChildProportions()
    {
        Make(out var path);
        var (gw, gh, ratios, relative) = ProbeScene.GroupGeometry(path);
        // Children are positioned/sized as fractions of the group, whatever size the group now has.
        Assert.True(relative, "child Width must be a formula of the group (Sheet.N!Width*k)");
        Assert.Equal(new[] { 0.25, 0.5, 0.3, 0.4 }, ratios["ChildA"], new Tol());
        Assert.Equal(new[] { 0.75, 0.5, 0.3, 0.4 }, ratios["ChildB"], new Tol());
        if (Saved is not null)
            Assert.True(Math.Abs(gw - ProbeScene.GroupW) > 0.01 || Math.Abs(gh - ProbeScene.GroupH) > 0.01,
                "group was not resized in this Visio-saved file, so the probe proves nothing");
    }

    sealed class Tol : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-4;
        public int GetHashCode(double d) => 0;
    }

    [Fact]
    public void ImageProvenanceRecordedAndVerifiable()
    {
        Make(out var path);
        var (status, source, mime) = ProbeScene.CheckProvenance(path);
        Assert.Equal(ProbeScene.Provenance.Match, status);
        Assert.Equal(ImagePatch.SourceUri, source);
        Assert.Equal(ImagePatch.MimeType, mime);
    }

    [Fact]
    public void ImageReplacementIsDetectedAsProvenanceMismatch()
    {
        var path = Tmp();
        ProbeScene.Write(path);
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry("visio/media/image1.png")!.Delete();
            using var o = zip.CreateEntry("visio/media/image1.png").Open();
            o.Write(new byte[] { 1, 2, 3 });
        }
        Assert.Equal(ProbeScene.Provenance.Mismatch, ProbeScene.CheckProvenance(path).Status);
    }

    [Fact]
    public void DuplicateUserIdResolved()
    {
        var path = Tmp();
        ProbeScene.WriteDuplicateIdScene(path);
        var (dups, resolved) = ProbeScene.ResolveDuplicateAgentIds(path);
        // The library keeps duplicate IDs; uniqueness must be enforced by our importer.
        Assert.Single(dups);
        Assert.Equal(2, resolved.Count);
        Assert.Equal(2, resolved.Distinct().Count());
    }

    [Fact]
    public void SvgSourcePreservation()
    {
        Make(out var path);
        var svg = ProbeScene.ReadSvgSource(path);
        Assert.Equal(ImagePatch.SvgSource, svg);
    }
}
