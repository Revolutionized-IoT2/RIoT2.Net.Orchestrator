using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services.Outbox;

namespace RIoT2.Net.Orchestrator.Services.Commands
{
    /// <summary>How the command outbox reaches the broker; implemented by <see cref="OrchestratorMqttService"/>.</summary>
    public interface ICommandTransport
    {
        bool IsConnected { get; }

        /// <summary>Publishes a serialized <see cref="Command"/> on the node's command topic.</summary>
        Task PublishCommandAsync(string nodeId, string payload);
    }

    /// <summary>Commands through the durable outbox (design 7.1).</summary>
    public interface ICommandOutbox
    {
        /// <summary>
        /// Stores a command for <paramref name="nodeId"/> with a new correlation id and returns its
        /// delivery. It is sent when the node is online, until it gets a final answer or its
        /// time-to-live (<paramref name="ttl"/>, by default <see cref="OrchestratorOptions.CommandTtl"/>) passes.
        /// </summary>
        CommandDelivery Enqueue(Command command, string nodeId, TimeSpan? ttl = null);

        /// <summary>The delivery, or <c>null</c> when the id is unknown or was cleaned up (24 hours after it became final).</summary>
        CommandDelivery Get(string correlationId);

        /// <summary>Waits until the delivery is final and returns it.</summary>
        Task<CommandDelivery> WaitAsync(string correlationId, CancellationToken cancellationToken);

        /// <summary>A node answered on <c>riot2/node/{id}/command/result</c>.</summary>
        void ResultReceived(string nodeId, CommandResult result);

        /// <summary>A node announced itself or went offline.</summary>
        void NodeOnlineChanged(string nodeId, NodeOnlineMessage message);

        /// <summary>Sets the broker connection; until then, commands wait.</summary>
        void UseTransport(ICommandTransport transport);

        /// <summary>The broker connection came (back) up: waiting commands are sent now.</summary>
        void TransportConnected();
    }

    /// <summary>
    /// Delivers commands from the outbox, one dispatcher per node, so a node's commands keep their order.
    /// A node that advertises <c>command-result/1</c> must answer each delivery within
    /// five seconds (<see cref="DefaultResultTimeout"/>), or it is sent again with the same correlation id, which the node
    /// deduplicates. A node that doesn't is sent the command once, which is final (<c>sent</c>), as
    /// before. While the node is offline, its commands wait until their time-to-live passes.
    /// </summary>
    internal sealed class CommandOutbox : BackgroundService, ICommandOutbox
    {
        public static readonly TimeSpan DefaultResultTimeout = TimeSpan.FromSeconds(5);

        /// <summary>How long one delivery waits for the node's result before it is sent again.</summary>
        internal TimeSpan ResultTimeout { get; set; } = DefaultResultTimeout;
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan WaitGrace = TimeSpan.FromSeconds(5);

        private readonly OutboxStore _store;
        private readonly IOnlineNodeService _nodes;
        private readonly IMessageStateService _state;
        private readonly ILogger<CommandOutbox> _logger;
        private readonly TimeProvider _time;
        private readonly TimeSpan _ttl;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, OutboxDispatcher> _dispatchers = new(StringComparer.Ordinal);
        private readonly List<Task> _runs = [];
        private readonly ConcurrentDictionary<string, (string NodeId, TaskCompletionSource<CommandResult> Result)> _waiting = new(StringComparer.Ordinal);
        private CancellationToken _stopping;
        private bool _running;
        private volatile ICommandTransport _transport;

        public CommandOutbox(OutboxStore store, IOnlineNodeService nodes, IMessageStateService state, IOptions<OrchestratorOptions> options,
            ILogger<CommandOutbox> logger, TimeProvider time)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _ttl = options.Value.CommandTtl;
        }

