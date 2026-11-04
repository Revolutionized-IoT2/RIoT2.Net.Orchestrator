using System.Net;
using System.Text;
using System.Text.Json;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Orchestrator.Services.DeviceSignIn
{
    /// <summary>An answer of <see cref="DeviceAuthorizationService"/>: the HTTP status, and a message unless it succeeded.</summary>
    public sealed record DeviceAuthorizationOutcome(int StatusCode, string? Message, DeviceAuthorizationStartResult? Result = null)
    {
        public bool Succeeded => StatusCode == StatusCodes.Status200OK;
    }

    /// <summary>
    /// What the callback did: its status, the pending sign-in it took (<c>null</c> when the state was unknown or
    /// expired, so nothing may be posted to a window), and the text the page shows.
    /// </summary>
    public sealed record DeviceAuthorizationCallbackOutcome(int StatusCode, PendingDeviceAuthorization? Pending, bool Success, string Message);

    /// <summary>
    /// Runs a device's account sign-in (Core 2.4.0) between the UI, the provider and the node: the start creates the
    /// state and asks the node for the provider's sign-in page; the callback takes the state and hands the code to
    /// the node. The code and the state are never logged.
    /// </summary>
    public sealed class DeviceAuthorizationService
    {
        internal const int MaxIdLength = 200;
        internal const int MaxUriLength = 2048;
        internal const int MaxCodeLength = 4096;
        internal const int MaxStateLength = 128;
        private const int MaxMessageLength = 500;

        private readonly DeviceAuthorizationStateStore _states;
        private readonly IOnlineNodeService _nodes;
        private readonly IHttpClientFactory _clients;
        private readonly ILogger<DeviceAuthorizationService> _logger;

        public DeviceAuthorizationService(DeviceAuthorizationStateStore states, IOnlineNodeService nodes, IHttpClientFactory clients,
            ILogger<DeviceAuthorizationService> logger)
        {
            _states = states;
            _nodes = nodes;
            _clients = clients;
            _logger = logger;
        }

        /// <summary>
        /// The redirect URI the provider sends the browser back to: absolute http(s), without user info, query or
        /// fragment, and the path <see cref="Constants.ApiOAuthCallbackPath"/> (any case). Behind the UI's
        /// <c>/api</c> proxy it is on the UI's origin.
        /// </summary>
        public static bool IsValidRedirectUri(string? redirectUri, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(redirectUri) || redirectUri.Length > MaxUriLength
                || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                error = "redirectUri must be an absolute http or https URL.";
            else if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || redirectUri.Contains('?') || redirectUri.Contains('#'))
                error = "redirectUri must not have user info, a query or a fragment.";
            else if (!string.Equals(uri.AbsolutePath, Constants.ApiOAuthCallbackPath, StringComparison.OrdinalIgnoreCase))
                error = $"redirectUri must end with {Constants.ApiOAuthCallbackPath}.";
            return error == null;
        }

        /// <summary>
        /// The window origin the result is posted to: an absolute http(s) <c>scheme://host[:port]</c>, without a path,
        /// query, fragment or user info. Returns it normalized, as a browser compares it.
        /// </summary>
        public static bool TryNormalizeOrigin(string? origin, out string normalized, out string? error)
        {
            normalized = string.Empty;
            error = null;
            if (string.IsNullOrWhiteSpace(origin) || origin.Length > MaxUriLength
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                error = "origin must be an absolute http or https origin.";
            else if (!IsBareAuthority(origin, uri))
                error = "origin must be scheme://host[:port], without a path.";
            else
                normalized = uri.GetLeftPart(UriPartial.Authority);
            return error == null;
        }

        /// <summary>
        /// Starts a sign-in for a device on an online node: 200 with the provider's sign-in page, 400 for a bad
        /// request, 404 for an unknown or offline node or a device the node doesn't have, 409 when the device's
        /// configuration isn't ready, 429 when too many sign-ins wait, and 502 when the node failed or didn't answer.
        /// </summary>
        public async Task<DeviceAuthorizationOutcome> StartAsync(string? nodeId, string? deviceId, string? redirectUri, string? origin,
            string? user, CancellationToken cancellationToken)
        {
            if (!IsValidId(nodeId) || !IsValidId(deviceId))
                return new(StatusCodes.Status400BadRequest, "A node id and a device id are required.");
            if (!IsValidRedirectUri(redirectUri, out var redirectError))
                return new(StatusCodes.Status400BadRequest, redirectError);
            if (!TryNormalizeOrigin(origin, out var normalizedOrigin, out var originError))
                return new(StatusCodes.Status400BadRequest, originError);

            var baseUrl = NodeBaseUrl(nodeId!);
            if (baseUrl == null)
                return new(StatusCodes.Status404NotFound, "The node is not online.");

            var state = _states.Create(nodeId!, deviceId!, redirectUri!, normalizedOrigin, user);
            if (state == null)
            {
                _logger.LogWarning("Device sign-in for {DeviceId} on node {NodeId} refused: too many sign-ins are waiting", deviceId, nodeId);
                return new(StatusCodes.Status429TooManyRequests, "Too many device sign-ins are waiting. Try again in a few minutes.");
            }

            var body = new DeviceAuthorizationStart { RedirectUri = redirectUri!, State = state };
            var answer = await PostAsync(baseUrl, Constants.ApiDeviceAuthorizationStartUrl, deviceId!, body, cancellationToken);
            if (answer.Status == HttpStatusCode.OK)
            {
                var result = TryRead<DeviceAuthorizationStartResult>(answer.Body);
                if (result != null && Uri.TryCreate(result.AuthorizeUrl, UriKind.Absolute, out var authorizeUrl)
                    && (authorizeUrl.Scheme == Uri.UriSchemeHttps || authorizeUrl.Scheme == Uri.UriSchemeHttp))
                {
                    _logger.LogInformation("Device sign-in for {DeviceId} on node {NodeId} started", deviceId, nodeId);
                    return new(StatusCodes.Status200OK, null, new DeviceAuthorizationStartResult { AuthorizeUrl = authorizeUrl.AbsoluteUri });
                }
                answer = answer with { Status = HttpStatusCode.BadGateway, Message = "The node answered without a valid sign-in URL." };
            }

            _states.Discard(state);
            var failure = Failure(answer);
            _logger.LogWarning("Device sign-in for {DeviceId} on node {NodeId} could not start: {Status}", deviceId, nodeId, failure.StatusCode);
            return failure;
        }

        /// <summary>
        /// The provider's redirect: takes the state (once, even when the provider reports an error) and, with a code,
        /// has the node exchange it with the stored redirect URI.
        /// </summary>
        public async Task<DeviceAuthorizationCallbackOutcome> CompleteAsync(string? state, string? code, string? error, string? errorDescription,
            CancellationToken cancellationToken)
        {
            var pending = state == null || state.Length > MaxStateLength ? null : _states.TryConsume(state);
            if (pending == null)
            {
                _logger.LogWarning("Device sign-in callback refused: unknown or expired state");
                return new(StatusCodes.Status400BadRequest, null, false,
                    "This sign-in link is unknown or has expired. Start the sign-in again from the device's page.");
            }

            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("Device sign-in for {DeviceId} on node {NodeId} was refused by the provider", pending.DeviceId, pending.NodeId);
                var reason = string.IsNullOrWhiteSpace(errorDescription) ? error : $"{error}: {errorDescription}";
                return new(StatusCodes.Status400BadRequest, pending, false, "The sign-in was not completed: " + Truncate(reason));
            }

            if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength)
            {
                _logger.LogWarning("Device sign-in for {DeviceId} on node {NodeId} returned without a code", pending.DeviceId, pending.NodeId);
                return new(StatusCodes.Status400BadRequest, pending, false, "The provider returned without an authorization code.");
            }

            var baseUrl = NodeBaseUrl(pending.NodeId);
            if (baseUrl == null)
            {
                _logger.LogWarning("Device sign-in for {DeviceId} on node {NodeId} failed: the node is offline", pending.DeviceId, pending.NodeId);
                return new(StatusCodes.Status404NotFound, pending, false, "The node is not online.");
            }

            var body = new DeviceAuthorizationCompletion { Code = code, RedirectUri = pending.RedirectUri };
            var answer = await PostAsync(baseUrl, Constants.ApiDeviceAuthorizationCompleteUrl, pending.DeviceId, body, cancellationToken);
            if ((int)answer.Status is >= 200 and < 300)
            {
                _logger.LogInformation("Device sign-in for {DeviceId} on node {NodeId} completed", pending.DeviceId, pending.NodeId);
                return new(StatusCodes.Status200OK, pending, true, "The device is connected to its account. You can close this window.");
            }

            var failure = Failure(answer);
            _logger.LogWarning("Device sign-in for {DeviceId} on node {NodeId} could not complete: {Status}", pending.DeviceId, pending.NodeId, failure.StatusCode);
            return new(failure.StatusCode, pending, false, failure.Message ?? "The sign-in could not be completed.");
        }

        // window.location.origin: "scheme://authority", tolerating one trailing slash; nothing else after the authority.
        private static bool IsBareAuthority(string origin, Uri uri)
        {
            if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return false;
            var prefix = uri.Scheme + "://";
            if (!origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            var authority = origin[prefix.Length..];
            if (authority.EndsWith('/'))
                authority = authority[..^1];
            return authority.Length > 0 && authority.IndexOfAny(['/', '\\', '?', '#', '@', ' ']) < 0;
        }

        private static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxIdLength;

        private string? NodeBaseUrl(string nodeId)
        {
            var node = _nodes.OnlineNodes.FirstOrDefault(n => n.Id == nodeId);
            var url = node?.OnlineNodeSettings?.IsOnline == true ? node.OnlineNodeSettings.NodeBaseUrl : null;
            return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
        }

        private sealed record NodeAnswer(HttpStatusCode Status, string? Body, string? Message);

        private async Task<NodeAnswer> PostAsync(string baseUrl, string route, string deviceId, object body, CancellationToken cancellationToken)
        {
            var url = baseUrl + route.Replace("{id}", Uri.EscapeDataString(deviceId), StringComparison.Ordinal);
            try
            {
                using var content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await _clients.CreateClient(OnlineNodeService.HttpClientName).PostAsync(url, content, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                return new NodeAnswer(response.StatusCode, text, TryRead<NodeMessage>(text)?.Message);
            }
            catch (Exception x) when (!cancellationToken.IsCancellationRequested)
            {
                // Without the exception: its message can carry the URL, never the body, but keep the log plain.
                _logger.LogWarning("Device sign-in call to node failed for {DeviceId}: {Error}", deviceId, x.GetType().Name);
                return new NodeAnswer(HttpStatusCode.BadGateway, null, "The node did not answer.");
            }
        }

        private static DeviceAuthorizationOutcome Failure(NodeAnswer answer)
        {
            var message = string.IsNullOrWhiteSpace(answer.Message) ? null : Truncate(answer.Message);
            return answer.Status switch
            {
                HttpStatusCode.NotFound => new(StatusCodes.Status404NotFound,
                    message ?? "The node has no device with this id, or doesn't support device sign-in."),
                HttpStatusCode.Conflict => new(StatusCodes.Status409Conflict, message ?? "The device is not ready to sign in."),
                HttpStatusCode.BadRequest => new(StatusCodes.Status400BadRequest, message ?? "The node refused the sign-in request."),
                _ => new(StatusCodes.Status502BadGateway, message ?? $"The node failed ({(int)answer.Status})."),
            };
        }

        private static T? TryRead<T>(string? json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                return Json.Deserialize<T>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Truncate(string text) => text.Length <= MaxMessageLength ? text : text[..MaxMessageLength] + "…";

        private sealed class NodeMessage
        {
            public string? Message { get; set; }
        }
    }
}
