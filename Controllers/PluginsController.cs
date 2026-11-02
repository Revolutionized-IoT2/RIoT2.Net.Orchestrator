using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Checks a node plugin URL before a node configuration refers to it.
    /// </summary>
    [Route("api/Nodes")]
    [Tags("Nodes")]
    [ApiController]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class PluginsController(IHttpClientFactory httpClientFactory) : ControllerBase
    {
        /// <summary>The <c>IHttpClientFactory</c> client that checks plugin URLs; it passes the outbound URL policy (design 7.5).</summary>
        public const string HttpClientName = "riot2-plugins";

        /// <summary>
        /// Fills in the plugin's file name from the URL's response headers, or its error message when
        /// the URL is missing, invalid or unreachable.
        /// </summary>
        [HttpPost("checkplugin")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        public async Task<IActionResult> CheckPlugin([FromBody] PluginFile plugin, CancellationToken cancellationToken = default)
        {
            try
            {
                if (plugin == null)
                    return new BadRequestObjectResult("Plugin object cannot be null");

                if (String.IsNullOrEmpty(plugin.Url))
                {
                    plugin.ErrorMessage = "Plugin Url must be provided";
                    return Ok(plugin);
                }

                if (!plugin.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !plugin.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    plugin.ErrorMessage = "Not a valid url";
                    return Ok(plugin);
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, plugin.Url);
                    using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    plugin.Name = response.Content.Headers.ContentDisposition?.FileName ?? "";
                }
                catch (Exception x) when (!cancellationToken.IsCancellationRequested)
                {
                    plugin.ErrorMessage = x.Message;
                }
                return Ok(plugin);
            }
            catch (Exception x)
            {
                return StatusCode(500, x.Message);
            }
        }
    }
}
