using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services.Commands
{
    /// <summary>
    /// A command delivery as <c>POST /api/v2/commands</c> and <c>GET /api/v2/commands/{correlationId}</c>
    /// return it (design 7.1).
    /// </summary>
    public sealed class CommandDelivery
    {
        /// <summary>The id of this delivery; the node echoes it in its result.</summary>
        public string CorrelationId { get; init; }

        /// <summary>The command (template) id.</summary>
        public string CommandId { get; init; }

        /// <summary>The node the command goes to.</summary>
        public string NodeId { get; init; }

        /// <summary>One of <see cref="CommandDeliveryStatus"/>.</summary>
        public string Status { get; init; }

        /// <summary>True when the status won't change any more.</summary>
        public bool IsFinal => Status != CommandDeliveryStatus.Pending;

        /// <summary>Why the command failed or was retried, if it was.</summary>
        public string Error { get; init; }

        /// <summary>Delivery attempts that got no final answer.</summary>
        public int Attempts { get; init; }

        public DateTimeOffset Created { get; init; }
        public DateTimeOffset Expires { get; init; }
        public DateTimeOffset Updated { get; init; }

        internal static CommandDelivery From(OutboxMessage message) => new()
        {
            CorrelationId = message.Id,
            CommandId = message.Subject,
            NodeId = message.Target,
            Status = StatusOf(message),
            Error = message.LastError,
            Attempts = message.Attempts,
            Created = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedUtc),
            Expires = DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresUtc),
            Updated = DateTimeOffset.FromUnixTimeMilliseconds(message.UpdatedUtc)
        };

        private static string StatusOf(OutboxMessage message) => message.Status switch
        {
            OutboxStatus.Pending => CommandDeliveryStatus.Pending,
            OutboxStatus.Expired => CommandDeliveryStatus.Expired,
            OutboxStatus.Failed => message.Result ?? CommandDeliveryStatus.Failed,
            _ => message.Result ?? CommandDeliveryStatus.Executed
        };
    }

    /// <summary>The values of <see cref="CommandDelivery.Status"/>.</summary>
    public static class CommandDeliveryStatus
    {
        /// <summary>Waiting for the node, for a retry or for the node's answer.</summary>
        public const string Pending = "pending";

        /// <summary>Published to a node that doesn't report results (no <c>command-result/1</c>); final.</summary>
        public const string Sent = "sent";

        /// <summary>The device ran the command.</summary>
        public const string Executed = "executed";

        /// <summary>The device threw.</summary>
        public const string Failed = "failed";

        /// <summary>No device on the node owns the command id.</summary>
        public const string UnknownCommand = "unknownCommand";

        /// <summary>The node had executed this delivery before (its first answer was lost) and didn't run it again.</summary>
        public const string Duplicate = "duplicate";

        /// <summary>The time-to-live passed before the node answered.</summary>
        public const string Expired = "expired";
    }
}
