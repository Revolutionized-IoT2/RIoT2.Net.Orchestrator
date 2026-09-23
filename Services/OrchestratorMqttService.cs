using System.Threading.Channels;
using Grpc.Net.Client;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;
using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Grpc;
using RIoT2.Net.Orchestrator.Services.Matter;

namespace RIoT2.Net.Orchestrator.Services
{
    internal class OrchestratorMqttService : IOrchestratorMqttService, IDisposable
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
        private readonly CancellationTokenSource _cts;
        private readonly Task _consumerTask;

        public OrchestratorMqttService(IOrchestratorConfigurationService configuration, IMessageStateService deviceStateService, IStoredObjectService storedObjectService, IOnlineNodeService onlineNodeService, ILogger<OrchestratorMqttService> logger, IMatterReportSink matterSink = null)
        {
            _logger = logger;
            _storedObjectService = storedObjectService;
            _configuration = configuration;
            _deviceStateService = deviceStateService;
            _onlineNodeService = onlineNodeService;
            _matterSink = matterSink;

            _reportTopic = Constants.Get("+", MqttTopic.Report); // Orchestrator is listening all reports...
            _nodeOnlineTopic = Constants.Get("+", MqttTopic.NodeOnline); // Orchestrator is listening all nodes...

            _client = new MqttClient(_configuration.OrchestratorConfiguration.Mqtt.ClientId,
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
            _consumerTask = Task.Run(() => ProcessQueueAsync(_cts.Token));

            _storedObjectService.StoredObjectEvent += IStoredObjectService_StoredObjectEvent;
        }

        // Single consumer loop: sequential, centrally guarded processing of queued work.
        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var work in _workQueue.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        await work();
                    }
                    catch (Exception x)
                    {
                        _logger.LogError(x, "Error while processing queued MQTT work item");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        private void Enqueue(Func<Task> work)
        {
            if (!_workQueue.Writer.TryWrite(work))
                _logger.LogWarning("Could not enqueue MQTT work item; queue is full or completed");
        }

        void IStoredObjectService_StoredObjectEvent(Type type, dynamic obj, OperationType changeType)
        {
            //Send report when Variable changes
            if (type == typeof(Variable) && changeType == OperationType.Updated)
            {
                var report = (obj as Variable).CreateReport();
                Enqueue(() => SendReport(report));
            }

            //Send Configuration command to node if its configuration is being updated
            if (type == typeof(NodeDeviceConfiguration) && changeType == OperationType.Updated)
            {
                string id = (obj as NodeDeviceConfiguration).Id;
                Enqueue(() => SendConfigurationCommand(id));
            }
        }

        public void Dispose()
        {
            _storedObjectService.StoredObjectEvent -= IStoredObjectService_StoredObjectEvent;
            _cts.Cancel();
            _workQueue.Writer.TryComplete();
            try
            {
                _consumerTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore errors during shutdown of the consumer task.
            }
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
            await _client.Publish(Constants.Get("", MqttTopic.OrchestratorOnline), null, true);
        }

        public async Task Start()
        {
            try
            {
                // Attach the handler before subscribing so no messages are missed
                // in the window between subscription and handler registration.
                _client.MessageReceived += _client_MessageReceived;
                await _client.Start(_reportTopic, _nodeOnlineTopic);

                await sendOrchestratorOnlineCommand();
            }
            catch (Exception x)
            {
                throw new Exception("Could not connect to MQTT Broker", x);
            }
        }

        public async Task Stop()
        {
            _workQueue.Writer.TryComplete();
            await _client.Stop();
        }

        // Callback stays lightweight: it only enqueues work and returns immediately.
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
                    using var channel = GrpcChannel.ForAddress(workflowNode.OnlineNodeSettings.NodeBaseUrl);
                    var client = new RIoTTriggerService.RIoTTriggerServiceClient(channel);

                    var response = await client.TriggerAsync(new TriggerRequest
                    {
                        Id = report.Id,
                        Data = report.ToJson()
                    });

                    if (!response.Success)
                        _logger.LogWarning("Workflow engine did not accept report {ReportId}", report.Id);
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

                    await SendConfigurationCommand(clientId);
                }
                else
                    _onlineNodeService.Remove(clientId);
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
