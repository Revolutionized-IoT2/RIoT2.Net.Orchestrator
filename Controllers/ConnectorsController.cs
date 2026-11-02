using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>The online connectors (design 7.3 phase 3), which the UI lists apart from the nodes.</summary>
    [ApiController]
    [Route("api/v2/connectors")]
    [Tags("Connectors")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class ConnectorsController : ControllerBase
    {
        private readonly IOnlineNodeService _nodes;

        public ConnectorsController(IOnlineNodeService nodes) => _nodes = nodes;

        /// <summary>
        /// Every online connector, with its <c>/health</c> answer when it announced a URL. Each connector is asked
        /// at the same time, for at most 8 s (longer than the 5 s a connector gives its target).
        /// </summary>
        [HttpGet]
        [ProducesResponseType<List<ConnectorInfo>>(StatusCodes.Status200OK)]
        public async Task<ActionResult<List<ConnectorInfo>>> GetAsync(CancellationToken cancellationToken) =>
            await _nodes.GetConnectorsAsync(cancellationToken);
    }
}
