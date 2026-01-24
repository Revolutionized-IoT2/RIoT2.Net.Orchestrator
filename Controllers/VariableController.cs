using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VariableController : ControllerBase
    {
        private readonly IStoredObjectService _storedObjectService;
        private readonly IOrchestratorConfigurationService _configuration;

        public VariableController(IStoredObjectService storedObjectService, IOrchestratorConfigurationService configuration)
        {
            _storedObjectService = storedObjectService;
            _configuration = configuration;
        }

        /// <summary>
        /// Retrieves all available variable templates
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("templates")]
        public IActionResult GetVariableTemplates(string id)
        {
            var a = new List<VariableTemplate>();
            foreach (var v in _storedObjectService.GetAll<Variable>())
            {
                a.Add(new VariableTemplate
                {
                    NodeId = _configuration.OrchestratorConfiguration.Id,
                    Node = "Orchestrator",
                    DeviceId = "",
                    Device = "Variable",
                    Name =  v.Name,
                    Id = v.Id,
                    Type = v.Model.Type,
                    Address = "",
                    Model = v.Value
                });
            }
            return new OkObjectResult(a);
        }
    }
}