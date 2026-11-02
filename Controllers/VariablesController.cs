using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services.Persistence;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Variable listing, values, saving and deletion. The variable templates are listed by
    /// <see cref="TemplatesController"/>; the routes keep the paths of the controllers they used to
    /// live in.
    /// </summary>
    [ApiController]
    public class VariablesController : ControllerBase
    {
        private readonly IStoredObjectStore _storedObjectService;

        public VariablesController(IStoredObjectStore storedObjectService)
        {
            _storedObjectService = storedObjectService;
        }

        [HttpGet("api/Nodes/variables")]
        [Tags("Nodes")]
        public IActionResult GetVariables() =>
            Ok(_storedObjectService.GetAll<Variable>().Select(VariableDTO.ToVariableDTO).ToList());

        [HttpGet("api/Variable/{id}/value")]
        [Tags("Variable")]
        public IActionResult GetVariableValue(string id)
        {
            var variable = _storedObjectService.Get<Variable>(id);
            if (variable != null)
                return Ok(VariableDTO.ToVariableDTO(variable));

            return NotFound();
        }

        /// <summary>
        /// Saves a variable. A number value arrives as text or a JSON number and is stored as an
        /// integer, or as a double when it has a decimal point.
        /// </summary>
        [HttpPost("api/Nodes/variable/save")]
        [Tags("Nodes")]
        public IActionResult SaveVariable([FromBody] VariableDTO variable)
        {
            if (variable.Type == Core.ValueType.Number)
            {
                // A number variable arrives with a value; a missing one throws as it always has.
                if (variable.Value!.ToString()!.Contains('.'))
                {
                    if (double.TryParse(variable.Value.ToString(), out double d))
                        variable.Value = d;
                    else
                        return BadRequest();
                }
                else
                {
                    if (int.TryParse(variable.Value.ToString(), out int i))
                        variable.Value = i;
                    else
                        return BadRequest();
                }
            }

            if (!string.IsNullOrEmpty(_storedObjectService.Save(variable.ToVariable())))
                return NoContent();
            else
                return Problem();
        }

        [HttpGet("api/Nodes/variable/{id}/delete")]
        [HttpDelete("/api/v2/variables/{id}")]
        [DeprecatedGetAlias("DELETE /api/v2/variables/{id}")]
        [Tags("Nodes")]
        public IActionResult DeleteVariable(string id)
        {
            _storedObjectService.Delete<Variable>(id);
            return NoContent();
        }
    }
}
