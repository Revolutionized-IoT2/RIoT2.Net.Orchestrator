using System.Threading.Channels;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Grpc;
using RIoT2.Net.Orchestrator.Services.Matter;

namespace RIoT2.Net.Orchestrator.Services
{
    internal class OrchestratorMqttService : IOrchestratorMqttService, IDisposable, IAsyncDisposable
    {
        private MqttClient _client;
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IMessageStateService _deviceStateService;
        private readonly IStoredObjectService _storedObjectService;
        private readonly IOnlineNodeService _onlineNodeService;
        private readonly IMatterReportSink _matterSink;
        private readonly ILogger _logger;

        private string _reportTopic;
        private string _nodeOnlineTopic;

        // Bounded queue decouples MQTT callback threads / event handlers from processing.
        private readonly Channel<Func<Task>> _workQueue;
        private readonly Channel<Func<Task>> _workflowQueue;
        private readonly CancellationTokenSource _cts;
        private readonly Task _consumerTask;
        private readonly Task _workflowTask;
        private readonly WorkflowTriggerClient _workflowClient = new();
        private int _disposed;

        public OrchestratorMqttService(IOrchestratorConfigurationService configuration, IMessageStateService deviceStateService, IStoredObjectService storedObjectService, IOnlineNodeService onlineNodeService, ILogger<OrchestratorMqttService> logger, IMatterReportSink matterSink = null, MqttClient mqttClient = null)
        {
            _logger = logger;
            _storedObjectService = storedObjectService;
            _configuration = configuration;
            _deviceStateService = deviceStateService;
            _onlineNodeService = onlineNodeService;
            _matterSink = matterSink;

            _reportTopic = Constants.Get("+", MqttTopic.Report); // Orchestrator is listening all reports...
            _nodeOnlineTopic = Constants.Get("+", MqttTopic.NodeOnline); // Orchestrator is listening all nodes...

            _client = mqttClient ?? new MqttClient(_configuration.OrchestratorConfiguration.Mqtt.ClientId,
                _configuration.OrchestratorConfiguration.Mqtt.ServerUrl,
                _configuration.OrchestratorConfiguration.Mqtt.Username,
                _configuration.OrchestratorConfiguration.Mqtt.Password);

            _workQueue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _cts = new CancellationTokenSource();
            _workflowQueue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _consumerTask = ProcessQueueAsync(_workQueue.Reader, "MQTT", _cts.Token);
            _workflowTask = ProcessQueueAsync(_workflowQueue.Reader, "workflow", _cts.Token);

            _storedObjectService.StoredObjectEvent += IStoredObjectService_StoredObjectEvent;
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
            _workflowQueue.Writer.TryComplete();
            try
            {
                await Task.WhenAll(_consumerTask, _workflowTask).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException x)
            {
                _logger.LogError(x, "MQTT processing did not stop within five seconds");
            }
            LogAbandonedWork();
            await _workflowClient.DisposeAsync();
            _client?.Dispose();
            _cts.Dispose();
        }

        public async Task SendCommand(string topic, Command command)
        {
            await _client.Publish(topic, Json.SerializeIgnoreNulls(command));
            _deviceStateService.SetState(command);
        }

        public async Task SendConfigurationCommand(string id)
        {
            var configurationCommandTopic = Constants.Get(id, MqttTopic.Configuration);
            await _client.Publish(configurationCommandTopic, Json.SerializeIgnoreNulls(generateConfigurationCommand(id)));
        }

        private async Task sendOrchestratorOnlineCommand()
        {
            //Retain message so nodes can get orchestrator info on reconnect
            await _client.Publish(Constants.Get("", MqttTopic.OrchestratorOnline), "{\"isOnline\":true}", true);
        }

        public async Task Start()
        {
            try
            {
                // Attach the handler before subscribing so no messages are missed
                // in the window between subscription and handler registration.
                _client.MessageReceived += _client_MessageReceived;
                _client.ConnectedAsync += sendOrchestratorOnlineCommand;
                await _client.Start(_reportTopic, _nodeOnlineTopic);
            }
            catch (Exception x)
            {
                throw new Exception("Could not connect to MQTT Broker", x);
            }
        }

