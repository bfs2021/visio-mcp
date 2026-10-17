using System.Text.Json;
using Diagram.Core.Contracts;
using Diagram.Visio;
using Xunit;

public class ProbeTests
{
    [Fact]
    public void DumpImport()
    {
        var path = Environment.GetEnvironmentVariable("DIAGRAM_DUMP_IN");
        if (path is null) return;
        var r = RT.Importer.Import(File.OpenRead(path), new ImportOptions());
        var outp = Environment.GetEnvironmentVariable("DIAGRAM_DUMP_OUT")!;
        if (!r.IsOk) { File.WriteAllText(outp, "ERROR " + r.Error!.Code + ": " + r.Error.Message); return; }
        File.WriteAllText(outp, JsonSerializer.Serialize(new { doc = r.Value.Document, diag = r.Value.Diagnostics, id = r.Value.Identity }, new JsonSerializerOptions(ContractJson.Options) { WriteIndented = true }));
    }
}
