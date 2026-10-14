namespace RIoT2.Net.Orchestrator.Services.Outbox
{
    /// <summary>The body of <c>GET /api/v2/outbox/summary</c>.</summary>
    public sealed class OutboxSummary
    {
        /// <summary>Rows per status; every status is present. Final rows are kept for 24 hours.</summary>
        public IReadOnlyDictionary<string, int> Counts { get; init; }

        /// <summary>The oldest pending message, or <c>null</c> when nothing is pending.</summary>
        public OutboxEntry OldestPending { get; init; }

        /// <summary>The ten most recently updated messages that recorded an error, newest first.</summary>
        public IReadOnlyList<OutboxEntry> LastErrors { get; init; }
    }

    /// <summary>One outbox message as the summary shows it; the payload is left out.</summary>
    public sealed class OutboxEntry
    {
        public string Id { get; init; }
        public string Kind { get; init; }
        public string Target { get; init; }
        public string Subject { get; init; }
        public string Status { get; init; }
        public int Attempts { get; init; }
        public DateTimeOffset Created { get; init; }
        public DateTimeOffset Expires { get; init; }
        public DateTimeOffset Updated { get; init; }
        public double AgeSeconds { get; init; }
        public string LastError { get; init; }

        internal static OutboxEntry From(OutboxMessage message, long now) => new()
        {
            Id = message.Id,
            Kind = message.Kind,
            Target = message.Target,
            Subject = message.Subject,
            Status = message.Status,
            Attempts = message.Attempts,
            Created = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedUtc),
            Expires = DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresUtc),
            Updated = DateTimeOffset.FromUnixTimeMilliseconds(message.UpdatedUtc),
            AgeSeconds = Math.Max(0, now - message.CreatedUtc) / 1000.0,
            LastError = message.LastError
        };
    }
}
