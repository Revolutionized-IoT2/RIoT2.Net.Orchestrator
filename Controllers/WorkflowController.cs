using Microsoft.AspNetCore.Mvc;
using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services;
using System.Text;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// This controller handles endpoints required by external workflow systems to interact with the orchestrator.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class WorkflowController : ControllerBase
    {

        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IOnlineNodeService _onlineNodeService;
        private readonly IMessageStateService _messageStateService;
        private readonly IStoredObjectService _storedObjectService;
        private readonly IOrchestratorMqttService _mqtt;

        public WorkflowController(IOrchestratorConfigurationService configuration, IOnlineNodeService onlineNodeService, IMessageStateService messageStateService, IStoredObjectService storedObjectService, IOrchestratorMqttService orchestratorMqttService)
        {
            _configuration = configuration;
            _onlineNodeService = onlineNodeService;
            _messageStateService = messageStateService;
            _storedObjectService = storedObjectService;
            _mqtt = orchestratorMqttService;
        }

        /// <summary>
        /// Returns the current or default value of a report as a JSON-formatted response.
        /// </summary>
        /// <param name="id">The identifier of the report,variable ,or command.</param>
        /// <returns>A JSON-formatted string containing the current value of the specified report variable or command.</returns>
        [HttpGet("report/{id}/value")]
        public IActionResult GetCurrentOrDefaultReportValue(string id)
        {
            var report = _messageStateService.Reports.FirstOrDefault(x => x.Id == id);
            if (report == null) //fetch from templates if not found in current states
            {
                var templates = _onlineNodeService.LoadDeviceConfigurationTemplatesAsync().Result;
                foreach (var node in templates.Keys)
                {
                    foreach (var device in templates[node])
                    {
                        var reportTemplate = device.ReportTemplates.FirstOrDefault(x => x.Id == id);
                        if (reportTemplate != null)
                            return Content(Json.Serialize(reportTemplate), "application/json");
                        //return new OkObjectResult(reportTemplate);
                    }
                }
            }

            return Content(Json.Serialize(report), "application/json");
        }

        /// <summary>
        /// Returns the current value of a command as a JSON-formatted response.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("command/{id}/value")]
        public IActionResult GetCurrentValueOfCommand(string id)
        {
            return Content(Json.Serialize(_messageStateService.Commands.FirstOrDefault(x => x.Id == id)), "application/json");
        }

        /// <summary>
        /// Retrieves all available report templates
        /// </summary>
        /// <param name="id">The identifier used to fetch report templates.</param>
        /// <returns>An IActionResult containing the report templates.</returns>
        [HttpGet("report/templates")]
        public IActionResult GetReportTemplates(string id)
        {
            var a = new List<object>();
            foreach (var node in _configuration.NodeConfigurations)
            {
                foreach (var device in node.DeviceConfigurations)
                {
                    foreach (var t in device.ReportTemplates)
                    {
                        a.Add(new
                        {
                            NodeId = node.Id,
                            Node = node.Name,
                            DeviceId = device.Id,
                            Device = device.Name,
                            FilterOptions = t.Filters,
                            t.Name,
                            t.Id,
                            t.Type,
                            t.Address,
                            t.RefreshSchedule,
                            t.MaintainHistory
                        });
                    }
                }
            }
            return new OkObjectResult(a);
        }

        /// <summary>
        /// Retrieves all available variable templates
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("variable/templates")]
        public IActionResult GetVariableTemplates(string id)
        {
            var a = new List<object>();
            foreach (var v in _storedObjectService.GetAll<Variable>())
            {
                a.Add(new
                {
                    NodeId = _configuration.OrchestratorConfiguration.Id,
                    Node = "Orchestrator",
                    DeviceId = "",
                    Device = "Variable",
                    //FilterOptions = v.fi .Filters,
                    v.Name,
                    v.Id,
                    v.Model.Type,
                    Model = v.Value
                });
            }
            return new OkObjectResult(a);
        }

        /// <summary>
        /// Retrieves all available command templates
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("command/templates")]
        public IActionResult GetCommandTemplates(string id)
        {
            var a = new List<object>();
            foreach (var node in _configuration.NodeConfigurations)
            {
                foreach (var device in node.DeviceConfigurations)
                {
                    foreach (var t in device.CommandTemplates)
                    {
                        a.Add(new
                        {
                            NodeId = node.Id,
                            Node = node.Name,
                            DeviceId = device.Id,
                            Device = device.Name,
                            t.Name,
                            t.Id,
                            t.Type,
                            t.Address,
                            t.Model
                        });
                    }
                }
            }
            return new OkObjectResult(a);
        }

        /// <summary>
        /// Executes a command identified by the given ID.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpPost("command/execute")]
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
