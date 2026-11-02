using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services;
using RIoT2.Net.Orchestrator.Services.Templates;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Template listings: the report, command and variable templates of the stored configuration,
    /// and the device templates an online node offers. The routes keep the paths of the controllers
    /// they used to live in.
    /// </summary>
    [ApiController]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class TemplatesController : ControllerBase
    {
        private readonly ITemplateCatalog _templates;
        private readonly IOnlineNodeService _onlineNodeService;

        public TemplatesController(ITemplateCatalog templates, IOnlineNodeService onlineNodeService)
        {
            _templates = templates;
            _onlineNodeService = onlineNodeService;
        }

        /// <summary>
        /// Retrieves all available report templates
        /// </summary>
        /// <returns>Return report templates with node and device data.</returns>
        [HttpGet("api/Report/templates")]
        [Tags("Report")]
        public IActionResult GetReportTemplates() => Ok(_templates.GetReportTemplates());

        /// <summary>
        /// Retrieves all available command templates
        /// </summary>
        /// <returns>Command templates with Node and device data</returns>
        [HttpGet("api/Command/templates")]
        [Tags("Command")]
        public IActionResult GetCommandTemplates() => Ok(_templates.GetCommandTemplates());

        /// <summary>
        /// Retrieves all available variable templates
        /// </summary>
        [HttpGet("api/Variable/templates")]
        [Tags("Variable")]
        public IActionResult GetVariableTemplates() => Ok(_templates.GetVariableTemplates());

        [HttpGet("api/Nodes/report/templates")]
        [Tags("Nodes")]
        public IActionResult GetNodeReportTemplates() => Ok(_templates.GetReportTemplates());

        [HttpGet("api/Nodes/variable/templates")]
        [Tags("Nodes")]
        public IActionResult GetNodeVariableTemplates() => Ok(_templates.GetVariableTemplates());

        [HttpGet("api/Nodes/command/templates")]
        [Tags("Nodes")]
        public IActionResult GetNodeCommandTemplates() => Ok(_templates.GetCommandTemplates());

        /// <summary>
        /// The device configuration templates that an online node offers, or an empty 200 when the
        /// node is offline or offers none.
        /// </summary>
        [HttpGet("api/Nodes/{id}/device/templates")]
        [Tags("Nodes")]
        public async Task<IActionResult> GetOnlineDeviceConfigurationTemplates(string id)
        {
            var templates = await _onlineNodeService.LoadDeviceConfigurationTemplateAsync(id);
            if (templates != null && templates.Count > 0)
                return Ok(templates);

            return Ok();
        }
    }
}
