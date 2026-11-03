using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Core.Models;
using RIoT2.Elsa.Workflows.Llm;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services.Templates;
using CoreJson = RIoT2.Core.Utils.Json;

namespace RIoT2.Net.Orchestrator.Services.Llm
{
    /// <summary>
    /// The tools a Run LLM step gives the model (ADR 0016). <see cref="LlmTools.ReadValues"/>: <c>list_values</c> and
    /// <c>read_value</c> over the reports, commands and variables. <see cref="LlmTools.SendCommands"/>: <c>send_command</c>,
    /// only for the step's allowed commands and at most its command limit, through the command outbox like every command,
    /// waiting for the device's result as long as the step's time allows (at most 30 s).
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
        public static readonly TimeSpan MaxCommandWait = TimeSpan.FromSeconds(30);

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
                ListValues when request.Tools.Contains(LlmTools.ReadValues) => List(arguments["kind"]?.ToString()).ToJsonString(),
                ReadValue when request.Tools.Contains(LlmTools.ReadValues) => Read(arguments["id"]?.ToString()).ToJsonString(),
                SendCommand when request.Tools.Contains(LlmTools.SendCommands) =>
                    (await SendAsync(arguments["id"]?.ToString(), arguments["value"], request, sent, deadline, time, cancellationToken)).ToJsonString(),
                _ => Error($"There is no tool '{call.Name}'.")
            };
        }

        private JsonArray List(string? kind)
        {
            var items = new JsonArray();
            if (kind is null or "" or "report")
                foreach (var x in templates.GetReportTemplates())
                    items.Add(Item("report", x.Id, x.Name, x.Node, x.Device, x.Type));
            if (kind is null or "" or "command")
                foreach (var x in templates.GetCommandTemplates())
                    items.Add(Item("command", x.Id, x.Name, x.Node, x.Device, x.Type));
            if (kind is null or "" or "variable")
                foreach (var x in templates.GetVariableTemplates())
                    items.Add(Item("variable", x.Id, x.Name, null, null, x.Type));
            return items;
        }

        private JsonObject Read(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return ErrorObject("An id is required.");
            if (state.Reports.FirstOrDefault(x => x.Id == id) is { } report)
                return new JsonObject { ["kind"] = "report", ["id"] = id, ["value"] = Value(report.Value), ["timeStamp"] = report.TimeStamp };
            if (state.Commands.FirstOrDefault(x => x.Id == id) is { } command)
                return new JsonObject { ["kind"] = "command", ["id"] = id, ["value"] = Value(command.Value) };
            if (storedObjects.Get<Variable>(id) is { } variable)
                return new JsonObject { ["kind"] = "variable", ["id"] = id, ["name"] = variable.Name, ["value"] = Value(variable.Value) };
            if (templates.FindReportTemplate(id) != null || templates.GetCommandTemplates().Any(x => x.Id == id))
                return new JsonObject { ["id"] = id, ["value"] = null, ["note"] = "No value has been received yet." };
            return ErrorObject($"There is no report, command or variable '{id}'.");
        }

        private async Task<JsonObject> SendAsync(string? id, JsonNode? value, LlmRunRequest request, List<LlmSentCommand> sent, DateTimeOffset deadline,
            TimeProvider time, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id) || !request.AllowedCommands.Contains(id))
                return ErrorObject($"The command '{id}' is not allowed; the allowed commands are {string.Join(", ", request.AllowedCommands)}.");
            if (sent.Count >= request.MaxCommands)
                return ErrorObject($"The limit of {request.MaxCommands} commands is reached.");
            var nodeId = configuration.FindNodeId(id);
            if (string.IsNullOrEmpty(nodeId))
            {
                sent.Add(new LlmSentCommand(id, value?.DeepClone(), CommandDeliveryStatus.UnknownCommand));
                return ErrorObject($"No node has the command '{id}'.");
            }

            var command = CoreJson.Deserialize<Command>(CoreJson.Serialize(new Command { Id = id, Value = new ValueModel(ToObject(value)) }))
                ?? throw new InvalidOperationException("The command could not be read.");
            var wait = deadline - time.GetUtcNow();
            if (wait > MaxCommandWait)
                wait = MaxCommandWait;
            if (wait < TimeSpan.FromSeconds(1))
                wait = TimeSpan.FromSeconds(1);
            var delivery = commands.Enqueue(command, nodeId, wait);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            CommandDelivery? final = null;
            try
            {
                final = await commands.WaitAsync(delivery.CorrelationId, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            final ??= commands.Get(delivery.CorrelationId) ?? delivery;
            sent.Add(new LlmSentCommand(id, value?.DeepClone(), final.Status));
            return new JsonObject { ["id"] = id, ["status"] = final.Status, ["error"] = final.Error };
        }

        private static JsonObject Item(string kind, string id, string name, string? node, string? device, Core.ValueType type) => new()
        {
            ["kind"] = kind,
            ["id"] = id,
            ["name"] = name,
            ["node"] = node,
            ["device"] = device,
            ["type"] = type.ToString()
        };

        private static JsonNode? Value(object? value)
        {
            if (value == null)
                return null;
            try
            {
                return JsonNode.Parse(CoreJson.Serialize(value) ?? "null");
            }
            catch (JsonException)
            {
                return JsonValue.Create(value.ToString());
            }
        }

        // Commands carry plain values, as a workflow's JavaScript produces them.
        private static object? ToObject(JsonNode? node) => node switch
        {
            null => null,
            JsonObject obj => obj.ToDictionary(x => x.Key, x => ToObject(x.Value)),
            JsonArray array => array.Select(ToObject).ToList(),
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<long>(out var l) => l,
            JsonValue v when v.TryGetValue<double>(out var d) => d,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString()
        };

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
