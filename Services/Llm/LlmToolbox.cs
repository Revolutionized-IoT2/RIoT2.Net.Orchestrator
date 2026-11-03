using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Elsa.Workflows.Llm;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services.Templates;
using RIoT2.Net.Orchestrator.Services.Tools;

namespace RIoT2.Net.Orchestrator.Services.Llm
{
    /// <summary>
    /// The tools a Run LLM step gives the model (ADR 0016). <see cref="LlmTools.ReadValues"/>: <c>list_values</c> and
    /// <c>read_value</c> over the reports, commands and variables. <see cref="LlmTools.SendCommands"/>: <c>send_command</c>,
    /// only for the step's allowed commands and at most its command limit, through the command outbox like every command,
    /// waiting for the device's result as long as the step's time allows (at most 30 s). The values and commands are
    /// <see cref="RiotTools"/>, which the MCP server shares (ADR 0018).
    /// </summary>
    public sealed class LlmToolbox(
        ITemplateCatalog templates,
        IMessageStateService state,
        IStoredObjectStore storedObjects,
        IOrchestratorConfigurationService configuration,
        ICommandOutbox commands)
    {
        public const string ListValues = "list_values";
        public const string ReadValue = "read_value";
        public const string SendCommand = "send_command";
        public static readonly TimeSpan MaxCommandWait = RiotTools.MaxCommandWait;

        private readonly RiotTools _tools = new(templates, state, storedObjects, configuration, commands);

        /// <summary>The tools for the step's allowed <see cref="LlmTools"/>.</summary>
        public static IReadOnlyList<ModelTool> For(LlmRunRequest request)
        {
            var tools = new List<ModelTool>();
            if (request.Tools.Contains(LlmTools.ReadValues))
            {
                tools.Add(new ModelTool(ListValues, "Lists the reports (device measurements and states), commands and variables, with their ids, names, nodes and devices.",
                    Schema(new JsonObject { ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("report", "command", "variable"), ["description"] = "Only this kind; omit for all." } })));
                tools.Add(new ModelTool(ReadValue, "Reads the current value of a report, command or variable by its id.",
                    Schema(new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } }, "id")));
            }
            if (request.Tools.Contains(LlmTools.SendCommands) && request.AllowedCommands.Count > 0 && request.MaxCommands > 0)
            {
                tools.Add(new ModelTool(SendCommand,
                    $"Sends a command to a device and returns its delivery status. Allowed commands: {string.Join(", ", request.AllowedCommands)}. At most {request.MaxCommands} per run.",
                    Schema(new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(request.AllowedCommands.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) },
                        ["value"] = new JsonObject { ["description"] = "The command's value: a boolean, number, text or object, as the command expects." }
                    }, "id", "value")));
            }
            return tools;
        }

        /// <summary>Runs one tool call and returns its result as JSON text for the model; sent commands are added to <paramref name="sent"/>.</summary>
        public async Task<string> RunAsync(ModelToolCall call, LlmRunRequest request, List<LlmSentCommand> sent, DateTimeOffset deadline, TimeProvider time, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            JsonObject arguments;
            try
            {
                arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                return Error("The arguments are not a JSON object.");
            }

            return call.Name switch
            {
                ListValues when request.Tools.Contains(LlmTools.ReadValues) => _tools.List(arguments["kind"]?.ToString()).ToJsonString(),
                ReadValue when request.Tools.Contains(LlmTools.ReadValues) => _tools.Read(arguments["id"]?.ToString()).ToJsonString(),
                SendCommand when request.Tools.Contains(LlmTools.SendCommands) =>
                    (await SendAsync(arguments["id"]?.ToString(), arguments["value"], request, sent, deadline, time, cancellationToken)).ToJsonString(),
                _ => Error($"There is no tool '{call.Name}'.")
            };
        }

        private async Task<JsonObject> SendAsync(string? id, JsonNode? value, LlmRunRequest request, List<LlmSentCommand> sent, DateTimeOffset deadline,
            TimeProvider time, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id) || !request.AllowedCommands.Contains(id))
                return ErrorObject($"The command '{id}' is not allowed; the allowed commands are {string.Join(", ", request.AllowedCommands)}.");
            if (sent.Count >= request.MaxCommands)
                return ErrorObject($"The limit of {request.MaxCommands} commands is reached.");

            var final = await _tools.SendAsync(id, value, deadline - time.GetUtcNow(), cancellationToken);
            if (final == null)
            {
                sent.Add(new LlmSentCommand(id, value?.DeepClone(), CommandDeliveryStatus.UnknownCommand));
                return ErrorObject($"No node has the command '{id}'.");
            }
            sent.Add(new LlmSentCommand(id, value?.DeepClone(), final.Status));
            return new JsonObject { ["id"] = id, ["status"] = final.Status, ["error"] = final.Error };
        }

        private static JsonObject Schema(JsonObject properties, params string[] required) => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
        };

        private static JsonObject ErrorObject(string message) => new() { ["error"] = message };

        private static string Error(string message) => ErrorObject(message).ToJsonString();
    }
}
