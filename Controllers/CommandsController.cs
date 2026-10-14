using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Services.Commands;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>
    /// Commands with a delivery status (design 7.1). <c>POST /api/command/execute</c> stays as it was:
    /// it queues the command the same way and returns 200.
    /// </summary>
    [ApiController]
    [Route("api/v2/commands")]
    public class CommandsController : ControllerBase
    {
        private const int MaxTtlSeconds = 86400;

        private readonly IOrchestratorConfigurationService _configuration;
        private readonly ICommandOutbox _commands;

        public CommandsController(IOrchestratorConfigurationService configuration, ICommandOutbox commands)
        {
            _configuration = configuration;
            _commands = commands;
        }

        /// <summary>
        /// Queues a command: <c>{ "id", "value", "ttlSeconds"?, "wait"? }</c>. Returns 202 with the delivery,
        /// or with <c>"wait": true</c> 200 once the delivery is final (at the latest when its time-to-live passes).
        /// </summary>
        [HttpPost]
        [ProducesResponseType<CommandDelivery>(StatusCodes.Status200OK)]
        [ProducesResponseType<CommandDelivery>(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> PostAsync([FromBody] JsonElement body, CancellationToken cancellationToken)
        {
            if (body.ValueKind != JsonValueKind.Object)
                return BadRequest("The body must be a JSON object with an id and a value.");
            var request = Json.Deserialize<CommandRequest>(body.GetRawText());
            if (string.IsNullOrWhiteSpace(request?.Id) || request.Value == null)
                return BadRequest("A command needs an id and a value.");
            if (request.TtlSeconds is < 1 or > MaxTtlSeconds)
                return BadRequest($"ttlSeconds must be a whole number from 1 to {MaxTtlSeconds}.");

            var nodeId = _configuration.FindNodeId(request.Id);
            if (string.IsNullOrEmpty(nodeId))
                return NotFound($"Could not find a node for command with ID: {request.Id}");

            var ttl = request.TtlSeconds == null ? (TimeSpan?)null : TimeSpan.FromSeconds(request.TtlSeconds.Value);
            var delivery = _commands.Enqueue(new Command { Id = request.Id, Value = request.Value }, nodeId, ttl);
            var location = Url.Action(nameof(Get), new { correlationId = delivery.CorrelationId });
            if (!request.Wait)
                return Accepted(location, delivery);
            return Ok(await _commands.WaitAsync(delivery.CorrelationId, cancellationToken) ?? delivery);
        }

        /// <summary>The delivery status. Final deliveries are kept for 24 hours.</summary>
        [HttpGet("{correlationId}")]
        [ProducesResponseType<CommandDelivery>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Get(string correlationId)
        {
            var delivery = _commands.Get(correlationId);
            return delivery == null ? NotFound($"No command delivery {correlationId}") : Ok(delivery);
        }

        /// <summary>The body of <c>POST /api/v2/commands</c>.</summary>
        public sealed class CommandRequest
        {
            public string Id { get; set; }
            public ValueModel Value { get; set; }

            /// <summary>Seconds to keep trying, 1–86400; default <c>RIOT2_OUTBOX_COMMAND_TTL</c>.</summary>
            public int? TtlSeconds { get; set; }

            /// <summary>Wait for the final status before answering. Default <c>false</c>.</summary>
            public bool Wait { get; set; }
        }
    }
}
