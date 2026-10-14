using System.Threading.Channels;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Services.Automation;
using RIoT2.Net.Orchestrator.Services.Commands;
using RIoT2.Net.Orchestrator.Services.Matter;

namespace RIoT2.Net.Orchestrator.Services
{
    internal class OrchestratorMqttService : IOrchestratorMqttService, ICommandTransport, IDisposable, IAsyncDisposable
    {
        private MqttClient _client;
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IMessageStateService _deviceStateService;
        private readonly IStoredObjectService _storedObjectService;
        private readonly IOnlineNodeService _onlineNodeService;
        private readonly IAutomationProvider _automation;
        private readonly ICommandOutbox _commands;
        private readonly IMatterReportSink _matterSink;
        private readonly ILogger _logger;

        private string _reportTopic;
        private string _nodeOnlineTopic;
        private string _commandResultTopic;
        private string _nodeStatusTopic;

        // Bounded queue decouples MQTT callback threads / event handlers from processing.
        private readonly Channel<Func<Task>> _workQueue;
        private readonly CancellationTokenSource _cts;
        private readonly Task _consumerTask;
        private readonly object _stopGate = new();
        private Task _stopTask;
        private int _disposed;

        public OrchestratorMqttService(IOrchestratorConfigurationService configuration, IMessageStateService deviceStateService, IStoredObjectService storedObjectService, IOnlineNodeService onlineNodeService, IAutomationProvider automation, ICommandOutbox commands, ILogger<OrchestratorMqttService> logger, IMatterReportSink matterSink = null, MqttClient mqttClient = null)
        {
            _logger = logger;
            _storedObjectService = storedObjectService;
            _configuration = configuration;
            _deviceStateService = deviceStateService;
            _onlineNodeService = onlineNodeService;
            _automation = automation ?? throw new ArgumentNullException(nameof(automation));
            _commands = commands ?? throw new ArgumentNullException(nameof(commands));
            _matterSink = matterSink;

            _reportTopic = Constants.Get("+", MqttTopic.Report); // Orchestrator is listening all reports...
            _nodeOnlineTopic = Constants.Get("+", MqttTopic.NodeOnline); // Orchestrator is listening all nodes...
            _commandResultTopic = Constants.Get("+", MqttTopic.CommandResult); // ...and every node's command results (design 7.1)
            _nodeStatusTopic = Constants.Get("+", MqttTopic.NodeStatus); // ...and configuration statuses (design 7.2)

            _client = mqttClient ?? new MqttClient(_configuration.OrchestratorConfiguration.Mqtt, logger);

            _workQueue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _cts = new CancellationTokenSource();
            _consumerTask = ProcessQueueAsync(_workQueue.Reader, "MQTT", _cts.Token);

            _storedObjectService.StoredObjectEvent += IStoredObjectService_StoredObjectEvent;
            _commands.UseTransport(this);
        }

