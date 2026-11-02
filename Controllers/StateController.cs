using Microsoft.AspNetCore.Mvc;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services;
using RIoT2.Net.Orchestrator.Services.Templates;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// The latest report and command states the orchestrator holds.
    /// </summary>
    [Route("api/Nodes")]
    [Tags("Nodes")]
    [ApiController]
    public class StateController : ControllerBase
    {
        private readonly IMessageStateService _messageStateService;
        private readonly IOnlineNodeService _onlineNodeService;
        private readonly ITemplateCatalog _templates;

        public StateController(IMessageStateService messageStateService, IOnlineNodeService onlineNodeService, ITemplateCatalog templates)
        {
            _messageStateService = messageStateService;
            _onlineNodeService = onlineNodeService;
            _templates = templates;
        }

        /// <summary>
        /// The latest report with the id. Without one, the template with the same address that the
        /// owning node currently offers, since a node's templates carry their default values.
        /// </summary>
        [HttpGet("report/{id}/state")]
        public async Task<IActionResult> GetReportState(string id)
        {
            var report = _messageStateService.Reports.FirstOrDefault(x => x.Id == id);
            if (report != null)
                return Content(Json.Serialize(report), "application/json");

            var template = await LoadOnlineTemplateAsync(id);
            if (template != null)
                return Content(Json.Serialize(template), "application/json");

            return NotFound($"Could not find report state with ID: {id}");
        }

        [HttpGet("command/{id}/state")]
        public IActionResult GetCommandState(string id)
        {
            var command = _messageStateService.Commands.FirstOrDefault(x => x.Id == id);
            if (command == null)
                return NotFound($"Could not find command state with ID: {id}");

            return Content(Json.Serialize(command), "application/json");
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

        // The ids in a node's own templates are random, so the stored template's address finds the match.
        private async Task<ReportTemplate?> LoadOnlineTemplateAsync(string id)
        {
            var configured = _templates.FindReportTemplate(id);
            if (configured == null)
                return null;

            var node = _onlineNodeService.OnlineNodes.FirstOrDefault(x => x.Id == configured.NodeId);
            if (node?.OnlineNodeSettings?.NodeType != NodeType.Device)
                return null;

            var devices = await _onlineNodeService.LoadDeviceConfigurationTemplateAsync(configured.NodeId);
            return devices?
                .Where(d => d.ReportTemplates != null)
                .SelectMany(d => d.ReportTemplates)
                .FirstOrDefault(t => t.Address == configured.Address);
        }
    }
}
