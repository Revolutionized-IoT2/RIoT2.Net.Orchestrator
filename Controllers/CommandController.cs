using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Models;
using RIoT2.Core.Security;
using RIoT2.Core.Utils;
using System.Text;
using RIoT2.Net.Orchestrator.Services;

namespace RIoT2.Net.Orchestrator.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class CommandController : ControllerBase
    {
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IMessageStateService _messageStateService;
        private readonly IOrchestratorMqttService _mqtt;

        public CommandController(IOrchestratorConfigurationService configuration, IMessageStateService messageStateService, IOrchestratorMqttService mqtt   )
        {
            _configuration = configuration;
            _messageStateService = messageStateService;
            _mqtt = mqtt;
        }

        /// <summary>
        /// Returns the current or default value of a command as a JSON-formatted response.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("{id}/value")]
        public IActionResult GetCurrentOrDefaultValueOfCommand(string id)
        {
            var cmd = _messageStateService.Commands.FirstOrDefault(x => x.Id == id);
            if (cmd == default)
            {
                var t = _configuration.GetCommandTemplates().FirstOrDefault(x => x.Id == id);
                if (t != default)
                    return Content(Json.Serialize(t.GetAsCommand()), "application/json");
                else
                    return NotFound($"Could not find command with ID: {id}");
            }
            return Content(Json.Serialize(cmd), "application/json");
        }

        /// <summary>
        /// Executes a posted command or returns BadRequest if the command is invalid or ID is missing or not found.
        /// </summary>
        /// <returns>OK if the command was executed successfully, otherwise BadRequest.</returns>
        [HttpPost("execute")]
        [Authorize(Policy = SecurityPolicies.Operator)]
        public async Task<IActionResult> ExecuteCommand()
        {
            string json;
            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
                json = await reader.ReadToEndAsync();

            if (String.IsNullOrEmpty(json))
                return BadRequest();

            var cmd = Json.Deserialize<Command>(json);
            if (cmd == null)
                return BadRequest();

            if (!await _mqtt.ExecuteCommand(cmd))
                return BadRequest($"Could not find a node for command with ID: {cmd.Id}");

            return new OkResult();
        }
    }
}
