using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>Tool results as JSON text, written with the REST API's JSON settings so tools and routes show the same objects.</summary>
    public sealed class McpJson(IOptions<JsonOptions> mvc)
    {
        public JsonNode? ToNode(object? value) =>
            value as JsonNode ?? JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), mvc.Value.JsonSerializerOptions);

        public CallToolResult Result(object? value) => Text(ToNode(value)?.ToJsonString() ?? "null");

        public static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

        public static CallToolResult Error(string message) => McpGuard.Error(message);

        /// <summary>An argument of any JSON type as a node; <c>null</c> for JSON null or a missing argument.</summary>
        public static JsonNode? Argument(JsonElement value) =>
            value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText());
    }
}