        public async Task Stop()
        {
            _client.MessageReceived -= _client_MessageReceived;
            _client.ConnectedAsync -= sendOrchestratorOnlineCommand;
            _storedObjectService.StoredObjectEvent -= IStoredObjectService_StoredObjectEvent;
            _workQueue.Writer.TryComplete();
            await _consumerTask;
            _workflowQueue.Writer.TryComplete();
            // Do not spend up to 1000 delivery deadlines draining a volatile queue on shutdown.
            _cts.Cancel();
            await _workflowTask;
            LogAbandonedWork();
            await _workflowClient.DisposeAsync();
            await _client.Stop();
        }

        private void LogAbandonedWork()
        {
            var count = 0;
            while (_workflowQueue.Reader.TryRead(out _))
                count++;
            if (count > 0)
                _logger.LogWarning("Discarded {Count} pending workflow deliveries during shutdown; no automatic replay", count);
        }

        // Only state processing can exert backpressure here; workflow delivery has its own bounded queue.
        private void _client_MessageReceived(MqttEventArgs mqttEventArgs)
        {
            Enqueue(() => HandleMessageAsync(mqttEventArgs));
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

                var workflowNode = _onlineNodeService.OnlineNodes.FirstOrDefault(x => x.OnlineNodeSettings.IsOnline && x.OnlineNodeSettings.NodeType == NodeType.Workflow);
                if (workflowNode != default)
                {
                    var settings = workflowNode.OnlineNodeSettings;
                    var url = string.IsNullOrWhiteSpace(settings.GrpcBaseUrl) ? settings.NodeBaseUrl : settings.GrpcBaseUrl;
                    var request = new TriggerRequest { Id = report.Id, Data = report.ToJson() };
                    if (!_workflowQueue.Writer.TryWrite(() => DeliverWorkflowAsync(url, request, _cts.Token)))
                        _logger.LogWarning("Workflow delivery queue is full or completed; report {ReportId} was not forwarded", report.Id);
                }
                else
                {
                    _logger.LogWarning("Could not process report {ReportId} because no workflow node is online", report.Id);
                }
            }
            else if (MqttClient.IsMatch(mqttEventArgs.Topic, _nodeOnlineTopic))
            {
                var onlineMessage = Json.Deserialize<NodeOnlineMessage>(mqttEventArgs.Message);
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
                _matterSink?.OnNodeOnlineChanged(clientId, onlineMessage.IsOnline);
                if (onlineMessage.IsOnline)
                    await SendConfigurationCommand(clientId);
            }
        }

        private async Task DeliverWorkflowAsync(string url, TriggerRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _workflowClient.TriggerAsync(url, request, cancellationToken);
                if (!response.Success)
                    _logger.LogWarning("Workflow engine did not accept report {ReportId}", request.Id);
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Workflow delivery failed for report {ReportId}; not retried automatically", request.Id);
            }
        }

        private ConfigurationCommand generateConfigurationCommand(string id) 
        {
            return new ConfigurationCommand()
            {
                ApiBaseUrl = _configuration.OrchestratorConfiguration.Url
            };
        }

        public async Task<bool> ExecuteCommand(Command command)
        {
            if (command == null || String.IsNullOrWhiteSpace(command.Id))
            {
                _logger.LogWarning("Could not execute command without an identifier");
                return false;
            }

            var outputNodeId = _configuration.FindNodeId(command.Id);
            if (String.IsNullOrEmpty(outputNodeId))
            {
                _logger.LogWarning("Could not find a node for command {CommandId}", command.Id);
                return false;
            }

            await SendCommand(Constants.Get(outputNodeId, MqttTopic.Command), command);
            return true;
        }

        //This method send orchestrator reports to mqtt
        public async Task SendReport(Report report)
        {
            var id = _configuration.OrchestratorConfiguration.Id;
            var topic = Constants.Get(id, MqttTopic.Report);
            await _client.Publish(topic, report.ToJson());
        }
    }
}
