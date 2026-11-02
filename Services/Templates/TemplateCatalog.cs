using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Models;
using RIoT2.Net.Orchestrator.Services.Persistence;
using RIoT2.Net.Orchestrator.Services;

namespace RIoT2.Net.Orchestrator.Services.Templates
{
    /// <inheritdoc cref="ITemplateCatalog" />
    /// <remarks>
    /// The listings are built on every call from the current configuration, so a saved node or
    /// variable shows up at once. Device parameters are not copied: the listings are read by the UI
    /// and the workflow designer, which only need the template's identity and value model.
    /// </remarks>
    public sealed class TemplateCatalog : ITemplateCatalog
    {
        /// <summary>The node name that variable templates are listed under.</summary>
        public const string VariableNode = "Orchestrator";

        /// <summary>The device name that variable templates are listed under.</summary>
        public const string VariableDevice = "Variable";

        private readonly IOrchestratorConfigurationService _configuration;
        private readonly IStoredObjectStore _storedObjects;

        public TemplateCatalog(IOrchestratorConfigurationService configuration, IStoredObjectStore storedObjects)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(storedObjects);
            _configuration = configuration;
            _storedObjects = storedObjects;
        }

        public IReadOnlyList<NodeReportTemplate> GetReportTemplates() =>
            Devices().SelectMany(x => (x.Device.ReportTemplates ?? []).Select(t => ToNodeTemplate(x.Node, x.Device, t))).ToList();

        public IReadOnlyList<NodeCommandTemplate> GetCommandTemplates() =>
            Devices().SelectMany(x => (x.Device.CommandTemplates ?? []).Select(t => ToNodeTemplate(x.Node, x.Device, t))).ToList();

        public IReadOnlyList<VariableTemplate> GetVariableTemplates()
        {
            // The id is a required setting (OrchestratorOptions.Load).
            var orchestratorId = _configuration.OrchestratorConfiguration?.Id!;
            return _storedObjects.GetAll<Variable>().Select(v => new VariableTemplate
            {
                NodeId = orchestratorId,
                Node = VariableNode,
                DeviceId = "",
                Device = VariableDevice,
                Name = v.Name,
                Id = v.Id,
                Type = v.Type,
                Address = "",
                Model = v.Value
            }).ToList();
        }

        public NodeReportTemplate? FindReportTemplate(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            foreach (var (node, device) in Devices())
            {
                var template = device.ReportTemplates?.FirstOrDefault(t => t.Id == id);
                if (template != null)
                    return ToNodeTemplate(node, device, template);
            }
            return null;
        }

        private IEnumerable<(NodeDeviceConfiguration Node, DeviceConfiguration Device)> Devices() =>
            (_configuration.NodeConfigurations ?? [])
                .SelectMany(node => (node.DeviceConfigurations ?? []).Select(device => (node, device)));

        private static NodeReportTemplate ToNodeTemplate(NodeDeviceConfiguration node, DeviceConfiguration device, ReportTemplate t) => new()
        {
            NodeId = node.Id,
            Node = node.Name,
            DeviceId = device.Id,
            Device = device.Name,
            Filters = t.Filters,
            Name = t.Name,
            Id = t.Id,
            Type = t.Type,
            Address = t.Address,
            Model = t.Model,
            MaintainHistory = t.MaintainHistory
        };

        private static NodeCommandTemplate ToNodeTemplate(NodeDeviceConfiguration node, DeviceConfiguration device, CommandTemplate t) => new()
        {
            NodeId = node.Id,
            Node = node.Name,
            DeviceId = device.Id,
            Device = device.Name,
            Name = t.Name,
            Id = t.Id,
            Type = t.Type,
            Address = t.Address,
            Model = t.Model
        };
    }
}
