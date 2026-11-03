using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Elsa.Workflows.Llm;
using RIoT2.Net.Orchestrator.Services.Llm;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// The Run LLM step's side of the orchestrator (ADR 0016): the language model connections (whose API keys never leave
    /// it), a connection test, the usage records, and <c>POST /api/llm/run</c>, which the remote workflow host calls for
    /// every Run LLM step. Reads need Viewer, connection changes Admin, running a step Operator.
    /// </summary>
    [ApiController]
    [Route("api/llm")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class LlmController(LlmConnectionService connections, ILlmRunner runner, LlmUsageStore usage, TimeProvider time) : ControllerBase
    {
        [HttpGet("connections")]
        [ProducesResponseType<IReadOnlyList<LlmConnectionInfo>>(StatusCodes.Status200OK)]
        public IActionResult Connections() => Ok(connections.List());

        [HttpPost("connections")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<LlmConnectionInfo>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult Create([FromBody] LlmConnectionSave request) => Saved(connections.Save(null, request));

        /// <summary>Changes a connection; <c>apiKey</c> null keeps the stored key, empty removes it.</summary>
        [HttpPut("connections/{id}")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<LlmConnectionInfo>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Update(string id, [FromBody] LlmConnectionSave request) =>
            connections.Get(id) == null ? NotFound() : Saved(connections.Save(id, request));

        [HttpDelete("connections/{id}")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Delete(string id) => connections.Delete(id) ? NoContent() : NotFound();

        /// <summary>Asks the connection's model for a one-word answer, to check its endpoint, model and key.</summary>
        [HttpPost("connections/{id}/test")]
        [Authorize(Policy = SecurityPolicies.Admin)]
        [ProducesResponseType<LlmRunResult>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Test(string id, CancellationToken cancellationToken)
        {
            var connection = connections.Get(id);
            if (connection == null)
                return NotFound();
            return Ok(await runner.RunAsync(new LlmRunRequest
            {
                Connection = connection.Name,
                Instructions = "This is a connection test. Answer with the single word OK.",
                TimeoutSeconds = 30
            }, cancellationToken));
        }

        /// <summary>
        /// Runs a Run LLM step: 200 with the result, or 422 with the failed result (its <c>errorCode</c> says why).
        /// </summary>
        [HttpPost("run")]
        [Authorize(Policy = SecurityPolicies.Operator)]
        [ProducesResponseType<LlmRunResult>(StatusCodes.Status200OK)]
        [ProducesResponseType<LlmRunResult>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<IActionResult> Run([FromBody] LlmRunRequest request, CancellationToken cancellationToken)
        {
            var result = await runner.RunAsync(request, cancellationToken);
            return result.Succeeded ? Ok(result) : UnprocessableEntity(result);
        }

        /// <summary>The usage of the last <c>days</c> (1-90, default 30), optionally of one connection.</summary>
        [HttpGet("usage")]
        [ProducesResponseType<LlmUsageSummary>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult Usage([FromQuery] int days = 30, [FromQuery] string? connection = null)
        {
            if (days is < 1 or > 90)
                return BadRequest("days must be 1-90.");
            return Ok(usage.Summarize(time.GetUtcNow().AddDays(-days), connection));
        }

        private IActionResult Saved((LlmConnectionInfo? Connection, IReadOnlyList<string> Errors) result) =>
            result.Connection != null ? Ok(result.Connection) : BadRequest(new { errors = result.Errors });
    }
}
