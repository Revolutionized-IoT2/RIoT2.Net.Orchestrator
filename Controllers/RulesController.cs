using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RulesController : ControllerBase
    {
        IStoredObjectService _ruleManagementService;
        IFunctionService _functionService;
        IRuleProcessorService _ruleProcessor;

        public RulesController(IStoredObjectService ruleManagementService, IFunctionService functionService, IRuleProcessorService ruleProcessorService)
        {
            _ruleManagementService = ruleManagementService;
            _functionService = functionService;
            _ruleProcessor = ruleProcessorService;
        }

        [HttpGet]
        public IActionResult GetRules()
        {
            return new OkObjectResult(_ruleManagementService.GetAll<Rule>().Select(x => new
            {
                x.Id,
                x.Name,
                x.Description,
                x.IsActive,
                x.Tags
            }));
        }

        [HttpGet("tags")]
        public IActionResult GetRuleTags()
        {
            return new OkObjectResult(_ruleManagementService.GetAll<Rule>().SelectMany(x => x.Tags).Distinct());
        }

        [HttpPost("save")]
        public IActionResult SaveRule([FromBody] Models.RuleDTO rule)
        {
            //Have to use DTO, because system.text.json cannot handle interfaces 
            var id = _ruleManagementService.Save(rule.ToRule(), true, true);

            if (!string.IsNullOrEmpty(id))
                return new OkObjectResult(id);
            else
                return Problem();
        }

        [HttpGet("{id}/state/{toState}")]
        public IActionResult SetRuleState(string id, string toState)
        {
            var rule = _ruleManagementService.GetAll<Rule>().FirstOrDefault(x => x.Id == id);
            if (rule == default)
                return BadRequest($"Rule with id {id} not found.");

            rule.IsActive = toState.ToLower() == "true";
            _ruleManagementService.Save(rule, true, true);

            return new OkObjectResult(id);
        }

        [HttpGet("{id}")]
        public IActionResult GetRule(string id)
        {
            var rule = _ruleManagementService.GetAll<Rule>().FirstOrDefault(x => x.Id == id);
            if (rule != default)
                return Content(Json.Serialize(rule), "application/json"); //serialized using newtonsoft; system.text.json cannot handle interfaces
            else
                return BadRequest($"Rule with id {id} not found.");
        }

        [HttpGet("{id}/delete")]
        public IActionResult DeleteRule(string id)
        {
            var rule = _ruleManagementService.GetAll<Rule>().FirstOrDefault(x => x.Id == id);
            if (rule != default) 
            {
                _ruleManagementService.Delete<Rule>(id);
                return new OkResult();
            }
            else
                return BadRequest($"Rule with id {id} not found.");
        }

        [HttpPost("function/run")]
        public IActionResult RunFunction([FromBody] Function function)
        {
            var f = _functionService.GetFunctions().FirstOrDefault(x => x.Id == function.FunctionId);
            if (f == null)
                return BadRequest($"Function with id {function.FunctionId} not found.");

            var result = f.Run(function.DataAsModel, function.Parameters);

            return Content(result.ToJson() , "application/json"); //Push Json directly to content
        }

        [HttpPost("simulate")]
        public IActionResult SimulateRule([FromBody] SimulationDTO data)
        {
            return new OkObjectResult(_ruleProcessor.RunRuleSimulation(data.Id, data.GetAsModel()));
        }

        [HttpGet("{id}/validate")]
        public IActionResult ValidateRule(string id)
        {
            var rule = _ruleManagementService.GetAll<Rule>().FirstOrDefault(x => x.Id==id);
            if(rule == default)
                return new NotFoundResult();

            return new OkObjectResult(rule.Validate());
        }
    }
}
