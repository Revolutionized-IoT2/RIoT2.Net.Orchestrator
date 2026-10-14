using Microsoft.AspNetCore.Mvc;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Controllers
{
    /// <summary>The durable outbox for workflow triggers (design 7.1).</summary>
    [ApiController]
    [Route("api/v2/outbox")]
    public class OutboxController : ControllerBase
    {
        private readonly OutboxStore _store;
        private readonly TimeProvider _time;

        public OutboxController(OutboxStore store, TimeProvider time)
        {
            _store = store;
            _time = time;
        }

        /// <summary>Messages per status, the oldest pending message and the last ten errors.</summary>
        [HttpGet("summary")]
        [ProducesResponseType<OutboxSummary>(StatusCodes.Status200OK)]
        public ActionResult<OutboxSummary> GetSummary() =>
            _store.GetSummary(_time.GetUtcNow().ToUnixTimeMilliseconds());
    }
}
