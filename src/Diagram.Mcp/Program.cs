using Diagram.Ipc;
using Diagram.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Stdout carries MCP protocol only. Every diagnostic, including this line, goes to stderr, and none of them contains document text.
Console.Error.WriteLine("Diagram.Mcp starting");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
var pipe = Environment.GetEnvironmentVariable("DIAGRAM_PIPE") ?? PipeNames.For();
builder.Services.AddSingleton(new PipeClient(pipe, TimeSpan.FromMilliseconds(800), "Diagram.Mcp"));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DiagramTools>();
await builder.Build().RunAsync();
