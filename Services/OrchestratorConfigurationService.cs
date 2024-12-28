using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Orchestrator.Services
{
    public class OrchestratorConfigurationService : IOrchestratorConfigurationService
    {
        private OrchestratorConfiguration _configuration;
        private List<NodeDeviceConfiguration> _nodes;
        private ILogger _logger;
        private DashboardConfiguration _dashboardConfiguration;
        private IStoredObjectService _storedObjectService;
        private IEnumerable<Variable> _variables;

        public OrchestratorConfigurationService(IConfiguration configuration, IStoredObjectService storedObjectService, ILogger<OrchestratorConfigurationService> logger) 
        {
            _logger = logger;
            _storedObjectService = storedObjectService;
            readOrchestratorConfiguration();
            readNodesConfigurations();
        }
        public OrchestratorConfiguration OrchestratorConfiguration { get { return _configuration; } }
        public List<NodeDeviceConfiguration> NodeConfigurations { get { return _nodes; } }

        public string FindNodeId(string commandId)
        {
            var node = _nodes.FirstOrDefault(x => x.DeviceConfigurations.Any(y => y.CommandTemplates.Any(c => c.Id == commandId)));
            return node?.Id;
        }

        public IEnumerable<CommandTemplate> GetCommandTemplates()
        {
            var templates = _nodes
                .Where(x => x.DeviceConfigurations != null)
                .SelectMany(x => x.DeviceConfigurations
                .Where(x => x.CommandTemplates != null)
                .SelectMany(d => d.CommandTemplates))
                .ToList();

            foreach (var v in _variables)
                templates.Add(v.GetAsCommandTemplate());

            return templates;
        }

        public IEnumerable<ReportTemplate> GetReportTemplates()
        {
            var templates = _nodes.Where(x => x.DeviceConfigurations != null)
                .SelectMany(x => x.DeviceConfigurations
                .Where(r => r.ReportTemplates != null)
                .SelectMany(d => d.ReportTemplates))
                .ToList();

            //Add variables to templates
            foreach (var v in _variables)
                templates.Add(v.GetAsReportTemplate());

            return templates;
        }

        public DashboardConfiguration DashboardConfiguration { get { return _dashboardConfiguration; } }

        private void readOrchestratorConfiguration() 
        {
            _configuration = new OrchestratorConfiguration()
            {
                Id = Environment.GetEnvironmentVariable("RIOT2_ORCHESTRATOR_ID"),
                BaseUrl = Environment.GetEnvironmentVariable("RIOT2_ORCHESTRATOR_URL"),
                Mqtt = new MqttConfiguration() 
                {
                    ClientId = Environment.GetEnvironmentVariable("RIOT2_ORCHESTRATOR_ID"),
                    Password = Environment.GetEnvironmentVariable("RIOT2_MQTT_PASSWORD"),
                    ServerUrl = Environment.GetEnvironmentVariable("RIOT2_MQTT_IP"),
                    Username = Environment.GetEnvironmentVariable("RIOT2_MQTT_USERNAME")
                },
                Qnap = new QnapConfiguration() 
                {
                    ServerUrl = Environment.GetEnvironmentVariable("RIOT2_QNAP_IP"),
                    Username = Environment.GetEnvironmentVariable("RIOT2_QNAP_USERNAME"),
                    Password  = Environment.GetEnvironmentVariable("RIOT2_QNAP_PASSWORD")
                }
            };
        }

        private void readNodesConfigurations() 
        {
            try
            {
                _nodes = _storedObjectService.GetAll<NodeDeviceConfiguration>().ToList();
                _dashboardConfiguration = _storedObjectService.GetAll<DashboardConfiguration>().FirstOrDefault();
                _variables = _storedObjectService.GetAll<Variable>();

                //Create default dashboard if there is nothing stored
                if (_dashboardConfiguration == null) 
                {
                    _dashboardConfiguration = new DashboardConfiguration()
                    {
                        Name = "",
                        Id = "",
                        Pages = []
                    };
                }

                updateNodeIdToCommandTemplates();
                refreshDashboardTemplates();
            }
            catch (Exception e)
            {
                _logger.LogError($"Could not read node configurations: {e.Message}");
            }
        }

        private void updateNodeIdToCommandTemplates() 
        {
            if(_nodes == null || _nodes.Count == 0)
                return;

            foreach (var node in _nodes) 
            {
                if (node.DeviceConfigurations == null)
                    continue;

                foreach (var x in node.DeviceConfigurations) 
                {
                    if (x.CommandTemplates == null)
                        continue;
          
                    foreach (var c in x.CommandTemplates) 
                        c.NodeId = node.Id;
                }
            }
        }

        private void refreshDashboardTemplates() 
        {
            if (_nodes == null || _nodes.Count == 0)
                return;

            var currentReportTemplates = GetReportTemplates();
            var currentCommandTemplates = GetCommandTemplates();

            foreach (var page in _dashboardConfiguration.Pages) 
            {
                foreach (var comp in page.Components) 
                {
                    foreach (var elem in comp.Elements) 
                    {
                        elem.ReportTemplate = currentReportTemplates.FirstOrDefault(x => x.Id == elem.ReportTemplate?.Id);
                        elem.CommandTemplate = currentCommandTemplates.FirstOrDefault(x => x.Id == elem.CommandTemplate?.Id);
                    }
                }
            }
        }

        public string SaveNodeConfiguration(string json)
        {
            try
            {
                NodeDeviceConfiguration conf = Json.DeserializeAutoTypeNameHandling<NodeDeviceConfiguration>(json);
                if (conf != null)
                {
                    return SaveNodeConfiguration(conf);
                }
                else 
                {
                    _logger.LogWarning("Could not serialize json to NodeDeviceConfiguration: {json}", json);
                }
            }
            catch (Exception x) 
            {
                _logger.LogError(x, "Error saving node from json: {json}", json);
            }
            return null;
        }

        public string SaveNodeConfiguration(NodeDeviceConfiguration configuration)
        {
            if (String.IsNullOrEmpty(configuration.Id)) 
            {
                _logger.LogWarning("Could not save node configuration. ID missing.");
                return null;
            }

            //Ensure that all sub items have ID
            foreach (var dev in configuration.DeviceConfigurations)
            {
                if (String.IsNullOrEmpty(dev.Id))
                    dev.Id = Guid.NewGuid().ToString();

                if (dev.ReportTemplates == null) 
                {
                    dev.ReportTemplates = [];
                }
                else 
                {
                    foreach (var r in dev.ReportTemplates)
                    {
                        if (String.IsNullOrEmpty(r.Id))
                            r.Id = Guid.NewGuid().ToString();
                    }
                }

                if (dev.CommandTemplates == null)
                {
                    dev.CommandTemplates = [];
                }
                else 
                {
                    foreach (var c in dev.CommandTemplates ?? Enumerable.Empty<CommandTemplate>())
                    {
                        if (String.IsNullOrEmpty(c.Id))
                            c.Id = Guid.NewGuid().ToString();
                    }
                }
            }

            _storedObjectService.Save(configuration);
            _nodes = _storedObjectService.GetAll<NodeDeviceConfiguration>().ToList();
            return configuration.Id;
        }
        public string SaveDashboardConfiguration(string json)
        {
            DashboardConfiguration dashboard = Json.DeserializeAutoTypeNameHandling<DashboardConfiguration>(json);
            if (dashboard != null)
                return SaveDashboardConfiguration(dashboard);

            _logger.LogError("Could not save dashboard configuration file from json: {json}", json);
            return null;
        }

        public string SaveDashboardConfiguration(DashboardConfiguration dashboard)
        {
            try
            {
                if (dashboard.Pages == null)
                {
                    dashboard.Pages = [];
                }
                else 
                {
                    //remove template data before saving...
                    foreach (var p in dashboard.Pages)
                    {
                        if (p.Components == null)
                        {
                            p.Components = [];
                        }
                        else 
                        {
                            foreach (var c in p.Components)
                            {
                                if (c.Elements == null) 
                                {
                                    c.Elements = [];
                                }
                                else 
                                {
                                    foreach (var e in c.Elements)
                                    {
                                        e.ReportTemplate = e.ReportTemplate != null ? new ReportTemplate { Id = e.ReportTemplate.Id } : null;
                                        e.CommandTemplate = e.CommandTemplate != null ? new CommandTemplate { Id = e.CommandTemplate.Id } : null;
                                    }
                                }
                            }
                        }
                    }
                }

                var id = _storedObjectService.Save(dashboard);
                _dashboardConfiguration = _storedObjectService.GetAll<DashboardConfiguration>().FirstOrDefault();

                refreshDashboardTemplates();
                return id;
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not save dashboard configuration file");
                return null;
            }
        }

        public void DeleteNodeConfiguration(string nodeId)
        {
            _storedObjectService.Delete<NodeDeviceConfiguration>(nodeId);
            _nodes = _storedObjectService.GetAll<NodeDeviceConfiguration>().ToList();
        }
    }
}
