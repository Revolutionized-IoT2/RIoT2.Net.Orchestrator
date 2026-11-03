using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services;
using RIoT2.Net.Orchestrator.Services.Mcp;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// The UI's AI access page (ADR 0018): the MCP server's deployment settings, the tool policy and the latest tool calls.
    /// Reads need Viewer, changing the policy Admin. No MCP tool reaches these routes.
    /// </summary>
    [ApiController]
    [Route("api/v2/mcp")]
    [Tags("Mcp")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class McpController(McpConfigurationService configuration, McpActivity activity, IOptions<OrchestratorOptions> options) : ControllerBase
    {
        /// <summary>Whether the MCP server and the remote listener run, how clients reach them, and every tool with its group and role.</summary>
        [HttpGet("status")]
        [ProducesResponseType<McpStatus>(StatusCodes.Status200OK)]
        public IActionResult Status()
        {
            var settings = options.Value;
            var mcp = settings.Mcp;
            return Ok(new McpStatus(
                mcp.Enabled,
                mcp.Enabled ? (settings.Url ?? "").TrimEnd('/') + McpServerSetup.Route : null,
                new McpRemoteStatus(mcp.RemoteEnabled, mcp.Auth, mcp.RemoteEnabled ? mcp.RemotePort : null, mcp.PublicUrl, mcp.RemoteMaxRole,
                    mcp.AllowedSubjects.Count > 0),
                McpToolGroups.All.Select(g => new McpGroupInfo(g, McpToolGroups.RoleOf(g))).ToList(),
                McpToolCatalog.Tools.Values.OrderBy(t => McpToolGroups.All.ToList().IndexOf(t.Group)).ThenBy(t => t.Name, StringComparer.Ordinal).ToList()));
        }

        [HttpGet("configuration")]
        [ProducesResponseType<McpConfiguration>(StatusCodes.Status200OK)]
        public IActionResult GetConfiguration() => Ok(configuration.Get());

        /// <summary>Changes the tool policy: the listeners, the tool groups, the allowed commands and the writes per minute.</summary>
        [HttpPut("configuration")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<McpConfiguration>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult SaveConfiguration([FromBody] McpConfigurationSave request)
        {
            var (saved, errors) = configuration.Save(request);
            return saved != null ? Ok(saved) : BadRequest(new { errors });
        }

        /// <summary>The latest MCP tool calls since the orchestrator started, newest first: <c>max</c> 1-200, default 100.</summary>
        [HttpGet("activity")]
        [ProducesResponseType<IReadOnlyList<McpActivityEntry>>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult Activity([FromQuery] int max = 100)
        {
            if (max is < 1 or > McpActivity.Capacity)
                return BadRequest($"max must be 1-{McpActivity.Capacity}.");
            return Ok(activity.Recent(max));
        }
    }

    /// <summary>The MCP server as the AI access page shows it.</summary>
    public sealed record McpStatus(bool Enabled, string? LocalUrl, McpRemoteStatus Remote, IReadOnlyList<McpGroupInfo> Groups, IReadOnlyList<McpToolInfo> Tools);

    /// <summary>The remote listener: its auth profile, port and public URL, the highest role it grants, and whether only listed subjects may use it.</summary>
    public sealed record McpRemoteStatus(bool Enabled, string Auth, int? Port, string? PublicUrl, string MaxRole, bool SubjectsRestricted);

    public sealed record McpGroupInfo(string Id, string Role);
}
