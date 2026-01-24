using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using System.Text;

namespace RIoT2.Net.Orchestrator.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CommandController : ControllerBase
    {
        //TODO MOVE all command APIs to this ctrl

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
        /// Retrieves all available command templates
        /// </summary>
        /// <returns>Command templates with Node and device data</returns>
        [HttpGet("templates")]
        public IActionResult GetCommandTemplates()
        {
            var a = new List<NodeCommandTemplate>();
            foreach (var node in _configuration.NodeConfigurations)
            {
                foreach (var device in node.DeviceConfigurations)
                {
                    foreach (var t in device.CommandTemplates)
                    {
                        a.Add(new NodeCommandTemplate
                        {
                            NodeId = node.Id,
                            Node = node.Name,
                            DeviceId = device.Id,
                            Device = device.Name,
                            Name =  t.Name,
                            Id =  t.Id,
                            Type =  t.Type,
                            Address =  t.Address,
                            Model =  t.Model
                        });
                    }
                }
            }
            return new OkObjectResult(a);
        }

        /// <summary>
        /// Executes a posted command or returns BadRequest if the command is invalid or ID is missing or not found.
        /// </summary>
        /// <returns>OK if the command was executed successfully, otherwise BadRequest.</returns>
        [HttpPost("execute")]
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

            var op = OutputOperation.Set_value; //TODO get Operation from template... or from command?
            await _mqtt.ProcessOutput(new RuleEvaluationResult()
            {
                Value = cmd.Value,
                CommandId = cmd.Id,
                Operation = op
            });

            return new OkResult();
        }
    }
}
