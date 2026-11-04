using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Forecasts;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services.Plans;
using RIoT2.Net.Orchestrator.Services.Templates;
using CoreJson = RIoT2.Core.Utils.Json;

namespace RIoT2.Net.Orchestrator.Services.Tools
{
    /// <summary>
    /// What AI tools can see and do, shared by the Run LLM step (<c>LlmToolbox</c>, ADR 0016) and the MCP server
    /// (ADR 0018): the value catalog, current values, forecasts, commands through the command outbox and command plans
    /// (ADR 0019). Callers add their own limits (allowed commands, counts, rates); this class only validates input and
    /// reports results as JSON.
    /// </summary>
    public sealed class RiotTools(
        ITemplateCatalog templates,
        IMessageStateService state,
        IStoredObjectStore storedObjects,
        IOrchestratorConfigurationService configuration,
        ICommandOutbox commands,
        IForecastStore? forecasts = null,
        ICommandPlanService? plans = null)
    {
        public const string ReportKind = "report";
        public const string CommandKind = "command";
        public const string VariableKind = "variable";
        public static readonly IReadOnlyList<string> Kinds = [ReportKind, CommandKind, VariableKind];

        /// <summary>The longest a tool waits for a device's command result.</summary>
        public static readonly TimeSpan MaxCommandWait = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long past its time-to-live a tool keeps waiting for the delivery: the outbox expires it on its next step, so
        /// stopping exactly at the time-to-live could report it still <c>sent</c>.
        /// </summary>
        public static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The reports, commands and variables, with their ids, names, nodes, devices and types: only <paramref name="kind"/>
        /// when given, and only those whose name, id, node or device contains <paramref name="query"/> (any case) when given.
        /// </summary>
        public JsonArray List(string? kind, string? query = null)
        {
            var items = new JsonArray();
            if (Matches(kind, ReportKind))
                foreach (var x in templates.GetReportTemplates())
                {
                    Add(items, query, ReportKind, x.Id, x.Name, x.Node, x.Device, x.Type);
                    if (IsForecast(x) && items.Count > 0 && (string?)items[^1]!["id"] == x.Id)
                        items[^1]!["forecast"] = true;
                }
            if (Matches(kind, CommandKind))
                foreach (var x in templates.GetCommandTemplates())
                    Add(items, query, CommandKind, x.Id, x.Name, x.Node, x.Device, x.Type);
            if (Matches(kind, VariableKind))
                foreach (var x in templates.GetVariableTemplates())
                    Add(items, query, VariableKind, x.Id, x.Name, null, null, x.Type);
            return items;
        }

        /// <summary>The current value of a report, command or variable; an object with <c>error</c> when there is none.</summary>
        public JsonObject Read(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return Error("An id is required.");
            if (state.Reports.FirstOrDefault(x => x.Id == id) is { } report)
                return new JsonObject { ["kind"] = ReportKind, ["id"] = id, ["value"] = Value(report.Value), ["timeStamp"] = report.TimeStamp };
            if (state.Commands.FirstOrDefault(x => x.Id == id) is { } command)
                return new JsonObject { ["kind"] = CommandKind, ["id"] = id, ["value"] = Value(command.Value) };
            if (storedObjects.Get<Variable>(id) is { } variable)
                return new JsonObject { ["kind"] = VariableKind, ["id"] = id, ["name"] = variable.Name, ["value"] = Value(variable.Value) };
            if (templates.FindReportTemplate(id) != null || templates.GetCommandTemplates().Any(x => x.Id == id))
                return new JsonObject { ["id"] = id, ["value"] = null, ["note"] = "No value has been received yet." };
            return Error($"There is no report, command or variable '{id}'.");
        }

        /// <summary>
        /// Queues a command for its node, with <paramref name="wait"/> (1 s to <see cref="MaxCommandWait"/>) as its time-to-live,
        /// and waits for its final status, at most <see cref="ExpiryGrace"/> longer. <c>null</c> when no node has the command.
        /// </summary>
        public async Task<CommandDelivery?> SendAsync(string id, JsonNode? value, TimeSpan wait, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var nodeId = configuration.FindNodeId(id);
            if (string.IsNullOrEmpty(nodeId))
                return null;

            var command = CreateCommand(id, value);
            if (wait > MaxCommandWait)
                wait = MaxCommandWait;
            if (wait < TimeSpan.FromSeconds(1))
                wait = TimeSpan.FromSeconds(1);
            var delivery = commands.Enqueue(command, nodeId, wait);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait + ExpiryGrace);
            CommandDelivery? final = null;
            try
            {
                final = await commands.WaitAsync(delivery.CorrelationId, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            return final ?? commands.Get(delivery.CorrelationId) ?? delivery;
        }

        /// <summary>Whether a report's value is a forecast series: its schema says so, or a series has been received.</summary>
        public bool IsForecast(NodeReportTemplate template) =>
            template.Schema?.Semantic == ValueSemantics.Forecast || forecasts?.Get(template.Id) != null;

        /// <summary>
        /// A window of a forecast report (<see cref="ForecastWindows"/>; times as ISO 8601) with its figures, the points and the
        /// <paramref name="rank"/> lowest and highest points; an object with <c>error</c> when there is none.
        /// </summary>
        public JsonObject ReadForecast(string? id, string? window = null, int? hours = null, string? from = null, string? to = null, int? rank = null)
        {
            if (forecasts == null)
                return Error("Forecasts are not available here.");
            if (string.IsNullOrWhiteSpace(id))
                return Error("An id is required.");
            if (!TryTime(from, out var start) || !TryTime(to, out var end))
                return Error("from and to must be ISO 8601 times, such as 2026-11-05T06:00:00Z.");
            ForecastSummary? summary;
            try
            {
                summary = forecasts.Query(id, string.IsNullOrWhiteSpace(window) ? ForecastWindows.Next : window, hours, start, end, rank ?? 0);
            }
            catch (ArgumentException x)
            {
                return Error(x.Message);
            }
            if (summary == null)
                return Error(templates.FindReportTemplate(id) == null ? $"There is no report '{id}'." : $"The report '{id}' has no forecast yet.");
            return JsonNode.Parse(CoreJson.Serialize(summary)!)!.AsObject();
        }

        /// <summary>
        /// Gives a command a plan of future values (<c>steps</c>: a list of <c>{ at, value }</c>), which replaces its active plan;
        /// the plan without its steps' details, or an object with <c>error</c>.
        /// </summary>
        public JsonObject PlanCommand(string id, JsonNode? steps, string? name, string source)
        {
            if (plans == null)
                return Error("Command plans are not available here.");
            if (steps is not JsonArray list)
                return Error("steps must be a list of { at, value }.");
            List<CommandPlanStepRequest>? requested;
            try
            {
                requested = list.Deserialize<List<CommandPlanStepRequest>>(CommandPlanStore.Json);
            }
            catch (JsonException x)
            {
                return Error($"The steps can't be read: {x.Message}");
            }
            var result = plans.Submit(new CommandPlanRequest { CommandId = id, Name = name, Steps = requested, Source = source });
            if (!result.Succeeded)
                return Error(result.Error ?? "The plan was refused.");
            return PlanSummary(result.Plan!);
        }

        /// <summary>The command plans, newest first: only <paramref name="commandId"/>'s and only active ones when asked.</summary>
        public JsonArray ListPlans(string? commandId, bool activeOnly, int limit = 20)
        {
            var items = new JsonArray();
            foreach (var plan in plans?.List(commandId, activeOnly, limit) ?? [])
                items.Add(PlanSummary(plan));
            return items;
        }

        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ssK";

        private static JsonObject PlanSummary(CommandPlan plan) => new()
        {
            ["planId"] = plan.Id,
            ["commandId"] = plan.CommandId,
            ["name"] = plan.Name,
            ["source"] = plan.Source,
            ["status"] = plan.Status,
            ["createdAt"] = plan.CreatedAt.ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["next"] = plan.Next?.ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["steps"] = new JsonArray(plan.Steps.Select(x => (JsonNode)new JsonObject
            {
                ["at"] = x.At.ToString(TimeFormat, CultureInfo.InvariantCulture),
                ["value"] = x.Value?.DeepClone(),
                ["status"] = x.Status
            }).ToArray())
        };

        private static bool TryTime(string? text, out DateTimeOffset? time)
        {
            time = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;
            if (!DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;
            time = parsed;
            return true;
        }

        /// <summary>A command with the value a workflow's JavaScript would give, after the same JSON round trip as on the wire.</summary>
        public static Command CreateCommand(string id, JsonNode? value) =>
            CoreJson.Deserialize<Command>(CoreJson.Serialize(new Command { Id = id, Value = new ValueModel(ToObject(value)) }))
                ?? throw new InvalidOperationException("The command could not be read.");

        /// <summary>A value as JSON: what <see cref="CoreJson"/> writes for it, or its text.</summary>
        public static JsonNode? Value(object? value)
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

        /// <summary>A JSON value as the plain value a workflow's JavaScript produces: dictionaries, lists, booleans, numbers and text.</summary>
        public static object? ToObject(JsonNode? node) => node switch
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

        public static JsonObject Error(string message) => new() { ["error"] = message };

        private static bool Matches(string? kind, string candidate) => string.IsNullOrEmpty(kind) || kind == candidate;

        private static void Add(JsonArray items, string? query, string kind, string id, string name, string? node, string? device, Core.ValueType type)
        {
            var text = query?.Trim();
            if (!string.IsNullOrEmpty(text) && !new[] { id, name, node, device }.Any(x => x?.Contains(text, StringComparison.OrdinalIgnoreCase) == true))
                return;
            items.Add(new JsonObject
            {
                ["kind"] = kind,
                ["id"] = id,
                ["name"] = name,
                ["node"] = node,
                ["device"] = device,
                ["type"] = type.ToString()
            });
        }
    }
}
