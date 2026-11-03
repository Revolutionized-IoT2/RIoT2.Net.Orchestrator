using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RIoT2.Core;
using RIoT2.Elsa.Workflows.Management;

namespace RIoT2.Net.Orchestrator.Services.Workflows
{
    /// <summary>
    /// The remote engine (RIOT2_WORKFLOW_MODE=remote, ADR 0016): the online workflow host's <c>/riot/workflows</c> API, at
    /// the <c>nodeBaseUrl</c> it announces. Calls send <c>X-RIoT2-Key</c> and pass the outbound URL policy (purpose
    /// <c>workflow</c>). While no workflow host is online, or it fails, the calls throw
    /// <see cref="WorkflowEngineUnavailableException"/>.
    /// </summary>
    internal sealed class RemoteWorkflowEngine(IHttpClientFactory clients, IOnlineNodeService nodes) : IWorkflowEngine
    {
        public const string HttpClientName = "riot2-workflow-host";
        private const string BasePath = "/riot/workflows";
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

        public async Task<WorkflowEngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var host = FindHost();
            if (host == null)
                return new WorkflowEngineStatus(WorkflowModes.Remote, false, WorkflowCatalogBuilder.Engine, null);
            try
            {
                using var response = await Client().GetAsync(new Uri(host, "/health"), cancellationToken);
                return new WorkflowEngineStatus(WorkflowModes.Remote, response.IsSuccessStatusCode, WorkflowCatalogBuilder.Engine, host.ToString());
            }
            catch (HttpRequestException)
            {
                return new WorkflowEngineStatus(WorkflowModes.Remote, false, WorkflowCatalogBuilder.Engine, host.ToString());
            }
        }

        public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            await GetAsync<List<WorkflowSummary>>("", cancellationToken) ?? [];

        public Task<WorkflowDetails?> GetAsync(string definitionId, int? version, CancellationToken cancellationToken = default) =>
            GetAsync<WorkflowDetails>($"/{Escape(definitionId)}{(version is { } v ? $"?version={v}" : "")}", cancellationToken);

        public async Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(string definitionId, CancellationToken cancellationToken = default) =>
            await GetAsync<List<WorkflowVersion>>($"/{Escape(definitionId)}/versions", cancellationToken) ?? [];

        public async Task<WorkflowSaveResult> SaveAsync(string? definitionId, JsonObject model, bool publish, CancellationToken cancellationToken = default) =>
            await SendSaveAsync(string.IsNullOrWhiteSpace(definitionId) ? HttpMethod.Post : HttpMethod.Put,
                string.IsNullOrWhiteSpace(definitionId) ? "" : $"/{Escape(definitionId)}", new SaveWorkflowRequest(model, publish), cancellationToken)
            ?? throw new WorkflowEngineUnavailableException("The workflow host doesn't know the workflow API.");

        public async Task<WorkflowSaveResult> ImportAsync(JsonObject model, bool publish, CancellationToken cancellationToken = default) =>
            await SendSaveAsync(HttpMethod.Post, "/import", new SaveWorkflowRequest(model, publish), cancellationToken)
            ?? throw new WorkflowEngineUnavailableException("The workflow host doesn't know the workflow API.");

        public Task<WorkflowSaveResult?> PublishAsync(string definitionId, CancellationToken cancellationToken = default) =>
            SendSaveAsync(HttpMethod.Post, $"/{Escape(definitionId)}/publish", null, cancellationToken);

        public Task<bool> RetractAsync(string definitionId, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Post, $"/{Escape(definitionId)}/retract", cancellationToken);

        public Task<bool> DeleteAsync(string definitionId, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Delete, $"/{Escape(definitionId)}", cancellationToken);

        public Task<WorkflowExport?> ExportAsync(string definitionId, CancellationToken cancellationToken = default) =>
            GetAsync<WorkflowExport>($"/{Escape(definitionId)}/export", cancellationToken);

        public async Task<WorkflowRunsPage> ListRunsAsync(string? definitionId, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            var query = $"/runs?page={page}&pageSize={pageSize}"
                + (string.IsNullOrWhiteSpace(definitionId) ? "" : $"&definitionId={Uri.EscapeDataString(definitionId)}")
                + (string.IsNullOrWhiteSpace(status) ? "" : $"&status={Uri.EscapeDataString(status)}");
            return await GetAsync<WorkflowRunsPage>(query, cancellationToken) ?? new WorkflowRunsPage([], 0, page, pageSize);
        }

        public Task<WorkflowRunDetails?> GetRunAsync(string instanceId, CancellationToken cancellationToken = default) =>
            GetAsync<WorkflowRunDetails>($"/runs/{Escape(instanceId)}", cancellationToken);

        public Task<bool> CancelRunAsync(string instanceId, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Post, $"/runs/{Escape(instanceId)}/cancel", cancellationToken);

        public async Task<JsonObject> GetCatalogAsync(CancellationToken cancellationToken = default) =>
            await GetAsync<JsonObject>("/catalog", cancellationToken) ?? throw new WorkflowEngineUnavailableException("The workflow host has no catalog.");

        private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken) where T : class
        {
            using var response = await CallAsync(HttpMethod.Get, path, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<T>(Options, cancellationToken);
        }

        private async Task<WorkflowSaveResult?> SendSaveAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            using var response = await CallAsync(method, path, body, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            // A refused save answers 400 with its errors.
            if (response.StatusCode != HttpStatusCode.BadRequest)
                await EnsureSuccessAsync(response, cancellationToken);
            return await response.Content.ReadFromJsonAsync<WorkflowSaveResult>(Options, cancellationToken);
        }

        private async Task<bool> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
        {
            using var response = await CallAsync(method, path, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;
            await EnsureSuccessAsync(response, cancellationToken);
            return true;
        }

        private async Task<HttpResponseMessage> CallAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            var host = FindHost() ?? throw new WorkflowEngineUnavailableException("No workflow host is online.");
            using var request = new HttpRequestMessage(method, new Uri(host, BasePath + path));
            if (body != null)
                request.Content = JsonContent.Create(body, options: Options);
            try
            {
                return await Client().SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException x)
            {
                throw new WorkflowEngineUnavailableException($"The workflow host at {host} didn't answer: {x.Message}", x);
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new WorkflowEngineUnavailableException(
                $"The workflow host answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail[..Math.Min(detail.Length, 500)])}");
        }

        private HttpClient Client() => clients.CreateClient(HttpClientName);

        private Uri? FindHost()
        {
            var settings = nodes.OnlineNodes.Select(x => x.OnlineNodeSettings)
                .FirstOrDefault(x => x?.IsOnline == true && x.NodeType == NodeType.Workflow);
            return Uri.TryCreate(settings?.NodeBaseUrl, UriKind.Absolute, out var uri) ? uri : null;
        }

        private static string Escape(string value) => Uri.EscapeDataString(value);
    }
}
