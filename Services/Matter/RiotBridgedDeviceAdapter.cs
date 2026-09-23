using System.Text.Json;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Matter.ControlBridge;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Connects one bridged Matter endpoint to the RIoT device behind it: RIoT reports drive the endpoint's
    /// cluster attributes, and controller-driven attribute changes are turned back into RIoT commands.
    /// </summary>
    /// <remarks>
    /// Both directions move through the cluster attribute, so an echo guard is needed: applying a report
    /// raises the same change event a controller's write does. The guard is thread-static because cluster
    /// change events fire synchronously on the thread performing the write, so it suppresses exactly the
    /// dispatch caused by this adapter's own report application and nothing else.
    /// </remarks>
    public sealed class RiotBridgedDeviceAdapter : IBridgedDeviceAdapter
    {
        [ThreadStatic]
        private static bool _applyingReport;

        private readonly Func<IEnumerable<Report>> _reportSource;
        private readonly Func<MatterCommandBinding, ValueModel, Task> _dispatch;
        private readonly ILogger _logger;
        private readonly List<Subscription> _subscriptions = [];

        private BridgedDevice _device;

        /// <param name="template">The endpoint declared by the RIoT device.</param>
        /// <param name="nodeId">The id of the node owning the device, used for status and reachability.</param>
        /// <param name="deviceId">The id of the RIoT device configuration the endpoint was declared by.</param>
        /// <param name="reportSource">Supplies the currently known reports, used to seed attributes on attach.</param>
        /// <param name="dispatch">Sends a RIoT command built from a controller-driven attribute change.</param>
        /// <param name="logger">Receives binding and dispatch diagnostics.</param>
        public RiotBridgedDeviceAdapter(
            MatterEndpointTemplate template,
            string nodeId,
            string deviceId,
            Func<IEnumerable<Report>> reportSource,
            Func<MatterCommandBinding, ValueModel, Task> dispatch,
            ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(reportSource);
            ArgumentNullException.ThrowIfNull(dispatch);

            Template = template;
            NodeId = nodeId;
            DeviceId = deviceId;
            _reportSource = reportSource;
            _dispatch = dispatch;
            _logger = logger;
        }

        /// <summary>The endpoint declaration this adapter serves.</summary>
        public MatterEndpointTemplate Template { get; }

        /// <summary>The id of the node owning the RIoT device.</summary>
        public string NodeId { get; }

        /// <summary>The id of the RIoT device configuration the endpoint was declared by.</summary>
        public string DeviceId { get; }

        /// <summary>The bridged endpoint, once attached.</summary>
        public BridgedDevice Device => _device;

        /// <inheritdoc />
        public ValueTask AttachAsync(BridgedDevice device, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(device);
            _device = device;

            foreach (var binding in Template.Commands ?? [])
            {
                Subscribe(binding);
            }

            // Seed from the state the orchestrator already holds, so a controller commissioning the bridge
            // sees current values rather than cluster defaults.
            foreach (var report in _reportSource() ?? [])
            {
                OnReport(report);
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DetachAsync(BridgedDevice device, CancellationToken cancellationToken = default)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Accessor.Unsubscribe?.Invoke(subscription.Handler);
            }

            _subscriptions.Clear();
            _device = null;
            return ValueTask.CompletedTask;
        }

        /// <summary>Marks the endpoint reachable or not, following the owning node's online state.</summary>
        public void SetReachable(bool reachable)
        {
            if (_device is not null)
            {
                _device.Reachable = reachable;
            }
        }

        /// <summary>
        /// Applies a RIoT report to every cluster attribute bound to it. Reports for other devices are
        /// ignored, so the bridge can hand every report to every adapter.
        /// </summary>
        public void OnReport(Report report)
        {
            if (_device is null || report is null || string.IsNullOrEmpty(report.Id) || report.Value is null)
                return;

            foreach (var binding in Template.Attributes ?? [])
            {
                if (!string.Equals(binding.ReportTemplateId, report.Id, StringComparison.Ordinal))
                    continue;

                // An empty filter on the binding accepts every filter value the device reports.
                if (!string.IsNullOrEmpty(binding.Filter) && !string.Equals(binding.Filter, report.Filter, StringComparison.Ordinal))
                    continue;

                Apply(binding, report);
            }
        }

        private void Apply(MatterAttributeBinding binding, Report report)
        {
            if (!TryExtract(report.Value, binding.ValuePath, out var raw))
            {
                _logger?.LogDebug(
                    "Matter endpoint {Endpoint}: report {ReportId} carried no value at path '{Path}' for {Attribute}",
                    Template.Id, report.Id, binding.ValuePath, binding.Attribute);
                return;
            }

            if (!MatterEndpointComposer.TryBind(_device, binding.Attribute, out var accessor))
            {
                _logger?.LogWarning(
                    "Matter endpoint {Endpoint}: attribute {Attribute} is not carried by device type {DeviceType}",
                    Template.Id, binding.Attribute, Template.DeviceType);
                return;
            }

            var value = MatterValueScaler.ToMatter(raw, binding.Scale);

            _applyingReport = true;
            try
            {
                accessor.Write(value);
            }
            catch (Exception x)
            {
                _logger?.LogError(x, "Matter endpoint {Endpoint}: could not apply {Attribute}", Template.Id, binding.Attribute);
            }
            finally
            {
                _applyingReport = false;
            }
        }

        private void Subscribe(MatterCommandBinding binding)
        {
            if (!MatterEndpointComposer.TryBind(_device, binding.Attribute, out var accessor))
            {
                _logger?.LogWarning(
                    "Matter endpoint {Endpoint}: command binding for {Attribute} does not match device type {DeviceType}",
                    Template.Id, binding.Attribute, Template.DeviceType);
                return;
            }

            if (!accessor.CanSubscribe)
            {
                // A sensor attribute is device-driven; a controller has nothing to write, so a command
                // binding on it would never fire.
                _logger?.LogWarning(
                    "Matter endpoint {Endpoint}: attribute {Attribute} raises no change event and cannot drive a command",
                    Template.Id, binding.Attribute);
                return;
            }

            // Held as a single delegate instance so the unsubscribe on detach matches the subscribe.
            EventHandler handler = (_, _) =>
            {
                if (_applyingReport)
                    return; // Our own report application; sending a command back would echo it to the device.

                Dispatch(binding, accessor);
            };

            accessor.Subscribe(handler);
            _subscriptions.Add(new Subscription(accessor, handler));
        }

        private void Dispatch(MatterCommandBinding binding, MatterAttributeAccessor accessor)
        {
            ValueModel payload;
            try
            {
                payload = BuildPayload(binding, accessor.Read());
            }
            catch (Exception x)
            {
                _logger?.LogError(x, "Matter endpoint {Endpoint}: could not build the command payload for {Attribute}",
                    Template.Id, binding.Attribute);
                return;
            }

            // The cluster raises its change event synchronously inside the Matter invoke path, so the
            // command is sent without blocking it; a failure is logged rather than surfaced to the controller.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _dispatch(binding, payload);
                }
                catch (Exception x)
                {
                    _logger?.LogError(x, "Matter endpoint {Endpoint}: could not send command {CommandId}",
                        Template.Id, binding.CommandTemplateId);
                }
            });
        }

        private static ValueModel BuildPayload(MatterCommandBinding binding, double matterValue)
        {
            var scaled = MatterValueScaler.ToRiot(matterValue, binding.Scale);
            var leaf = IsBoolean(binding.Attribute) ? new ValueModel(scaled != 0d) : new ValueModel(scaled);

            if (string.IsNullOrEmpty(binding.ValuePath))
                return leaf;

            // A path only means anything inside an entity, so the binding must carry the entity to write into.
            var target = string.IsNullOrWhiteSpace(binding.ValueTemplateJson)
                ? new ValueModel("{}")
                : new ValueModel(binding.ValueTemplateJson);

            return target.Update(leaf, binding.ValuePath);
        }

        /// <summary>Whether the attribute is carried as a boolean in RIoT, rather than as a number.</summary>
        private static bool IsBoolean(MatterAttribute attribute) => attribute is
            MatterAttribute.OnOff or MatterAttribute.Occupancy or MatterAttribute.BooleanStateValue;

        /// <summary>
        /// Reads the value at <paramref name="path"/> out of <paramref name="value"/> as a double.
        /// </summary>
        /// <remarks>
        /// <see cref="ValueModel.GetValue{T}"/> is not used here: it throws when the stored JSON type does
        /// not match the requested one, and every binding would otherwise need to know whether its device
        /// reports a number, a boolean or a string. Walking the JSON directly lets one code path accept all
        /// three and normalise booleans to 0 and 1.
        /// </remarks>
        private static bool TryExtract(ValueModel value, string path, out double result)
        {
            result = 0d;

            using var document = JsonDocument.Parse(value.ToJson());
            var element = document.RootElement;

            if (!string.IsNullOrEmpty(path) && !TryWalk(ref element, path))
                return false;

            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    return element.TryGetDouble(out result);
                case JsonValueKind.True:
                    result = 1d;
                    return true;
                case JsonValueKind.False:
                    result = 0d;
                    return true;
                case JsonValueKind.String:
                    var text = element.GetString();
                    if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result))
                        return true;
                    if (bool.TryParse(text, out var parsed))
                    {
                        result = parsed ? 1d : 0d;
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        // Walks a dotted ValueModel path, with "[i]" indexing into an array: "lights[0].state.on".
        private static bool TryWalk(ref JsonElement element, string path)
        {
            foreach (var rawSegment in path.Split('.'))
            {
                var segment = rawSegment;
                var bracket = segment.IndexOf('[');
                var name = bracket < 0 ? segment : segment[..bracket];

                if (name.Length > 0)
                {
                    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                        return false;
                }

                while (bracket >= 0)
                {
                    var close = segment.IndexOf(']', bracket);
                    if (close < 0 || !int.TryParse(segment[(bracket + 1)..close], out var index))
                        return false;

                    if (element.ValueKind != JsonValueKind.Array || index < 0 || index >= element.GetArrayLength())
                        return false;

                    element = element[index];
                    bracket = segment.IndexOf('[', close);
                }
            }

            return true;
        }

        private readonly record struct Subscription(MatterAttributeAccessor Accessor, EventHandler Handler);
    }
}
