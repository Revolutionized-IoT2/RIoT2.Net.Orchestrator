using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Models;
using RIoT2.Core.Interfaces.Services;
using System.Data;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Orchestrator.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DashboardController : ControllerBase
    {
        IOrchestratorConfigurationService _configuration;
        IMessageStateService _messageStateService;

        public DashboardController(IOrchestratorConfigurationService orchestratorConfigurationService, IMessageStateService messageStateService)
        {
            _configuration = orchestratorConfigurationService;
            _messageStateService = messageStateService;
        }

        [HttpGet("configuration")]
        public IActionResult GetDashboard()
        {
            bool includeHistory = false;
            if(HttpContext.Request.Query.ContainsKey("history"))
                includeHistory = HttpContext.Request.Query["history"].ToString().ToLower() == "true";

            var config = _configuration.DashboardConfiguration;

            if (includeHistory)
                updateHistory(ref config);

            return Content(Json.Serialize(config), "application/json");
            //return new OkObjectResult(config);
        }

        [HttpPost("configuration")]
        public IActionResult SaveDashboard([FromBody] System.Text.Json.JsonElement json)
        {
            var id = _configuration.SaveDashboardConfiguration(json.ToString());
            if(!string.IsNullOrEmpty(id))
                return new OkObjectResult(id);

            return new StatusCodeResult(500);
        }

        [HttpGet("reports")]
        public IActionResult GetCurrentStates()
        {
            return Content(Json.Serialize(_messageStateService.Reports), "application/json");
            //return new OkObjectResult(_messageStateService.Reports);
        }

        [HttpGet("report/{id}/history")]
        public IActionResult GetReportHistory(string id)
        {
            return Content(Json.Serialize(getReportHistory(id)), "application/json");
            //return new OkObjectResult(getReportHistory(id));
        }

        [HttpGet("reports/history/reset")]
        public IActionResult ResetReportHistory()
        {
            _messageStateService.Reset();
            return new OkResult();
        }

        private void updateHistory(ref DashboardConfiguration dashboard) 
        {
            foreach (var page in dashboard.Pages)
            {
                foreach (var comp in page.Components)
                {
                    foreach (var elem in comp.Elements)
                    {
                        if (elem.ReportTemplate == null)
                            continue;

                        elem.PreviousReports = getReportHistory(elem.ReportTemplate.Id, elem.NumberOfPreviousReports);
                    }
                }
            }
        }

        private List<Report> getReportHistory(string id, int? count = null) 
        {
            if (count != null && count < 1)
                return new List<Report>();

            var history = _messageStateService.GetHistory(id, count);
            if (history == null || history.Count() == 0) //if no history is maintained return only current value
                history = _messageStateService.Reports.Where(x => x.Id == id);

            return history.ToList();
                
        }
    }
}
