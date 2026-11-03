using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services.Tools;

namespace RIoT2.Net.Orchestrator.Services.Mcp.Tools
{
    /// <summary>
    /// The <see cref="McpToolGroups.Variables"/> tool: <c>set_variable</c> changes a variable's value, not its definition. The saved variable
    /// is published as a report, so workflows that watch it run.
    /// </summary>
    [McpServerToolType]
    public sealed class VariableTools(IStoredObjectStore store)
    {
        [McpServerTool(Name = "set_variable", Title = "Set a variable", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
        [McpToolGroup(McpToolGroups.Variables, Write = true)]
        [Description("Sets a variable's value. Workflows that watch the variable run. The value must fit the variable's type in list_values.")]
        public CallToolResult SetVariable(
            [Description("The variable's id from list_values.")] string id,
            [Description("The new value: a boolean, number, text or object, as the variable's type expects.")] JsonElement value)
        {
            if (string.IsNullOrWhiteSpace(id) || store.Get<Variable>(id) is not { } variable)
                return McpJson.Error($"There is no variable '{id}'.");
            var (converted, error) = VariableValues.Convert(McpJson.Argument(value), variable.Value == null ? null : variable.Type);
            if (error != null)
                return McpJson.Error(error);

            var saved = new Variable { Id = variable.Id, Name = variable.Name, Description = variable.Description, IsPersistant = variable.IsPersistant, Value = converted };
            if (store.Save(saved) == null)
                return McpJson.Error("The variable could not be stored.");
            return McpJson.Text(new JsonObject { ["id"] = saved.Id, ["name"] = saved.Name, ["value"] = RiotTools.Value(saved.Value) }.ToJsonString());
        }
    }

    /// <summary>Variable values from JSON, as the REST API stores them: numbers as integers or doubles.</summary>
    public static class VariableValues
    {
        /// <summary>
        /// <paramref name="value"/> as a value for a variable of <paramref name="type"/> (any type when <c>null</c>): numbers and
        /// booleans may arrive as text. The problem when it doesn't fit.
        /// </summary>
        public static (object? Value, string? Error) Convert(JsonNode? value, Core.ValueType? type)
        {
            switch (type)
            {
                case Core.ValueType.Number:
                    var text = value is JsonValue v && v.GetValueKind() is JsonValueKind.Number or JsonValueKind.String ? v.ToString() : null;
                    if (text != null && !text.Contains('.') && !text.Contains('e', StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                        return (i, null);
                    if (text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                        return (d, null);
                    return (null, "The variable is a number; give a number.");
                case Core.ValueType.Boolean:
                    if (value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                        return (b.GetValue<bool>(), null);
                    if (value is JsonValue s && s.GetValueKind() == JsonValueKind.String && bool.TryParse(s.GetValue<string>(), out var parsed))
                        return (parsed, null);
                    return (null, "The variable is a boolean; give true or false.");
                case Core.ValueType.Text:
                    if (value is JsonValue t && t.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        return (t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : t.ToJsonString(), null);
                    return (null, "The variable is text; give text.");
                default:
                    if (value?.ToJsonString().Length > 64 * 1024)
                        return (null, "The value is larger than 64 KB.");
                    return (RiotTools.ToObject(value), null);
            }
        }
    }
}
