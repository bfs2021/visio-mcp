using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using IpcProbe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Stdout carries MCP protocol only. Every diagnostic, including the startup line, goes to stderr.
Console.Error.WriteLine("McpProbe starting");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
var pipe = Environment.GetEnvironmentVariable("DIAGRAM_PIPE") ?? PipeNames.For(Envelope.ProtocolVersion);
builder.Services.AddSingleton(new HostClient(pipe, TimeSpan.FromMilliseconds(500)));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DiagramTools>();
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class DiagramTools(HostClient host)
{
    [McpServerTool(Name = "get_document_summary"), Description("Returns the open document's id, revision, page count and title.")]
    public async Task<CallToolResult> GetDocumentSummary(CancellationToken ct)
    {
        try
        {
            using var result = await host.CallAsync("doc.summary", null, ct);
            return Ok(JsonNode.Parse(result.RootElement.GetRawText())!);
        }
        catch (HostException e)
        {
            return Fail(e);
        }
    }

    static CallToolResult Ok(JsonNode structured) => new()
    {
        Content = [new TextContentBlock { Text = structured.ToJsonString() }],
        StructuredContent = JsonSerializer.SerializeToElement(structured),
    };

    static CallToolResult Fail(HostException e)
    {
        var error = new JsonObject { ["code"] = e.Code, ["message"] = e.Message, ["retryable"] = e.Retryable };
        if (e.Outcome != null) error["details"] = new JsonObject { ["outcome"] = e.Outcome };
        var body = new JsonObject { ["error"] = error };
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = body.ToJsonString() }],
            StructuredContent = JsonSerializer.SerializeToElement(body),
        };
    }
}
