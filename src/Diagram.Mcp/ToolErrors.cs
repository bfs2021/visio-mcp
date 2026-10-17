using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using ModelContextProtocol.Protocol;

namespace Diagram.Mcp;

/// <summary>Turns application results and errors into MCP tool results. Errors keep the application's own code, retryable flag and outcome.</summary>
public static class ToolErrors
{
    public static CallToolResult Ok(JsonNode structured) => new()
    {
        Content = [new TextContentBlock { Text = structured.ToJsonString() }],
        StructuredContent = JsonSerializer.SerializeToElement(structured),
    };

    public static CallToolResult Fail(AppError e)
    {
        var error = new JsonObject { ["code"] = e.Code, ["message"] = e.Message, ["retryable"] = e.Retryable };
        if (e.Outcome != null) error["outcome"] = e.Outcome;
        if (e.Details != null) error["details"] = JsonNode.Parse(e.Details.ToJsonString());
        return Wrap(error);
    }

    public static CallToolResult Fail(string code, string message, bool retryable = false)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message, ["retryable"] = retryable };
        return Wrap(error);
    }

    static CallToolResult Wrap(JsonObject error)
    {
        var body = new JsonObject { ["error"] = error };
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = body.ToJsonString() }],
            StructuredContent = JsonSerializer.SerializeToElement(body),
        };
    }
}
