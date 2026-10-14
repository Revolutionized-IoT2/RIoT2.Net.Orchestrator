namespace RIoT2.Net.Orchestrator.Services.Outbox
{
    /// <summary>One row of the outbox (design 7.1). Times are Unix milliseconds, UTC.</summary>
    public sealed record OutboxMessage
    {
        /// <summary>Message id (a GUID); also the idempotency key the receiver deduplicates on.</summary>
        public string Id { get; init; }

        /// <summary><see cref="OutboxKind"/>.</summary>
        public string Kind { get; init; }

        /// <summary>Where the message goes: a node id, or <see cref="OutboxKind.Workflow"/>. Delivery is FIFO per target.</summary>
        public string Target { get; init; }

        /// <summary>What the message is about, for logs and the summary: the report id or command id.</summary>
        public string Subject { get; init; }

        /// <summary>The serialized message, as the kind's transport needs it.</summary>
        public string Payload { get; init; }

        public long CreatedUtc { get; init; }
        public long ExpiresUtc { get; init; }
        public long NextAttemptUtc { get; init; }
        public long UpdatedUtc { get; init; }
        public int Attempts { get; init; }

        /// <summary><see cref="OutboxStatus"/>.</summary>
        public string Status { get; init; } = OutboxStatus.Pending;

        public string Result { get; init; }
        public string LastError { get; init; }
    }

    public static class OutboxKind
    {
        public const string Workflow = "workflow";
        public const string Command = "command";
    }

    public static class OutboxStatus
    {
        public const string Pending = "pending";
        public const string Sent = "sent";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Expired = "expired";

        public static readonly IReadOnlyList<string> All = [Pending, Sent, Done, Failed, Expired];
    }
}
