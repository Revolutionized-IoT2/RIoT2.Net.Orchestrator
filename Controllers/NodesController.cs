using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Node configurations and online status. Templates, state, variables, plugin checks and cron
    /// validation have their own controllers under the same <c>api/Nodes</c> prefix.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class NodesController : ControllerBase
    {
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IOnlineNodeService _onlineNodeService;
        private readonly IMessageStateService _messageStateService;

        public NodesController(IOrchestratorConfigurationService configuration, IOnlineNodeService onlineNodeService, IMessageStateService messageStateService)
        {
            _configuration = configuration;
            _onlineNodeService = onlineNodeService;
            _messageStateService = messageStateService;
        }

        [HttpGet]
        public async Task<IActionResult> GetNodesAsync()
        {
            var nodes = await _onlineNodeService.GetNodesAsync();
            return new OkObjectResult(nodes);
        }

        [HttpGet("{id}/devices/status")]
        public async Task<IActionResult> GetNodeDevicesStatusAsync(string id)
        {
            var node = _onlineNodeService.OnlineNodes.FirstOrDefault(x => x.Id == id);
            if(node == null)
                return new OkResult();

            var statuses = await _onlineNodeService.LoadDeviceStatusFromNodeAsync(id);
            return new OkObjectResult(statuses);
        }

        [HttpGet("online")]
        public IActionResult GetOnlineNodes()
        {
            var allNodes = _configuration.NodeConfigurations;

            var nodes = _onlineNodeService.OnlineNodes.Select(x => new
            {
                Name = String.IsNullOrEmpty(x.OnlineNodeSettings.Name) ? allNodes?.FirstOrDefault(a => a.Id == x.Id)?.Name : x.OnlineNodeSettings.Name,
                HasDevices = allNodes?.FirstOrDefault(a => a.Id == x.Id)?.DeviceConfigurations?.Count > 0,
                x.Id,
                x.OnlineNodeSettings.IsOnline,
                x.OnlineNodeSettings.NodeType,
                x.OnlineNodeSettings.Manifest,
                x.OnlineNodeSettings.PluginManifest,
                x.OnlineNodeSettings.NodeBaseUrl
            }).ToList();

            return new OkObjectResult(nodes);
        }

        [HttpGet("{id}/configuration")]
        public IActionResult GetConfiguration(string id)
        {
            bool includesState = false;
            if (HttpContext.Request.Query.ContainsKey("state"))
                includesState = HttpContext.Request.Query["state"].ToString().ToLower() == "true";

            var configuration = _configuration.NodeConfigurations.FirstOrDefault(x => x.Id == id);
            if (configuration == null)
                return NotFound($"Could not find node configuration with ID: {id}");

            // The states go into a copy: the stored configuration is what nodes fetch and what its hash covers.
            if (includesState)
                return Content(Json.Serialize(WithCurrentStates(configuration)), "application/json");

            // Design 7.2: the ETag is the configuration hash, and a node that runs it gets 304.
            if (!String.IsNullOrEmpty(configuration.Hash))
            {
                var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{configuration.Hash}\"");
                Response.Headers.ETag = etag.ToString();
                if (Microsoft.Net.Http.Headers.EntityTagHeaderValue.TryParseList(Request.Headers.IfNoneMatch, out var requested)
                    && requested.Any(tag => tag.Compare(etag, useStrongComparison: false) || tag.Equals(Microsoft.Net.Http.Headers.EntityTagHeaderValue.Any)))
                    return StatusCode(StatusCodes.Status304NotModified);
            }
            return Content(Json.Serialize(configuration), "application/json");
        }

        [HttpGet("{id}/delete")]
        [HttpDelete("/api/v2/nodes/{id}")]
        [DeprecatedGetAlias("DELETE /api/v2/nodes/{id}")]
        public IActionResult DeleteNode(string id)
        {
            _configuration.DeleteNodeConfiguration(id);
            return new OkResult();
        }

        [HttpPost("configuration")]
        public IActionResult SaveConfiguration([FromBody] System.Text.Json.JsonElement json)
        {
            try
            {
                var id = _configuration.SaveNodeConfiguration(json.ToString());
                if (String.IsNullOrEmpty(id))
                    return new BadRequestResult();

                return new OkObjectResult(id);
            }
            catch (Exception x) 
            {
                return StatusCode(500, x.Message);
            }
        }

        private NodeDeviceConfiguration WithCurrentStates(NodeDeviceConfiguration configuration)
        {
            var copy = Json.Deserialize<NodeDeviceConfiguration>(Json.Serialize(configuration))!;
            foreach (var device in copy.DeviceConfigurations ?? [])
            {
                foreach (var commandTemplate in device.CommandTemplates ?? [])
                {
                    var latestCommand = _messageStateService.Commands.FirstOrDefault(x => x.Id == commandTemplate.Id);
                    if (latestCommand != null)
                        commandTemplate.Model = latestCommand.Value;
                }
            }
            return copy;
        }
    }
}
