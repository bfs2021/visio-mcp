using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Xunit;

/// <summary>
/// The TypeScript side validates contracts/fixtures/samples.json against the JSON Schemas. This side proves the
/// generated C# DTOs accept exactly the same samples and write them back unchanged, so both languages describe one wire format.
/// </summary>
public class SchemaParityTests
{
    static JsonObject Samples()
    {
        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "samples.json")))!.AsObject();
        raw.Remove("$comment");
        var resolved = new JsonObject();
        foreach (var (k, v) in raw) resolved[k] = Resolve(raw, v!);
        return resolved;
    }

    static JsonNode Resolve(JsonObject all, JsonNode node)
    {
        switch (node)
        {
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var x in a) arr.Add(x is null ? null : Resolve(all, x));
                return arr;
            case JsonObject o when o["$sample"] is JsonNode name:
                var target = Resolve(all, all[name.GetValue<string>()]!);
                if (o["path"] is JsonNode p)
                    foreach (var seg in p.GetValue<string>().Split('/'))
                        target = target is JsonArray ta ? ta[int.Parse(seg)]! : target[seg]!;
                return target.DeepClone();
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (k, v) in o) obj[k] = v is null ? null : Resolve(all, v);
                return obj;
            default:
                return node.DeepClone();
        }
    }

    // An explicit JSON null in a patch ("clear this property") is a TypeScript-engine concept; the C# DTOs treat null as absent.
    static JsonNode WithoutNulls(JsonNode node)
    {
        switch (node)
        {
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var x in a) arr.Add(WithoutNulls(x!));
                return arr;
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (k, v) in o) if (v is not null) obj[k] = WithoutNulls(v);
                return obj;
            default:
                return node.DeepClone();
        }
    }

    static Type TypeFor(string key)
    {
        var name = key == "ResponseEnvelopeError" ? "ResponseEnvelope" : key;
        return typeof(ContractJson).Assembly.GetType($"Diagram.Core.Contracts.{name}") ?? throw new InvalidOperationException($"no generated type {name}");
    }

    public static IEnumerable<object[]> Keys() => Samples().Select(kv => new object[] { kv.Key });

    [Theory]
    [MemberData(nameof(Keys))]
    public void SampleRoundTripsThroughGeneratedDto(string key)
    {
        var sample = Samples()[key]!;
        var dto = sample.Deserialize(TypeFor(key), ContractJson.Options)!;
        var back = JsonSerializer.SerializeToNode(dto, TypeFor(key), ContractJson.Options)!;
        Assert.True(JsonNode.DeepEquals(WithoutNulls(sample), back), $"{key} changed in the round trip:\n{sample.ToJsonString()}\n{back.ToJsonString()}");
    }

    [Fact]
    public void EveryOperationAndElementKindDeserializesToItsOwnType()
    {
        var req = Samples()["MutationRequest"].Deserialize<MutationRequest>(ContractJson.Options)!;
        Assert.Equal(
            new[] { "CreateShapeOp", "CreateTextOp", "CreateImageOp", "CreateConnectorOp", "SetOp", "MoveOp", "ResizeOp", "RotateOp", "DeleteOp", "RegisterAssetOp", "SetPageOp" },
            req.Operations.Select(o => o.GetType().Name).ToArray());

        var doc = Samples()["DiagramDocument"].Deserialize<DiagramDocument>(ContractJson.Options)!;
        Assert.Equal(
            new[] { "ShapeElement", "TextElement", "ImageElement", "ConnectorElement", "GroupElement" },
            doc.Pages[0].Elements.Select(e => e.GetType().Name).ToArray());
        Assert.IsType<SolidPaint>(((ShapeElement)doc.Pages[0].Elements[0]).Style.Fill);
        Assert.IsType<NonePaint>(((ShapeElement)doc.Pages[0].Elements[0]).Style.Stroke);
    }

    [Fact]
    public void DiscriminatorMayAppearInAnyPropertyOrder()
    {
        // The canonical serializer sorts keys, so "kind" is not first; the C# side must still read it.
        var json = """{"opacity":1,"colour":"#112233","kind":"solid"}""";
        var paint = JsonSerializer.Deserialize<Paint>(json, ContractJson.Options);
        Assert.IsType<SolidPaint>(paint);
    }

    [Fact]
    public void InvalidFixtureWithUnknownFieldIsRejected()
    {
        var env = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "invalid-apply.json")))!.AsObject();
        var req = new JsonObject(env["params"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.DeepClone()))
        {
            ["documentId"] = env["documentId"]!.DeepClone(),
            ["sessionId"] = env["sessionId"]!.DeepClone(),
        };
        Assert.Throws<JsonException>(() => req.Deserialize<MutationRequest>(ContractJson.Options));
    }

    [Fact]
    public void ValidFixtureEnvelopeAndParamsDeserialize()
    {
        var env = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "valid-apply.json")))!;
        var envelope = env.Deserialize<RequestEnvelope>(ContractJson.Options)!;
        Assert.Equal("doc.apply", envelope.Method);
        Assert.Equal(1, envelope.ProtocolVersion);
        var merged = new JsonObject(env["params"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.DeepClone()))
        {
            ["documentId"] = env["documentId"]!.DeepClone(),
            ["sessionId"] = env["sessionId"]!.DeepClone(),
        };
        var req = merged.Deserialize<MutationRequest>(ContractJson.Options)!;
        Assert.Equal(3, req.Operations.Count);
        Assert.IsType<CreateShapeOp>(req.Operations[0]);
    }
}
