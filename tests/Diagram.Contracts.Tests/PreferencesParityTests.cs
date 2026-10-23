using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Xunit;

public class PreferencesParityTests
{
    static JsonNode Defaults() => JsonNode.Parse("""{"schemaVersion":1,"units":"mm","grid":{"spacingPt":14.173228346456694,"visible":true,"snap":true},"font":{"family":"Segoe UI","sizePt":12},"recovery":{"checkpointSeconds":60},"theme":"light"}""")!;
    [Fact]
    public void DefaultsAndCaptureAreIndependent()
    {
        var p = PreferencesValidation.DefaultPreferences();
        Assert.Equal(72 * 5 / 25.4, p.Grid.SpacingPt);
        Assert.Equal("Segoe UI", p.Font.Family);
        Assert.Equal(12, p.Font.SizePt);
        Assert.Equal(60, p.Recovery.CheckpointSeconds);
        Assert.Equal("mm", p.Units); Assert.Equal("light", p.Theme);
        Assert.True(p.Grid.Visible && p.Grid.Snap);
        Assert.Equal(p.Font, PreferencesValidation.CaptureDefaults(p).Font);
    }
    [Theory]
    [InlineData("schemaVersion", "2")][InlineData("schemaVersion", "\"1\"")]
    [InlineData("units", "\"feet\"")][InlineData("theme", "\"system\"")][InlineData("extra", "true")]
    [InlineData("grid.spacingPt", "0.09")][InlineData("grid.spacingPt", "1440.01")][InlineData("grid.spacingPt", "\"5\"")]
    [InlineData("grid.visible", "1")][InlineData("grid.snap", "\"true\"")][InlineData("grid.extra", "1")]
    [InlineData("font.family", "\"\"")][InlineData("font.family", "\"   \"")][InlineData("font.family", "42")]
    [InlineData("font.sizePt", "0")][InlineData("font.sizePt", "1001")]
    [InlineData("recovery.checkpointSeconds", "9")][InlineData("recovery.checkpointSeconds", "3601")][InlineData("recovery.checkpointSeconds", "10.5")][InlineData("recovery.checkpointSeconds", "\"60\"")]
    public void RejectsInvalidBoundary(string path, string json)
    {
        var p = Defaults(); var keys = path.Split('.');
        if (keys.Length == 2) p[keys[0]]![keys[1]] = JsonNode.Parse(json); else p[path] = JsonNode.Parse(json);
        var result = PreferencesValidation.ValidatePreferences(p);
        Assert.Null(result.Value); Assert.Equal("invalid_request", result.Error!.Code);
    }
    [Fact]
    public void FamilyNormalizesBeforeBoundsAndUnavailableFontIsValid()
    {
        var p = Defaults(); p["font"]!["family"] = "  " + new string('x', 128) + "  ";
        Assert.Equal(new string('x', 128), PreferencesValidation.ValidatePreferences(p).Value!.Font.Family);
        p["font"]!["family"] = new string('x', 129);
        Assert.NotNull(PreferencesValidation.ValidatePreferences(p).Error);
    }
    [Theory][InlineData(0.1, 1, 10)][InlineData(1440, 1000, 3600)]
    public void AcceptsInclusiveBounds(double spacing, double size, int seconds)
    {
        var p = Defaults(); p["grid"]!["spacingPt"] = spacing; p["font"]!["sizePt"] = size; p["recovery"]!["checkpointSeconds"] = seconds;
        Assert.NotNull(PreferencesValidation.ValidatePreferences(p).Value);
    }
    [Fact]
    public void RejectsNonFiniteAndMalformedRoots()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }) {
            var p = Defaults(); p["grid"]!["spacingPt"] = JsonValue.Create(value);
            Assert.NotNull(PreferencesValidation.ValidatePreferences(p).Error);
        }
        foreach (var p in new JsonNode?[] { null, new JsonObject(), new JsonArray(), JsonValue.Create(true) }) Assert.NotNull(PreferencesValidation.ValidatePreferences(p).Error);
    }
    [Fact]
    public void SharedBoundaryFixturesAgreeWithFrontend()
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "preferences-boundaries.json")))!;
        foreach (var group in new[] { "valid", "invalid" }) foreach (var fixture in fixtures[group]!.AsArray()) {
            var p = Defaults(); var keys = fixture!["path"]!.GetValue<string>().Split('.');
            if (keys.Length == 2) p[keys[0]]![keys[1]] = fixture["value"]?.DeepClone(); else p[keys[0]] = fixture["value"]?.DeepClone();
            var result = PreferencesValidation.ValidatePreferences(p);
            Assert.Equal(group == "valid", result.Value is not null);
            if (fixture["normalized"] is JsonNode expected) Assert.Equal(expected.GetValue<string>(), result.Value!.Font.Family);
        }
    }}

