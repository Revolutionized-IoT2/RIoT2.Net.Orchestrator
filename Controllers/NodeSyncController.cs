using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Configuration sync per node (design 7.2 phase 6): the state the UI polls, and re-applying a
    /// node's configuration.
    /// </summary>
    [ApiController]
    [Route("api/v2/nodes")]
    [Tags("Nodes")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class NodeSyncController : ControllerBase
    {
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IOnlineNodeService _nodes;
        private readonly IOrchestratorMqttService _mqtt;

        public NodeSyncController(IOrchestratorConfigurationService configuration, IOnlineNodeService nodes, IOrchestratorMqttService mqtt)
        {
            _configuration = configuration;
            _nodes = nodes;
            _mqtt = mqtt;
        }

        /// <summary>Every configured node's sync state. Cheap: the nodes aren't asked anything.</summary>
        [HttpGet("sync")]
        [ProducesResponseType<List<NodeSync>>(StatusCodes.Status200OK)]
        public ActionResult<List<NodeSync>> GetSync() => _nodes.GetSyncStates();

        /// <summary>
        /// Sends the node its configuration command again. A node that runs the configuration with every
        /// device running only reports its status again; otherwise it fetches and applies the configuration,
        /// which also restarts devices that aren't running. 409 when the node is offline: it gets the
        /// configuration when it comes online.
        /// </summary>
        [HttpPost("{id}/configuration/apply")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [Authorize(Policy = SecurityPolicies.Operator)]
        public async Task<IActionResult> ApplyAsync(string id)
        {
            if (!_configuration.NodeConfigurations.Any(x => x.Id == id))
                return NotFound($"Could not find node configuration with ID: {id}");
            if (!_nodes.OnlineNodes.Any(x => x.Id == id && x.OnlineNodeSettings?.IsOnline == true))
                return Conflict($"Node {id} is offline; it gets its configuration when it comes online.");
            await _mqtt.SendConfigurationCommand(id);
            return Accepted();
        }
    }
}
