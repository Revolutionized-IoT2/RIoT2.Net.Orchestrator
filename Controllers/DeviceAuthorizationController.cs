using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Models;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services.DeviceSignIn;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Device account sign-in (Core 2.4.0): the UI's "Connect" button starts it, and the provider redirects the
    /// popup back to the anonymous callback, whose <c>state</c> is the only credential.
    /// </summary>
    [ApiController]
    [Authorize(Policy = SecurityPolicies.Admin)]
    public class DeviceAuthorizationController : ControllerBase
    {
        /// <summary>The callback's route; the same as <c>Constants.ApiOAuthCallbackPath</c>, which isn't a constant.</summary>
        public const string CallbackRoute = "/api/oauth/callback";

        private readonly DeviceAuthorizationService _authorization;

        public DeviceAuthorizationController(DeviceAuthorizationService authorization) => _authorization = authorization;

        /// <summary>
        /// Starts a device's sign-in and returns the provider's sign-in page for a popup. 400 for an invalid
        /// <c>redirectUri</c> or <c>origin</c>, 404 for an offline node or a device it doesn't have, 409 when the
        /// device's configuration isn't ready (the node's message), 429 when too many sign-ins wait, 502 when the
        /// node failed.
        /// </summary>
        [HttpPost("api/Nodes/{nodeId}/devices/{deviceId}/authorization")]
        [Tags("Nodes")]
        [ProducesResponseType<DeviceAuthorizationStartResult>(StatusCodes.Status200OK)]
        public async Task<IActionResult> StartAsync(string nodeId, string deviceId, [FromBody] DeviceAuthorizationRequest request)
        {
            var outcome = await _authorization.StartAsync(nodeId, deviceId, request.RedirectUri, request.Origin,
                User?.Identity?.Name, HttpContext.RequestAborted);
            return outcome.Succeeded
                ? Ok(outcome.Result)
                : StatusCode(outcome.StatusCode, new { message = outcome.Message });
        }

        /// <summary>
        /// Where the provider redirects the sign-in popup: an HTML page that shows the result and posts it to the
        /// UI window. Anonymous; the single-use state identifies the sign-in.
        /// </summary>
        [HttpGet(CallbackRoute)]
        [AllowAnonymous]
        [Tags("OAuth")]
        [Produces("text/html")]
        public async Task<IActionResult> CallbackAsync([FromQuery] string? code, [FromQuery] string? state,
            [FromQuery] string? error, [FromQuery(Name = "error_description")] string? errorDescription)
        {
            var outcome = await _authorization.CompleteAsync(state, code, error, errorDescription, HttpContext.RequestAborted);
            var (html, nonce) = DeviceAuthorizationPage.Render(outcome);
            DeviceAuthorizationPage.ApplyHeaders(Response.Headers, nonce);
            return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = outcome.StatusCode };
        }
    }
}
