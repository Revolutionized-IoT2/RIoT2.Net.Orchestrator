using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Orchestrator.Models;

namespace RIoT2.Net.Orchestrator.Services
{
    public class OnlineNodeService : IOnlineNodeService
    {
        private List<OnlineNode> _onlineNodes;
        private ILogger<OnlineNodeService> _logger;

        public OnlineNodeService(ILogger<OnlineNodeService> logger)
        {
            _logger = logger;
            _onlineNodes = new List<OnlineNode>();
        }

        public IEnumerable<OnlineNode> OnlineNodes { get { return _onlineNodes; } }

        public void Add(OnlineNode node)
        {
            var existingNode = _onlineNodes.FirstOrDefault(x => x.Id == node.Id);
            if (existingNode == default)
            {
                _onlineNodes.Add(node);
            }
            else
            {
                existingNode.OnlineNodeSettings = node.OnlineNodeSettings;
            }
        }

        public void Remove(string id)
        {
            int removeIdx = -1;
            for (int x = 0; x < _onlineNodes.Count; x++)
            {
                if (_onlineNodes[x].Id == id)
                {
                    removeIdx = x;
                    break;
                }
            }

            if (removeIdx > -1)
                _onlineNodes.RemoveAt(removeIdx);
        }

        public async Task<List<DeviceConfiguration>> LoadDeviceConfigurationTemplateAsync(string id) 
        {
            var onlineNode = _onlineNodes.FirstOrDefault(x => x.Id == id);
            if (onlineNode == default)
                return null;

            try 
            {
                var response = await Web.GetAsync(onlineNode.OnlineNodeSettings.NodeBaseUrl + Constants.ApiConfigurationTemplateUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var deviceConfiguration = Json.Deserialize<List<DeviceConfiguration>>(json);
                    return deviceConfiguration;
                }
            }
            catch(Exception x) 
            {
                _logger.LogError(x, $"Could not load configuration template for {id}");
            }
            return null;
        }

        public async Task<List<DeviceStatus>> LoadDeviceStatusFromNodeAsync(string nodeId)
        {
            var onlineNode = _onlineNodes.FirstOrDefault(x => x.Id == nodeId);
            if (onlineNode == default)
                return null;

            try
            {
                var response = await Web.GetAsync(onlineNode.OnlineNodeSettings.NodeBaseUrl + Constants.ApiDeviceStateUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var listOfDeviceStatus = Json.Deserialize<List<DeviceStatus>>(json);
                    return listOfDeviceStatus;
                }
            }
            catch (Exception x)
            {
                _logger.LogError(x, $"Could not load Device status data for node: {nodeId}");
            }
            return null;
        }

        public async Task<Dictionary<string, List<DeviceConfiguration>>> LoadDeviceConfigurationTemplatesAsync()
        {
            var results = new Dictionary<string, List<DeviceConfiguration>>();

            Dictionary<string, Task<List<DeviceConfiguration>>> requests = new Dictionary<string, Task<List<DeviceConfiguration>>>();
            foreach (var onlineNode in _onlineNodes)
                requests.Add(onlineNode.Id, LoadDeviceConfigurationTemplateAsync(onlineNode.Id));

            if(requests.Count == 0)
                return results;

            await Task.WhenAll(requests.Values.ToArray());

            foreach (var key in requests.Keys)
                results.Add(key, requests[key].Result);

            return results;
        }
    }
}
