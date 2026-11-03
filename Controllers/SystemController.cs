using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Security;
using RIoT2.Net.Orchestrator.Services.Health;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>The system health page (I46).</summary>
    [ApiController]
    [Route("api/v2/system")]
    [Tags("System")]
    [Authorize(Policy = SecurityPolicies.Viewer)]
    public class SystemController : ControllerBase
    {
        private readonly SystemHealthService _health;

        public SystemController(SystemHealthService health)
        {
            _health = health;
        }

        /// <summary>
        /// The orchestrator's health checks, the outbox, and every node with its presence history since the
        /// orchestrator started, its sync state, and the state and last error of each device. Asks each online
        /// device node for its device states (at most 5 s each).
        /// </summary>
        [HttpGet("health")]
        [ProducesResponseType<SystemHealth>(StatusCodes.Status200OK)]
        public Task<SystemHealth> GetHealthAsync(CancellationToken cancellationToken) => _health.GetAsync(cancellationToken);
    }
}
