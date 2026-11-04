using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services.Plans;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Command plans (ADR 0019): schedules of a command's future values that the orchestrator sends at their times through the
    /// command outbox. A new plan for a command replaces its active plan. Reads need Viewer, changes Operator, as commands do.
    /// </summary>
    [ApiController]
    [Route("api/v2/command-plans")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class CommandPlansController(ICommandPlanService plans) : ControllerBase
    {
        /// <summary>The plans, newest first: <c>?commandId=</c> for one command's, <c>?active=true</c> for the active ones, <c>limit</c> 1-500 (100).</summary>
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<CommandPlan>>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult List([FromQuery] string? commandId, [FromQuery] bool active = false, [FromQuery] int limit = 100)
        {
            if (limit is < 1 or > 500)
                return BadRequest("limit must be 1-500.");
            if (commandId?.Length > 200)
                return BadRequest("The commandId is at most 200 characters.");
            return Ok(plans.List(commandId, active, limit));
        }

        [HttpGet("{id}")]
        [ProducesResponseType<CommandPlan>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Get(string id)
        {
            var plan = plans.Get(id);
            return plan == null ? NotFound($"No command plan {id}") : Ok(plan);
        }

        /// <summary>
        /// Starts a plan: <c>{ "commandId", "steps": [{ "at", "value" }], "name"?, "source"?, "catchUp"?: "latest" | "none" }</c>.
        /// 1-200 steps, at least a minute apart, at most 24 hours in the past and 7 days ahead. Answers 201 with the plan.
        /// </summary>
        [HttpPost]
        [ProducesResponseType<CommandPlan>(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [Authorize(Policy = SecurityPolicies.Operator)]
        public IActionResult Post([FromBody] JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object)
                return BadRequest("The body must be a JSON object with a commandId and steps.");
            CommandPlanRequest? request;
            try
            {
                request = body.Deserialize<CommandPlanRequest>(CommandPlanStore.Json);
            }
            catch (JsonException x)
            {
                return BadRequest($"The plan can't be read: {x.Message}");
            }
            var result = plans.Submit(request ?? new CommandPlanRequest(), "REST API");
            if (!result.Succeeded)
                return result.UnknownCommand ? NotFound(result.Error) : BadRequest(result.Error);
            return CreatedAtAction(nameof(Get), new { id = result.Plan!.Id }, result.Plan);
        }

        /// <summary>Ends a plan: its pending steps aren't sent. Answers with the plan.</summary>
        [HttpDelete("{id}")]
        [ProducesResponseType<CommandPlan>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [Authorize(Policy = SecurityPolicies.Operator)]
        public IActionResult Cancel(string id)
        {
            var plan = plans.Cancel(id);
            return plan == null ? NotFound($"No command plan {id}") : Ok(plan);
        }

        /// <summary>Ends a command's active plan: <c>?commandId=</c>. Answers with the plan, or 204 when the command has none.</summary>
        [HttpDelete]
        [ProducesResponseType<CommandPlan>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [Authorize(Policy = SecurityPolicies.Operator)]
        public IActionResult CancelActive([FromQuery] string? commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 200)
                return BadRequest("A commandId (1-200 characters) is required.");
            var plan = plans.CancelActive(commandId.Trim());
            return plan == null ? NoContent() : Ok(plan);
        }
    }
}
