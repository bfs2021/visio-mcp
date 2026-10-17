using System.Text.Json;
using VsdxProbe;
var output = args.Length >= 2 && args[0] == "--output" ? args[1] : "generated.vsdx";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
Console.WriteLine(JsonSerializer.Serialize(ProbeScene.Write(output), new JsonSerializerOptions { WriteIndented = true }));
