using System.Runtime.InteropServices;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;
using RIoT2.Core;
using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Services
{
    internal class OrchestratorMqttService : IOrchestratorMqttService, IDisposable
    {
        private MqttClient _client;
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IRuleProcessorService _processorService;
        private readonly IMessageStateService _deviceStateService;
        private readonly IStoredObjectService _ruleManagementService;
        private readonly IOnlineNodeService _onlineNodeService;
        private readonly ILogger _logger; 

        private string _reportTopic;
        private string _nodeOnlineTopic;

        public OrchestratorMqttService(IOrchestratorConfigurationService configuration, IRuleProcessorService workflowService, IMessageStateService deviceStateService, IStoredObjectService ruleManagementService, IOnlineNodeService onlineNodeService, ILogger<OrchestratorMqttService> logger)
        {
            _logger = logger;
            _ruleManagementService = ruleManagementService;
            _configuration = configuration;
            _processorService = workflowService;
            _deviceStateService = deviceStateService;
            _onlineNodeService = onlineNodeService;

            _reportTopic = Constants.Get("+", MqttTopic.Report); // Orchestrator is listening all reports...
            _nodeOnlineTopic = Constants.Get("+", MqttTopic.NodeOnline); // Orchestrator is listening all nodes...

            _client = new MqttClient(_configuration.OrchestratorConfiguration.Mqtt.ClientId,
                _configuration.OrchestratorConfiguration.Mqtt.ServerUrl,
                _configuration.OrchestratorConfiguration.Mqtt.Username,
                _configuration.OrchestratorConfiguration.Mqtt.Password);

            _ruleManagementService.StoredObjectEvent += IStoredObjectService_StoredObjectEvent;
        }

        void IStoredObjectService_StoredObjectEvent(Type type, dynamic obj, OperationType changeType)
        {
            //Send report when Variable changes
            if (type == typeof(Variable) && changeType == OperationType.Updated)
            {
                SendReport((obj as Variable).CreateReport()).Wait();
            }

            //Send Configuration command to node if its configuration is being updated
            if (type == typeof(NodeDeviceConfiguration) && changeType == OperationType.Updated)
            {
                SendConfigurationCommand((obj as NodeDeviceConfiguration).Id).Wait();
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
        }

        public async Task SendCommand(string topic, Command command)
        {
            _deviceStateService.SetState(command);
            await _client.Publish(topic, Json.SerializeIgnoreNulls(command));
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
                await _client.Start(_reportTopic, _nodeOnlineTopic);
                _client.MessageReceived += _client_MessageReceived;

                await sendOrchestratorOnlineCommand();
            }
            catch (Exception x)
            {
                throw new Exception("Could not connect to MQTT Broker", x);
            }
        }

        public async Task Stop()
        {
            await _client.Stop();
        }

        private async void _client_MessageReceived(MqttEventArgs mqttEventArgs)
        {
            try
            {
                if (MqttClient.IsMatch(mqttEventArgs.Topic, _reportTopic))
                {
                    var report = Report.Create(mqttEventArgs.Message);

                    if (report == null)
                    {
                        _logger.LogWarning("Couldn't create Report {mqttEventArgs.Message}", mqttEventArgs.Message);
                        return;
                    }

                    var template = _configuration.GetReportTemplates().FirstOrDefault(x => x.Id == report.Id);
                    if (template == null)
                        return; //Do not process reports that have not been defined

                    _deviceStateService.SetState(report, template.MaintainHistory);

                    //TODO validate report against template!

                    //Re-route report to External Workflow Engine if configured
                    if (_configuration.OrchestratorConfiguration.UseExtWorkflowEngine)
                    {
                        var workflowNode = _onlineNodeService.OnlineNodes.FirstOrDefault(x => x.OnlineNodeSettings.IsOnline && x.OnlineNodeSettings.NodeType == NodeType.Workflow);
                        if (workflowNode != default)
                        {
                            var url = workflowNode.OnlineNodeSettings.NodeBaseUrl + Constants.ApiWorkflowTriggerUrl.Replace("{id}", report.Id);
                            await Web.PostAsync(url, report.ToJson());
                        }
                        else
                        {
                            _logger.LogWarning("Could not process report {report.Id} because no workflow node is online", report.Id);
                        }
                    }
                    else // use internal rule processor
                    {
                        var rules = new List<Rule>();
                        foreach (var rule in _ruleManagementService.GetAll<Rule>())
                        {
                            if (!rule.IsActive)
                                continue;

                            if (rule.RuleItems == null || rule.RuleItems.Count < 2)
                                continue;

                            var trigger = rule.RuleItems.First() as RuleTrigger;
                            if (trigger?.ReportId != report.Id)
                                continue;

                            if (!String.IsNullOrEmpty(report.Filter) && !String.IsNullOrEmpty(trigger?.Filter) && trigger?.Filter.ToLower() != report.Filter.ToLower())
                                continue;

                            rules.Add(rule);
                        }

                        if (rules.Count > 0)
                            await _processorService.ProcessReportAsync(report, rules, processOutputs);
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
            catch (Exception x) 
            {
                _logger.LogError(x, "Could not handle mqtt message {mqttEventArgs.Message}", mqttEventArgs.Message);
            }
        }

        private ConfigurationCommand generateConfigurationCommand(string id) 
        {
            return new ConfigurationCommand()
            {
                ApiBaseUrl = _configuration.OrchestratorConfiguration.Url
            };
        }

        public async Task ProcessOutput(RuleEvaluationResult output) 
        {
            if (String.IsNullOrEmpty(output.CommandId))
                return;

            if (output.Operation == OutputOperation.Variable)
            {
                var outputVariable = _ruleManagementService.GetAll<Variable>().FirstOrDefault(x => x.Id == output.CommandId);
                if (outputVariable == default)
                    return;

                outputVariable.ValueAsJson = output.Value.ToJson();
                _ruleManagementService.Save(outputVariable);
                return;
            }

            var outputNodeId = _configuration.FindNodeId(output.CommandId);
            if (String.IsNullOrEmpty(outputNodeId))
                return;

            var cmdTopic = Constants.Get(outputNodeId, MqttTopic.Command);

            var cmd = new Command()
            {
                Id = output.CommandId,
                Value = output.Value
            };

            await SendCommand(cmdTopic, cmd);
        } 

        private async Task processOutputs(List<RuleEvaluationResult> outputs)
        {
            List<Task> commandTasks = new List<Task>();
            foreach (var output in outputs)
                commandTasks.Add(ProcessOutput(output));

            await Task.WhenAll(commandTasks);
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
