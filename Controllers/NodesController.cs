using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Orchestrator.Services;
using System.Text;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;
using RIoT2.Core.Models;
using RIoT2.Core;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NodesController : ControllerBase
    {
        private IOrchestratorConfigurationService _configuration;
        private IOnlineNodeService _onlineNodeService;
        private IMessageStateService _messageStateService;
        private IFunctionService _functionService;
        private IStoredObjectService _storedObjectService;
        private IOrchestratorMqttService _mqtt;

        public NodesController(IOrchestratorConfigurationService configuration, IOnlineNodeService onlineNodeService, IMessageStateService messageStateService, IFunctionService functionService, IStoredObjectService storedObjectService, IOrchestratorMqttService orchestratorMqttService) 
        {
            _configuration = configuration;
            _onlineNodeService = onlineNodeService; 
            _messageStateService = messageStateService;
            _functionService = functionService;
            _storedObjectService = storedObjectService;
            _mqtt = orchestratorMqttService;
        }

        [HttpGet]
        public IActionResult GetNodes()
        {
            List<Node> nodes = new List<Node>();
            var onlineNodes = _onlineNodeService.OnlineNodes.Select(x => x.Id).ToList();

            foreach (var conf in _configuration.NodeConfigurations) 
            {
                nodes.Add(new Node() { 
                    Id = conf.Id,
                    Name = conf.Name,
                    IsOnline = onlineNodes.Contains(conf.Id),
                    DeviceStatuses = _onlineNodeService.LoadDeviceStatusFromNodeAsync(conf.Id).Result
                });
            }

            //TODO load all node statuses at the same time

            return new OkObjectResult(nodes);
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
                x.OnlineNodeSettings.IsOnline
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
            if (includesState)
                appendCurrentStates(ref configuration);

            return Content(Json.Serialize(configuration), "application/json");
           //return new OkObjectResult(configuration);
        }

        [HttpGet("{id}/delete")]
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
                //TODO TESTAA TÄMÄ!!!!
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

        [HttpGet("report/{id}/state")]
        public IActionResult GetReportState(string type, string id)
        {
            var report = _messageStateService.Reports.FirstOrDefault(x => x.Id == id);
            if (report == null) //fetch from 
            {
                var templates = _onlineNodeService.LoadDeviceConfigurationTemplatesAsync().Result;
                foreach (var node in templates.Keys) 
                {
                    foreach (var device in templates[node]) 
                    {
                        var reportTemplate = device.ReportTemplates.FirstOrDefault(x => x.Id == id);
                        if(reportTemplate != null)
                            return Content(Json.Serialize(reportTemplate), "application/json");
                            //return new OkObjectResult(reportTemplate);
                    }
                } 
            }

            return Content(Json.Serialize(report), "application/json");
            //return new OkObjectResult(report);
        }

        [HttpGet("command/{id}/state")]
        public IActionResult GetCommandState(string type, string id)
        {
            return Content(Json.Serialize(_messageStateService.Commands.FirstOrDefault(x => x.Id == id)), "application/json");
            //return new OkObjectResult(_messageStateService.Commands.FirstOrDefault(x => x.Id == id));
        }

        [HttpPost("report/state")]
        public IActionResult SetNodeReportStates([FromBody] IEnumerable<Report> reports)
        {
            try
            {
                foreach (var report in reports)
                    _messageStateService.SetState(report);

                return NoContent();
            }
            catch (Exception x)
            {
                return StatusCode(500, x.Message);
            }
        }

        [HttpGet("report/templates")]
        public IActionResult GetReportTemplatates()
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

        [HttpGet("variable/templates")]
        public IActionResult GetVariableTemplatates()
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
                    v.Model
                });
            }
            return new OkObjectResult(a);
        }

        [HttpGet("command/templates")]
        public IActionResult GetCommandTemplatates()
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

        [HttpGet("function/templates")]
        public IActionResult GetFunctionTemplatates()
        {
            var a = new List<object>();
            foreach (var f in _functionService.GetFunctions())
            {
                a.Add(new
                {
                    f.Name,
                    f.Description,
                    f.ExpectedParameters,
                    f.Id
                });
            }
            return new OkObjectResult(a);
        }

        [HttpGet("device/{id}/template")]
        public async Task<IActionResult> GetOnlineDeviceConfigurationTemplatates(string id)
        {
            var template = await _onlineNodeService.LoadDeviceConfigurationTemplateAsync(id);
            if(template != null && template.Count > 0)
                return new OkObjectResult(template);

            return new OkResult();
        }

        [HttpGet("variables")]
        public IActionResult GetVariables()
        {
            return new OkObjectResult(_storedObjectService.GetAll<Variable>().ToList());
        }

        [HttpPost("variable/save")]
        public IActionResult SaveVariable([FromBody] Variable variable)
        {
            if (!string.IsNullOrEmpty(_storedObjectService.Save(variable)))
                return NoContent();
            else
                return Problem();
        }

        [HttpGet("variable/{id}/delete")]
        public IActionResult DeleteVariable(string id)
        {
            _storedObjectService.Delete<Variable>(id);
            return NoContent();
        }


        /// <summary>
        /// Nodes (or dashboard) can send commands only via orchestrator. Otherwise state is not tracked.
        /// </summary>
        /// <param name="variable"></param>
        /// <returns></returns>
        [HttpPost("command/{type}")]
        public async Task<IActionResult> SendOrchestratorCommandAsync(int type)
        {
            string json;
            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
                json = await reader.ReadToEndAsync();

            if (String.IsNullOrEmpty(json))
                return BadRequest();

            var cmd = Json.Deserialize<Command>(json);
            if (cmd == null)
                return BadRequest();

            //TODO get Operation from template...
            var op = (OutputOperation)type;

            await _mqtt.ProcessOutput(new RuleEvaluationResult() 
            {
                Value = cmd.Value,
                CommandId = cmd.Id,
                Operation = op
            });

            return new OkResult();
        }

        private void appendCurrentStates(ref NodeDeviceConfiguration configuration) 
        {
            foreach(var node in _configuration.NodeConfigurations) 
            {
                foreach (var device in node.DeviceConfigurations) 
                {
                    foreach (var commandTemplate in device.CommandTemplates) 
                    {
                        var latestCommand = _messageStateService.Commands.FirstOrDefault(x => x.Id == commandTemplate.Id);
                        if (latestCommand != null)
                            commandTemplate.Model = latestCommand.Value;
                    }
                }
            }
        }
    }
}
