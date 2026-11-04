extern alias JsonSchemaNet;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Elsa.Workflows.Llm;
using JsonSchema = JsonSchemaNet::Json.Schema.JsonSchema;
using EvaluationOptions = JsonSchemaNet::Json.Schema.EvaluationOptions;
using OutputFormat = JsonSchemaNet::Json.Schema.OutputFormat;

namespace RIoT2.Net.Orchestrator.Services.Llm
{
    /// <summary>
    /// Runs Run LLM steps (ADR 0016, after the Dls LLM step): in process for the embedded workflow engine, and for the
    /// remote workflow host through <c>POST /api/llm/run</c>. The model gets the instructions and the input, may call the
    /// step's tools until it answers or the tool call limit or the timeout is reached, and a JSON answer is parsed and
    /// checked against the step's schema. Every step is recorded in the usage store, whatever its outcome.
    /// </summary>
    public sealed class LlmService(
        LlmConnectionService connections,
        ILanguageModels models,
        LlmToolbox toolbox,
        LlmUsageStore usage,
        TimeProvider time,
        ILogger<LlmService> logger) : ILlmRunner
    {
        public async Task<LlmRunResult> RunAsync(LlmRunRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Validate() is { } problem)
                return LlmRunResult.Failure(LlmErrorCodes.InvalidRequest, problem);
            var connection = connections.FindByName(request.Connection);
            if (connection == null)
                return LlmRunResult.Failure(LlmErrorCodes.UnknownConnection, $"There is no LLM connection named '{request.Connection}'.");
            if (!connection.Enabled)
                return LlmRunResult.Failure(LlmErrorCodes.ConnectionDisabled, $"The LLM connection '{connection.Name}' is disabled.");

            var started = Stopwatch.GetTimestamp();
            var result = await RunAsync(connection, request, cancellationToken);
            try
            {
                usage.Add(new LlmUsageRecord(time.GetUtcNow(), connection.Name, connection.Provider, connection.Model, request.WorkflowDefinitionId,
                    request.WorkflowInstanceId, request.ActivityId, result.Succeeded, result.ErrorCode, result.Usage.InputTokens, result.Usage.OutputTokens,
                    result.ToolCalls, result.Commands.Count, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
            catch (Exception x) when (x is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
            {
                logger.LogWarning(x, "Could not record the usage of a Run LLM step");
            }
            return result;
        }

        private async Task<LlmRunResult> RunAsync(LlmConnection connection, LlmRunRequest request, CancellationToken cancellationToken)
        {
            string? apiKey;
            try
            {
                apiKey = connections.ReadApiKey(connection);
            }
            catch (CryptographicException)
            {
                return LlmRunResult.Failure(LlmErrorCodes.ProviderError, $"The API key of '{connection.Name}' can't be read on this orchestrator; enter it again.");
            }

            var model = models.Open(connection, apiKey);
            var tools = LlmToolbox.For(request);
            var json = request.ResponseFormat == LlmResponseFormats.Json;
            var schema = request.JsonSchema as JsonObject;
            var messages = new List<ModelMessage> { ModelMessage.User(UserMessage(request)) };
            var sent = new List<LlmSentCommand>();
            var deadline = time.GetUtcNow().AddSeconds(request.TimeoutSeconds);
            int input = 0, output = 0, toolCalls = 0;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            try
            {
                while (true)
                {
                    var response = await model.CompleteAsync(new ModelRequest(SystemPrompt(request, tools, schema, time.GetUtcNow()), messages, tools, json, schema), timeout.Token);
                    input += response.InputTokens;
                    output += response.OutputTokens;
                    if (response.ToolCalls.Count == 0)
                        return Answer(response.Text, json, schema, new LlmUsage(input, output), toolCalls, sent);

                    if (toolCalls + response.ToolCalls.Count > request.MaxToolCalls)
                    {
                        return LlmRunResult.Failure(LlmErrorCodes.ToolCallLimit,
                            $"The model wanted more than {request.MaxToolCalls} tool calls.", new LlmUsage(input, output), toolCalls, sent);
                    }

                    messages.Add(ModelMessage.Assistant(response.Text, response.ToolCalls));
                    foreach (var call in response.ToolCalls)
                    {
                        toolCalls++;
                        var toolResult = await toolbox.RunAsync(call, request, sent, deadline, time, timeout.Token);
                        messages.Add(ModelMessage.Tool(call.Id, toolResult));
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return LlmRunResult.Failure(LlmErrorCodes.Timeout, $"No answer within {request.TimeoutSeconds} s.", new LlmUsage(input, output), toolCalls, sent);
            }
            catch (LanguageModelException x)
            {
                logger.LogWarning("The LLM connection {Connection} failed: {Error}", connection.Name, x.Message);
                return LlmRunResult.Failure(LlmErrorCodes.ProviderError, x.Message, new LlmUsage(input, output), toolCalls, sent);
            }
        }

        /// <summary>The final answer: as it is, or with a JSON answer parsed and checked against the schema.</summary>
        public static LlmRunResult Answer(string? text, bool json, JsonObject? schema, LlmUsage usage, int toolCalls, IReadOnlyList<LlmSentCommand> sent)
        {
            if (!json)
                return new LlmRunResult { Succeeded = true, Text = text, Usage = usage, ToolCalls = toolCalls, Commands = sent };

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(StripFences(text ?? ""));
            }
            catch (JsonException x)
            {
                return LlmRunResult.Failure(LlmErrorCodes.InvalidJson, $"The answer is not JSON: {x.Message}", usage, toolCalls, sent) with { Text = text };
            }

            if (schema != null)
            {
                var evaluation = JsonSchema.FromText(schema.ToJsonString()).Evaluate(JsonSerializer.SerializeToElement(node), new EvaluationOptions { OutputFormat = OutputFormat.List });
                if (!evaluation.IsValid)
                {
                    var errors = (evaluation.Details ?? []).Where(x => x.Errors != null)
                        .SelectMany(x => x.Errors!.Select(e => $"{x.InstanceLocation}: {e.Value}")).Take(5).ToList();
                    return LlmRunResult.Failure(LlmErrorCodes.InvalidJson,
                        $"The answer doesn't match the schema{(errors.Count > 0 ? ": " + string.Join("; ", errors) : ".")}", usage, toolCalls, sent) with { Text = text, Json = node };
                }
            }
            return new LlmRunResult { Succeeded = true, Text = text, Json = node, Usage = usage, ToolCalls = toolCalls, Commands = sent };
        }

        private static string SystemPrompt(LlmRunRequest request, IReadOnlyList<ModelTool> tools, JsonObject? schema, DateTimeOffset now)
        {
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
            var prompt = new StringBuilder("You are a step of an automation workflow in RIoT2, a home and building automation system. "
                + "Do what the instructions ask, using the input data. Be brief and factual. "
                + $"The time now is {local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)} ({local.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture)}).");
            if (tools.Any(x => x.Name == LlmToolbox.ReadValue))
                prompt.Append(" Use list_values and read_value to look up the current reports, commands and variables when you need them, and read_forecast for forecasts and prices ahead.");
            if (tools.Any(x => x.Name == LlmToolbox.SendCommand))
                prompt.Append($" Send a command with send_command only when the instructions call for it; you may send at most {request.MaxCommands}.");
            if (tools.Any(x => x.Name == LlmToolbox.PlanCommand))
                prompt.Append(" Plan a command's future values with plan_command when the instructions ask for a schedule; times are ISO 8601 with a time zone.");
            if (request.ResponseFormat == LlmResponseFormats.Json)
            {
                prompt.Append(" Answer with a single JSON value only, without any other text or code fences.");
                if (schema != null)
                    prompt.Append(" It must match this JSON schema: ").Append(schema.ToJsonString());
            }
            return prompt.ToString();
        }

        private static string UserMessage(LlmRunRequest request)
        {
            var message = new StringBuilder("Instructions:\n").Append(request.Instructions.Trim());
            if (request.Input != null)
                message.Append("\n\nInput data (JSON):\n").Append(request.Input.ToJsonString());
            return message.ToString();
        }

        private static string StripFences(string text)
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return trimmed;
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            return start > 0 && end > start ? trimmed[(start + 1)..end].Trim() : trimmed.Trim('`').Trim();
        }
    }
}