        public CommandDelivery Enqueue(Command command, string nodeId, TimeSpan? ttl = null)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentException.ThrowIfNullOrWhiteSpace(command.Id, nameof(command));
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
            var lifetime = ttl ?? _ttl;
            if (lifetime <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ttl), "The time-to-live must be positive.");

            var now = Now;
            // The correlation id is the row id, so every redelivery carries the same one.
            var id = Guid.NewGuid().ToString("N");
            var message = new OutboxMessage
            {
                Id = id,
                Kind = OutboxKind.Command,
                Target = nodeId,
                Subject = command.Id,
                Payload = Json.SerializeIgnoreNulls(new Command { Id = command.Id, Value = command.Value, CorrelationId = id }),
                CreatedUtc = now,
                ExpiresUtc = now + (long)lifetime.TotalMilliseconds,
                NextAttemptUtc = now,
                UpdatedUtc = now
            };
            _store.Add(message);
            Dispatcher(nodeId).Signal();
            return CommandDelivery.From(message);
        }

        public CommandDelivery Get(string correlationId)
        {
            var message = string.IsNullOrWhiteSpace(correlationId) ? null : _store.Get(correlationId);
            return message?.Kind == OutboxKind.Command ? CommandDelivery.From(message) : null;
        }

        public async Task<CommandDelivery> WaitAsync(string correlationId, CancellationToken cancellationToken)
        {
            while (true)
            {
                var delivery = Get(correlationId);
                // The dispatcher expires a delivery at its time-to-live; the grace period covers its next step.
                if (delivery == null || delivery.IsFinal || _time.GetUtcNow() > delivery.Expires + WaitGrace)
                    return delivery;
                await Task.Delay(PollInterval, _time, cancellationToken);
            }
        }

        public void ResultReceived(string nodeId, CommandResult result)
        {
            if (string.IsNullOrEmpty(result?.CorrelationId))
            {
                _logger.LogWarning("Ignored a command result from {NodeId} without a correlation id", nodeId);
                return;
            }
            if (_waiting.TryGetValue(result.CorrelationId, out var waiter) && waiter.NodeId == nodeId)
            {
                waiter.Result.TrySetResult(result);
                return;
            }

            // The attempt stopped waiting (or the orchestrator restarted): record a final answer anyway.
            var (status, outcome, error) = Interpret(result);
            if (outcome == DeliveryOutcome.Retry)
                return;
            var message = _store.Get(result.CorrelationId);
            if (message?.Kind != OutboxKind.Command)
                return;
            if (_store.CompleteLate(message.Id, nodeId, outcome == DeliveryOutcome.Delivered ? OutboxStatus.Done : OutboxStatus.Failed, Now, status, error))
            {
                if (status is CommandDeliveryStatus.Executed or CommandDeliveryStatus.Duplicate)
                    _state.SetState(Command.Create(message.Payload));
                _logger.LogInformation("Command {CommandId} ({CorrelationId}) for {NodeId}: the result {Status} arrived after its attempt stopped waiting",
                    message.Subject, message.Id, nodeId, status);
            }
        }

        public void NodeOnlineChanged(string nodeId, NodeOnlineMessage message)
        {
            if (message?.IsOnline != true)
                return;
            OutboxDispatcher dispatcher;
            lock (_gate)
                _dispatchers.TryGetValue(nodeId, out dispatcher);
            dispatcher?.TargetCameOnline();
        }

        public void UseTransport(ICommandTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            TransportConnected();
        }

        public void TransportConnected()
        {
            OutboxDispatcher[] dispatchers;
            lock (_gate)
                dispatchers = [.. _dispatchers.Values];
            foreach (var dispatcher in dispatchers)
                dispatcher.Signal();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            lock (_gate)
            {
                _stopping = stoppingToken;
                _running = true;
                foreach (var dispatcher in _dispatchers.Values)
                    _runs.Add(dispatcher.RunAsync(stoppingToken));
            }
            // Commands stored before a restart are delivered when their node comes back, within their time-to-live.
            foreach (var target in _store.PendingTargets(OutboxKind.Command))
                Dispatcher(target).Signal();

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            Task[] runs;
            lock (_gate)
            {
                _running = false;
                runs = [.. _runs];
            }
            await Task.WhenAll(runs);
        }

        private OutboxDispatcher Dispatcher(string nodeId)
        {
            lock (_gate)
            {
                if (_dispatchers.TryGetValue(nodeId, out var dispatcher))
                    return dispatcher;
                dispatcher = new OutboxDispatcher(_store, nodeId, "command", () => IsAvailable(nodeId),
                    (message, token) => DeliverAsync(nodeId, message, token), _logger, _time);
                _dispatchers.Add(nodeId, dispatcher);
                if (_running)
                    _runs.Add(dispatcher.RunAsync(_stopping));
                return dispatcher;
            }
        }

        private bool IsAvailable(string nodeId) => _transport?.IsConnected == true && FindNode(nodeId) != null;

        private OnlineNode FindNode(string nodeId) =>
            _nodes.OnlineNodes.FirstOrDefault(x => x.Id == nodeId && x.OnlineNodeSettings?.IsOnline == true);

        private async Task<DeliveryResult> DeliverAsync(string nodeId, OutboxMessage message, CancellationToken cancellationToken)
        {
            var transport = _transport;
            var node = FindNode(nodeId);
            if (transport == null || node == null)
                return DeliveryResult.Retry("The node is offline.");
            var now = _time.GetUtcNow();
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresUtc);
            if (expires <= now)
                return DeliveryResult.Retry("The time-to-live passed before the command was sent.");
            var command = Command.Create(message.Payload);

            if (!node.OnlineNodeSettings.HasCapability(Contract.Capabilities.CommandResult))
            {
                // A node without results: sending is all that can be known, as before.
                await transport.PublishCommandAsync(nodeId, message.Payload);
                _state.SetState(command);
                return DeliveryResult.Delivered(CommandDeliveryStatus.Sent);
            }

            var waiter = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[message.Id] = (nodeId, waiter);
            try
            {
                await transport.PublishCommandAsync(nodeId, message.Payload);
                var timeout = expires - now < ResultTimeout ? expires - now : ResultTimeout;
                CommandResult result;
                try
                {
                    result = await waiter.Task.WaitAsync(timeout, _time, cancellationToken);
                }
                catch (TimeoutException)
                {
                    return DeliveryResult.Retry($"No result from the node within {timeout.TotalSeconds:0.#} s.");
                }
                var (status, outcome, error) = Interpret(result);
                switch (outcome)
                {
                    case DeliveryOutcome.Delivered:
                        // "duplicate": the node executed this delivery earlier, and its answer was lost.
                        _state.SetState(command);
                        return DeliveryResult.Delivered(status);
                    case DeliveryOutcome.Failed:
                        return DeliveryResult.Failed(error, status);
                    default:
                        return DeliveryResult.Retry(error);
                }
            }
            finally
            {
                _waiting.TryRemove(message.Id, out _);
            }
        }

        // What a node's answer means for the delivery: the delivery status, and whether it is final.
        private static (string Status, DeliveryOutcome Outcome, string Error) Interpret(CommandResult result) => result.Status switch
        {
            CommandResultStatus.Executed => (CommandDeliveryStatus.Executed, DeliveryOutcome.Delivered, null),
            CommandResultStatus.Duplicate => (CommandDeliveryStatus.Duplicate, DeliveryOutcome.Delivered, null),
            CommandResultStatus.Failed => (CommandDeliveryStatus.Failed, DeliveryOutcome.Failed, result.Error ?? "The device failed."),
            CommandResultStatus.UnknownCommand => (CommandDeliveryStatus.UnknownCommand, DeliveryOutcome.Failed, result.Error ?? "No device on the node handles the command."),
            // The node didn't run it (for example its queue was full): send it again.
            CommandResultStatus.Rejected => (CommandDeliveryStatus.Pending, DeliveryOutcome.Retry, "Rejected by the node: " + (result.Error ?? "no reason given")),
            _ => (result.Status ?? CommandDeliveryStatus.Failed, DeliveryOutcome.Failed, $"Unknown result status '{result.Status}'.")
        };

        private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
