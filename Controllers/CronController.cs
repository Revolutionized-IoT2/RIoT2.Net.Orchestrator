using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Validates the Quartz cron expressions of device refresh schedules.
    /// </summary>
    [Route("api/Nodes")]
    [Tags("Nodes")]
    [ApiController]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class CronController : ControllerBase
    {
        /// <summary>
        /// Returns the expression with its validity and, when valid, a summary with HTML line breaks.
        /// </summary>
        [HttpPost("validatecron")]
        public IActionResult ValidateCron([FromBody] CronValidationResult cron)
        {
            bool isValid = CronExpression.IsValidExpression(cron.Expression);
            if (isValid)
                cron.Summary = new CronExpression(cron.Expression).GetExpressionSummary();

            if (!string.IsNullOrEmpty(cron.Summary))
                cron.Summary = cron.Summary.Replace("\n", "<br />");

            cron.IsValid = isValid;
            return Ok(cron);
        }
    }
}
