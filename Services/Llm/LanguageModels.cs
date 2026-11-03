using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RIoT2.Net.Orchestrator.Services.Llm
{
    // The language model adapters (ADR 0016, after the Dls LLM port): OpenAI's chat completions (OpenAI, Azure OpenAI and
    // OpenAI-compatible servers such as Ollama) and Anthropic's messages API, over one HttpClient that never follows
    // redirects and passes the outbound URL policy (purpose "llm").

    /// <summary>A message of the conversation: user, assistant (with its tool calls) or tool (a tool call's result).</summary>
    public sealed record ModelMessage(string Role, string? Content, IReadOnlyList<ModelToolCall>? ToolCalls = null, string? ToolCallId = null)
    {
        public static ModelMessage User(string content) => new("user", content);
        public static ModelMessage Assistant(string? content, IReadOnlyList<ModelToolCall> toolCalls) => new("assistant", content, toolCalls);
        public static ModelMessage Tool(string toolCallId, string content) => new("tool", content, null, toolCallId);
    }

    /// <summary>A tool the model called, with its arguments as JSON text.</summary>
    public sealed record ModelToolCall(string Id, string Name, string Arguments);

    /// <summary>A tool the model may call; <see cref="Parameters"/> is its arguments' JSON schema.</summary>
    public sealed record ModelTool(string Name, string Description, JsonObject Parameters);

    /// <summary>One turn: the system prompt, the conversation, the tools, and whether the answer must be JSON (with a schema).</summary>
    public sealed record ModelRequest(string System, IReadOnlyList<ModelMessage> Messages, IReadOnlyList<ModelTool> Tools, bool JsonAnswer, JsonObject? JsonSchema);

    /// <summary>The model's turn: its text, or the tools it wants called, and the tokens used.</summary>
    public sealed record ModelResponse(string? Text, IReadOnlyList<ModelToolCall> ToolCalls, int InputTokens, int OutputTokens);

    /// <summary>The provider refused or failed the call.</summary>
    public sealed class LanguageModelException(string message, Exception? inner = null) : Exception(message, inner);

    public interface ILanguageModel
    {
        Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Opens a model for a connection.</summary>
    public interface ILanguageModels
    {
        ILanguageModel Open(LlmConnection connection, string? apiKey);
    }

    public sealed class HttpLanguageModels(IHttpClientFactory clients) : ILanguageModels
    {
        public const string HttpClientName = "riot2-llm";
        public const string OutboundPurpose = "llm";
        public const int DefaultMaxOutputTokens = 1024;

        public ILanguageModel Open(LlmConnection connection, string? apiKey)
        {
            ArgumentNullException.ThrowIfNull(connection);
            var client = clients.CreateClient(HttpClientName);
            return connection.Provider == LlmProviders.Anthropic
                ? new AnthropicModel(client, connection, apiKey)
                : new OpenAiChatModel(client, connection, apiKey);
        }

        internal static Uri Combine(string endpoint, string path) => new(endpoint.TrimEnd('/') + "/" + path.TrimStart('/'));

        internal static async Task<JsonObject> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException x)
            {
                throw new LanguageModelException($"The model didn't answer: {x.Message}", x);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonObject? json = null;
                try
                {
                    json = JsonNode.Parse(text) as JsonObject;
                }
                catch (JsonException)
                {
                }
                if (!response.IsSuccessStatusCode)
                {
                    var message = json?["error"] switch
                    {
                        JsonObject error => error["message"]?.ToString(),
                        JsonValue error => error.ToString(),
                        _ => null
                    };
                    throw new LanguageModelException($"The model answered HTTP {(int)response.StatusCode}{(message == null ? "" : ": " + message)}");
                }
                return json ?? throw new LanguageModelException("The model's answer is not JSON.");
            }
        }

        internal static int Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    }

    /// <summary>OpenAI chat completions, as OpenAI, Azure OpenAI and compatible servers (Ollama, LM Studio, vLLM) serve them.</summary>
    internal sealed class OpenAiChatModel(HttpClient client, LlmConnection connection, string? apiKey) : ILanguageModel
    {
        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
            foreach (var turn in request.Messages)
            {
                var item = new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content };
                if (turn.ToolCalls is { Count: > 0 } calls)
                {
                    item["tool_calls"] = new JsonArray(calls.Select(x => (JsonNode)new JsonObject
                    {
                        ["id"] = x.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = x.Name, ["arguments"] = x.Arguments }
                    }).ToArray());
                }
                if (turn.ToolCallId != null)
                    item["tool_call_id"] = turn.ToolCallId;
                messages.Add(item);
            }

            var body = new JsonObject { ["messages"] = messages };
            if (connection.Provider != LlmProviders.AzureOpenAI)
                body["model"] = connection.Model;
            // OpenAI's newer models only take max_completion_tokens; compatible servers know max_tokens.
            body[connection.Provider == LlmProviders.OpenAICompatible ? "max_tokens" : "max_completion_tokens"] =
                connection.MaxOutputTokens ?? HttpLanguageModels.DefaultMaxOutputTokens;
            if (connection.Temperature is { } temperature)
                body["temperature"] = temperature;
            if (request.Tools.Count > 0)
            {
                body["tools"] = new JsonArray(request.Tools.Select(x => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = x.Name, ["description"] = x.Description, ["parameters"] = x.Parameters.DeepClone() }
                }).ToArray());
            }
            if (request.JsonAnswer)
            {
                body["response_format"] = request.JsonSchema is { } schema
                    ? new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "answer", ["schema"] = schema.DeepClone(), ["strict"] = false } }
                    : new JsonObject { ["type"] = "json_object" };
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, Url()) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(apiKey))
            {
                if (connection.Provider == LlmProviders.AzureOpenAI)
                    message.Headers.Add("api-key", apiKey);
                else
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var json = await HttpLanguageModels.SendAsync(client, message, cancellationToken);
            var answer = json["choices"]?[0]?["message"] as JsonObject ?? throw new LanguageModelException("The model's answer has no message.");
            var toolCalls = (answer["tool_calls"] as JsonArray ?? [])
                .Select(x => new ModelToolCall(
                    x?["id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                    x?["function"]?["name"]?.ToString() ?? "",
                    x?["function"]?["arguments"] switch { JsonValue text when text.TryGetValue<string>(out var s) => s, { } other => other.ToJsonString(), null => "{}" }))
                .ToList();
            var usage = json["usage"];
            return new ModelResponse(answer["content"]?.ToString(), toolCalls,
                HttpLanguageModels.Int(usage?["prompt_tokens"]), HttpLanguageModels.Int(usage?["completion_tokens"]));
        }

        private Uri Url() => connection.Provider switch
        {
            LlmProviders.AzureOpenAI => HttpLanguageModels.Combine(connection.Endpoint!,
                $"openai/deployments/{Uri.EscapeDataString(connection.Model)}/chat/completions?api-version={Uri.EscapeDataString(connection.ApiVersion ?? LlmProviders.DefaultAzureApiVersion)}"),
            _ => HttpLanguageModels.Combine(connection.Endpoint ?? "https://api.openai.com/v1", "chat/completions")
        };
    }

    /// <summary>Anthropic's messages API. It has no JSON mode: the system prompt asks for JSON, and the answer is checked.</summary>
    internal sealed class AnthropicModel(HttpClient client, LlmConnection connection, string? apiKey) : ILanguageModel
    {
        public const string Version = "2023-06-01";

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var messages = new JsonArray();
            foreach (var turn in request.Messages)
            {
                switch (turn.Role)
                {
                    case "assistant":
                        var blocks = new JsonArray();
                        if (!string.IsNullOrEmpty(turn.Content))
                            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = turn.Content });
                        foreach (var call in turn.ToolCalls ?? [])
                            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = ParseObject(call.Arguments) });
                        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                        break;
                    case "tool":
                        var result = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = turn.ToolCallId, ["content"] = turn.Content };
                        // Consecutive tool results go in one user message.
                        if (messages.LastOrDefault() is JsonObject { } last && last["role"]?.ToString() == "user" && last["content"] is JsonArray results
                            && results.All(x => x?["type"]?.ToString() == "tool_result"))
                            results.Add(result);
                        else
                            messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray { result } });
                        break;
                    default:
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = turn.Content });
                        break;
                }
            }

            var body = new JsonObject
            {
                ["model"] = connection.Model,
                ["max_tokens"] = connection.MaxOutputTokens ?? HttpLanguageModels.DefaultMaxOutputTokens,
                ["system"] = request.System,
                ["messages"] = messages
            };
            if (connection.Temperature is { } temperature)
                body["temperature"] = Math.Min(temperature, 1);
            if (request.Tools.Count > 0)
            {
                body["tools"] = new JsonArray(request.Tools.Select(x => (JsonNode)new JsonObject
                {
                    ["name"] = x.Name,
                    ["description"] = x.Description,
                    ["input_schema"] = x.Parameters.DeepClone()
                }).ToArray());
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, HttpLanguageModels.Combine(connection.Endpoint ?? "https://api.anthropic.com", "v1/messages"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            message.Headers.Add("anthropic-version", Version);
            if (!string.IsNullOrEmpty(apiKey))
                message.Headers.Add("x-api-key", apiKey);

            var json = await HttpLanguageModels.SendAsync(client, message, cancellationToken);
            var content = json["content"] as JsonArray ?? [];
            var text = string.Concat(content.Where(x => x?["type"]?.ToString() == "text").Select(x => x?["text"]?.ToString()));
            var toolCalls = content.Where(x => x?["type"]?.ToString() == "tool_use")
                .Select(x => new ModelToolCall(x?["id"]?.ToString() ?? Guid.NewGuid().ToString("N"), x?["name"]?.ToString() ?? "",
                    x?["input"]?.ToJsonString() ?? "{}"))
                .ToList();
            var usage = json["usage"];
            return new ModelResponse(text.Length == 0 ? null : text, toolCalls,
                HttpLanguageModels.Int(usage?["input_tokens"]), HttpLanguageModels.Int(usage?["output_tokens"]));
        }

        private static JsonNode ParseObject(string json)
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject();
            }
        }
    }
}
