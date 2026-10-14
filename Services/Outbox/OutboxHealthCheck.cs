using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RIoT2.Net.Orchestrator.Services.Outbox
{
    /// <summary>
    /// <c>Degraded</c> when more than 500 outbox messages are pending, or when a pending message is older
    /// than half its time-to-live (design 7.1). Details: <c>GET /api/v2/outbox/summary</c>.
    /// </summary>
    internal sealed class OutboxHealthCheck : IHealthCheck
    {
        public const int PendingLimit = 500;

        private readonly OutboxStore _store;
        private readonly TimeProvider _time;

        public OutboxHealthCheck(OutboxStore store, TimeProvider time)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var health = _store.GetHealth(_time.GetUtcNow().ToUnixTimeMilliseconds());
            var data = new Dictionary<string, object> { ["pending"] = health.Pending, ["stale"] = health.Stale };
            if (health.Pending > PendingLimit)
                return Task.FromResult(HealthCheckResult.Degraded($"{health.Pending} outbox messages are pending (limit {PendingLimit}).", data: data));
            if (health.Stale > 0)
                return Task.FromResult(HealthCheckResult.Degraded($"{health.Stale} pending outbox messages are older than half their time-to-live.", data: data));
            return Task.FromResult(HealthCheckResult.Healthy($"{health.Pending} outbox messages are pending.", data));
        }
    }
}
