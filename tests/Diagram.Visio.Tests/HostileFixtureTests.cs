using Diagram.Visio;
using Xunit;

public class HostileFixtureTests
{
    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(Path.Combine(IdentityTests.Fixtures, "hostile"), "*.vsdx").Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>Every hostile file is refused with a structured error. None may crash the importer, read outside the package, or open a local file.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void HostilePackageIsRefusedCleanly(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(IdentityTests.Fixtures, "hostile", name));
        var r = RT.Importer.Import(new MemoryStream(bytes), new ImportOptions());
        Assert.False(r.IsOk, $"{name} should have been refused");
        Assert.False(string.IsNullOrWhiteSpace(r.Error!.Code));
        Assert.DoesNotContain("win.ini", r.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