        // Single consumer loop: sequential, centrally guarded processing of queued work.
        private async Task ProcessQueueAsync(ChannelReader<Func<Task>> reader, string queueName, CancellationToken cancellationToken)
        {
            try
            {
                while (await reader.WaitToReadAsync(cancellationToken))
                {
                    while (!cancellationToken.IsCancellationRequested && reader.TryRead(out var work))
                    {
                        try
                        {
                            await work();
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception x)
                        {
                            _logger.LogError(x, "Error while processing queued {Queue} work item", queueName);
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected on shutdown.
            }
        }

        private void Enqueue(Func<Task> work)
        {
            try
            {
                // The event contract is synchronous: apply backpressure instead of silently losing state.
                _workQueue.Writer.WriteAsync(work, _cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                _logger.LogWarning("Could not enqueue MQTT work item; service is stopping");
            }
            catch (ChannelClosedException)
            {
                _logger.LogWarning("Could not enqueue MQTT work item; queue is completed");
            }
        }

        void IStoredObjectService_StoredObjectEvent(Type type, dynamic obj, OperationType changeType)
        {
            //Send report when Variable changes
            if (type == typeof(Variable) && changeType is OperationType.Created or OperationType.Updated)
            {
                var report = (obj as Variable).CreateReport();
                Enqueue(() => SendReport(report));
            }

            //Send Configuration command to node if its configuration is being updated
            if (type == typeof(NodeDeviceConfiguration))
            {
                string id = changeType == OperationType.Deleted ? null : (obj as NodeDeviceConfiguration)?.Id;
                if (_matterSink != null)
                    Enqueue(_matterSink.OnConfigurationChangedAsync);
                if (id != null)
                    Enqueue(() => SendConfigurationCommand(id));
                // A deleted node's retained status would describe a configuration that no longer exists.
                if (changeType == OperationType.Deleted && obj is string deleted && !String.IsNullOrEmpty(deleted))
                    Enqueue(() => ClearNodeStatusAsync(deleted));
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _storedObjectService.StoredObjectEvent -= IStoredObjectService_StoredObjectEvent;
            _cts.Cancel();
            _workQueue.Writer.TryComplete();
            try
            {
                await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException x)
            {
                _logger.LogError(x, "MQTT processing did not stop within five seconds");
            }
            _client?.Dispose();
            _cts.Dispose();
        }

        public async Task SendCommand(string topic, Command command)
        {
            await _client.PublishAsync(topic, Json.SerializeIgnoreNulls(command));
            _deviceStateService.SetState(command);
        }

        public async Task SendConfigurationCommand(string id)
        {
            var configurationCommandTopic = Constants.Get(id, MqttTopic.Configuration);
            await _client.PublishAsync(configurationCommandTopic, Json.SerializeIgnoreNulls(CreateConfigurationCommand(id)));
        }

        private async Task ClearNodeStatusAsync(string id)
        {
            _onlineNodeService.SetStatus(id, null);
            await _client.PublishAsync(Constants.Get(id, MqttTopic.NodeStatus), "", retain: true);
        }

        public bool IsConnected => _client?.IsConnected() == true;

        Task ICommandTransport.PublishCommandAsync(string nodeId, string payload) =>
            _client.PublishAsync(Constants.Get(nodeId, MqttTopic.Command), payload);

        private async Task sendOrchestratorOnlineCommand()
        {
            //Retain message so nodes can get orchestrator info on reconnect
            await _client.PublishAsync(Constants.Get("", MqttTopic.OrchestratorOnline), "{\"isOnline\":true}", retain: true);
            _commands.TransportConnected();
        }

        public async Task Start()
        {
            try
            {
                // Attach the handler before subscribing so no messages are missed
                // in the window between subscription and handler registration.
                _client.MessageReceivedAsync += _client_MessageReceivedAsync;
                _client.ConnectedAsync += sendOrchestratorOnlineCommand;
                // Replace the retained presence if the orchestrator stops or its connection drops.
                _client.SetLastWill(Constants.Get("", MqttTopic.OrchestratorOnline), "{\"isOnline\":false}", true);
                await _client.Start(_reportTopic, _nodeOnlineTopic, _commandResultTopic, _nodeStatusTopic);
            }
            catch (Exception x)
            {
                throw new Exception("Could not connect to MQTT Broker", x);
            }
        }

        // A host stopped by its owner and by Run calls this twice at the same time; both wait for one stop.
        public Task Stop()
        {
            lock (_stopGate)
                return _stopTask ??= StopCoreAsync();
        }

        private async Task StopCoreAsync()
        {
            _client.MessageReceivedAsync -= _client_MessageReceivedAsync;
            _client.ConnectedAsync -= sendOrchestratorOnlineCommand;
            _storedObjectService.StoredObjectEvent -= IStoredObjectService_StoredObjectEvent;
            // Draining stores the remaining reports' workflow triggers in the outbox.
            _workQueue.Writer.TryComplete();
            await _consumerTask;
            await _client.Stop();
        }

        // Workflow triggers go to the durable outbox, so delivery to Elsa never holds up this queue.
        private async Task _client_MessageReceivedAsync(MqttEventArgs mqttEventArgs)
        {
            try
            {
                // Waits while the queue is full; the MQTT client then stops reading, and QoS 1 redelivers.
                await _workQueue.Writer.WriteAsync(() => HandleMessageAsync(mqttEventArgs), _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                _logger.LogWarning("Could not enqueue MQTT work item; service is stopping");
            }
            catch (ChannelClosedException)
            {
                _logger.LogWarning("Could not enqueue MQTT work item; queue is completed");
            }
        }

        internal async Task HandleMessageAsync(MqttEventArgs mqttEventArgs)
        {
            if (MqttClient.IsMatch(mqttEventArgs.Topic, _reportTopic))
            {
                var report = Report.Create(mqttEventArgs.Message);

                if (report == null)
                {
                    _logger.LogWarning("Couldn't create Report {Message}", mqttEventArgs.Message);
                    return;
                }

                var template = _configuration.GetReportTemplates().FirstOrDefault(x => x.Id == report.Id);
                if (template == null)
                    return; //Do not process reports that have not been defined

                _deviceStateService.SetState(report, template.MaintainHistory);

                //Mirror the report onto any Matter endpoint bound to it, so a controller sees the change
                _matterSink?.OnReport(report);

                //TODO validate report against template!

                // Stored in the outbox and delivered when a workflow node is online (design 7.1).
                await _automation.ReportReceivedAsync(report, _cts.Token);
            }
            else if (MqttClient.IsMatch(mqttEventArgs.Topic, _nodeOnlineTopic))
            {
                // An empty payload clears a retained message; anything else unreadable is a fault on the sender.
                if (string.IsNullOrWhiteSpace(mqttEventArgs.Message))
                    return;
                var onlineMessage = ReadOnlineMessage(mqttEventArgs.Message);
                if (onlineMessage == null)
                {
                    _logger.LogWarning("Ignored an unreadable presence message on {Topic}: {Payload}",
                        mqttEventArgs.Topic, Truncate(mqttEventArgs.Message, 200));
                    return;
                }
                var clientId = Constants.GetTopicId(mqttEventArgs.Topic, MqttTopic.NodeOnline);

                if (onlineMessage.IsOnline)
                {
                    _onlineNodeService.Add(new Models.OnlineNode()
                    {
                        Id = clientId,
                        OnlineNodeSettings = onlineMessage
                    });
                }
                else
                    _onlineNodeService.Remove(clientId);
                _automation.NodeOnlineChanged(clientId, onlineMessage);
                _commands.NodeOnlineChanged(clientId, onlineMessage);
                _matterSink?.OnNodeOnlineChanged(clientId, onlineMessage.IsOnline);
                // Sent on every presence, even when the node's retained status says it runs this configuration:
                // that status may be older than the node. A desired-state node answers an applied hash
                // without fetching or restarting anything (design 7.2).
                if (onlineMessage.IsOnline)
                    await SendConfigurationCommand(clientId);
            }
            else if (MqttClient.IsMatch(mqttEventArgs.Topic, _nodeStatusTopic))
            {
                var nodeId = Constants.GetTopicId(mqttEventArgs.Topic, MqttTopic.NodeStatus);
                if (string.IsNullOrWhiteSpace(mqttEventArgs.Message))
                {
                    _onlineNodeService.SetStatus(nodeId, null);
                    return;
                }
                var status = NodeStatusMessage.Create(mqttEventArgs.Message);
                if (status == null)
                {
                    _logger.LogWarning("Ignored an unreadable node status on {Topic}: {Payload}",
                        mqttEventArgs.Topic, Truncate(mqttEventArgs.Message, 200));
                    return;
                }
                var previous = _onlineNodeService.GetStatus(nodeId);
                _onlineNodeService.SetStatus(nodeId, status);
                if (status.State == NodeConfigurationState.Failed && (previous?.State != status.State || previous?.Error != status.Error))
                    _logger.LogWarning("Node {NodeId} could not apply its configuration and runs revision {Revision}: {Error}",
                        nodeId, status.AppliedRevision?.ToString() ?? "none", status.Error);
                else if (status.FailedDevices?.Count > 0 && previous?.FailedDevices?.Count != status.FailedDevices.Count)
                    _logger.LogWarning("Node {NodeId} applied revision {Revision}, but these devices are not running: {Devices}",
                        nodeId, status.AppliedRevision?.ToString() ?? "none", string.Join(", ", status.FailedDevices.Select(d => $"{d.Id} ({d.Message})")));
            }
            else if (MqttClient.IsMatch(mqttEventArgs.Topic, _commandResultTopic))
            {
                var result = CommandResult.Create(mqttEventArgs.Message);
                if (result == null)
                {
                    _logger.LogWarning("Ignored an unreadable command result on {Topic}: {Payload}",
                        mqttEventArgs.Topic, Truncate(mqttEventArgs.Message, 200));
                    return;
                }
                _commands.ResultReceived(Constants.GetTopicId(mqttEventArgs.Topic, MqttTopic.CommandResult), result);
            }
        }

        private static NodeOnlineMessage ReadOnlineMessage(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                return Json.Deserialize<NodeOnlineMessage>(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        private static string Truncate(string value, int length) =>
            value == null || value.Length <= length ? value : value[..length] + "...";

        // Revision and hash let a desired-state node skip a configuration it already runs (design 7.2).
        internal ConfigurationCommand CreateConfigurationCommand(string id)
        {
            var desired = _configuration.NodeConfigurations.FirstOrDefault(x => x.Id == id);
            return new ConfigurationCommand()
            {
                ApiBaseUrl = _configuration.OrchestratorConfiguration.Url,
                Revision = desired?.Revision,
                Hash = desired?.Hash
            };
        }

        // Every command goes through the outbox (design 7.1): it waits while its node is offline, and is
        // retried until the node answers or its time-to-live passes. Returns false for an unknown command.
        public Task<bool> ExecuteCommand(Command command)
        {
            if (command == null || String.IsNullOrWhiteSpace(command.Id))
            {
                _logger.LogWarning("Could not execute command without an identifier");
                return Task.FromResult(false);
            }

            var outputNodeId = _configuration.FindNodeId(command.Id);
            if (String.IsNullOrEmpty(outputNodeId))
            {
                _logger.LogWarning("Could not find a node for command {CommandId}", command.Id);
                return Task.FromResult(false);
            }

            _commands.Enqueue(command, outputNodeId);
            return Task.FromResult(true);
        }

        //This method send orchestrator reports to mqtt
        public async Task SendReport(Report report)
        {
            var id = _configuration.OrchestratorConfiguration.Id;
            var topic = Constants.Get(id, MqttTopic.Report);
            await _client.PublishAsync(topic, report.ToJson());
        }
    }
}
