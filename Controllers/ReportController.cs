using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Orchestrator.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ReportController : ControllerBase
    {
        //TODO MOVE all report API to this ctrl

        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IMessageStateService _messageStateService;

        public ReportController(IOrchestratorConfigurationService configuration, IMessageStateService messageStateService)
        {
            _configuration = configuration;
            _messageStateService = messageStateService;
        }

        /// <summary>
        /// Returns the current or default value of a report as a JSON-formatted response.
        /// </summary>
        /// <param name="id">The identifier of the report,variable ,or command.</param>
        /// <returns>A JSON-formatted string containing the current value of the specified report variable or command.</returns>
        [HttpGet("{id}/value")]
        public IActionResult GetCurrentOrDefaultReportValue(string id)
        {
            var report = _messageStateService.Reports.FirstOrDefault(x => x.Id == id);
            if (report == default) //fetch from templates if not found in current states
            {
                var t = _configuration.GetReportTemplates().FirstOrDefault(x => x.Id == id);
                if (t != default)
                    return Content(Json.Serialize(t.GetAsReport()), "application/json");
                else
                    return NotFound($"Could not find report with ID: {id}");
            }
            return Content(Json.Serialize(report), "application/json");
        }

        /// <summary>
        /// Retrieves all available report templates
        /// </summary>
        /// <returns>Return report templates with node and device data.</returns>
        [HttpGet("templates")]
        public IActionResult GetReportTemplates()
        {
            var a = new List<NodeReportTemplate>();
            foreach (var node in _configuration.NodeConfigurations)
            {
                foreach (var device in node.DeviceConfigurations)
                {
                    foreach (var t in device.ReportTemplates)
                    {
                        a.Add(new NodeReportTemplate
                        {
                            NodeId = node.Id,
                            Node = node.Name,
                            DeviceId = device.Id,
                            Device = device.Name,
                            Filters = t.Filters,
                            Name = t.Name,
                            Id = t.Id,
                            Type = t.Type,
                            Address = t.Address,
                            Model = t.Model,
                            RefreshSchedule = t.RefreshSchedule,
                            MaintainHistory = t.MaintainHistory
                        });
                    }
                }
            }
            return new OkObjectResult(a);
        }
    }
}
