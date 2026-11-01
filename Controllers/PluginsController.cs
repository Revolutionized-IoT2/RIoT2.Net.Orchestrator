using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Models;
using System.Net.Http.Headers;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Checks a node plugin URL before a node configuration refers to it.
    /// </summary>
    [Route("api/Nodes")]
    [Tags("Nodes")]
    [ApiController]
    public class PluginsController : ControllerBase
    {
        /// <summary>
        /// Fills in the plugin's file name from the URL's response headers, or its error message when
        /// the URL is missing, invalid or unreachable.
        /// </summary>
        [HttpPost("checkplugin")]
        public async Task<IActionResult> CheckPlugin([FromBody] PluginFile plugin)
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

                HttpContentHeaders metadata;
                try
                {
                    metadata = await Web.GetUrlMetadata(plugin.Url);
                }
                catch (Exception x)
                {
                    plugin.ErrorMessage = x.Message;
                    return Ok(plugin);
                }

                if (metadata == null)
                {
                    plugin.ErrorMessage = "Plugin Url is not valid or accessible";
                    return Ok(plugin);
                }

                plugin.Name = metadata.ContentDisposition?.FileName ?? "";
                return Ok(plugin);
            }
            catch (Exception x)
            {
                return StatusCode(500, x.Message);
            }
        }
    }
}
