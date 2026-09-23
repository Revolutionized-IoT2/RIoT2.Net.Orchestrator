using RIoT2.Core.Models.Matter;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.ControlBridge;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
// Disambiguates from Microsoft.AspNetCore.Http.Endpoint, which ImplicitUsings brings into scope.
using Endpoint = RIoT2.Matter.Device.Endpoint;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Translates a RIoT <see cref="MatterEndpointTemplate"/> into Matter: the device type and cluster set
    /// a bridged endpoint is composed from, and a uniform accessor over the cluster attribute behind each
    /// <see cref="MatterAttribute"/>.
    /// </summary>
    /// <remarks>
    /// Every attribute is read and written as a <see cref="double"/>, with booleans carried as 0 or 1, so
    /// the adapter needs no per-attribute typing. Only attributes a controller can drive expose a
    /// subscription; the sensor clusters are device-driven and have no change events.
    /// </remarks>
    public static class MatterEndpointComposer
    {
        /// <summary>Maps a declared device type onto the Matter device type its endpoint advertises.</summary>
        /// <exception cref="NotSupportedException">The declared device type has no Matter counterpart yet.</exception>
        public static DeviceType Resolve(MatterDeviceType deviceType) => deviceType switch
        {
            MatterDeviceType.OnOffLight => StandardDeviceTypes.OnOffLight,
            MatterDeviceType.DimmableLight => StandardDeviceTypes.DimmableLight,
            MatterDeviceType.ColorTemperatureLight => StandardDeviceTypes.ColorTemperatureLight,
            MatterDeviceType.ExtendedColorLight => StandardDeviceTypes.ExtendedColorLight,
            MatterDeviceType.OnOffPlugInUnit => StandardDeviceTypes.OnOffPlugInUnit,
            MatterDeviceType.DimmablePlugInUnit => StandardDeviceTypes.DimmablePlugInUnit,
            MatterDeviceType.ContactSensor => StandardDeviceTypes.ContactSensor,
            MatterDeviceType.LightSensor => StandardDeviceTypes.LightSensor,
            MatterDeviceType.OccupancySensor => StandardDeviceTypes.OccupancySensor,
            MatterDeviceType.TemperatureSensor => StandardDeviceTypes.TemperatureSensor,
            MatterDeviceType.HumiditySensor => StandardDeviceTypes.HumiditySensor,
            MatterDeviceType.Thermostat => StandardDeviceTypes.Thermostat,
            _ => throw new NotSupportedException($"Matter device type '{deviceType}' is not supported by the bridge.")
        };

        /// <summary>
        /// Returns the action that populates a bridged endpoint with the application clusters mandated by
        /// <paramref name="deviceType"/>.
        /// </summary>
        /// <remarks>
        /// The action runs once per bridged endpoint, so every endpoint owns its own cluster instances.
        /// Identify is added everywhere: a controller uses it to make a device announce itself during and
        /// after commissioning.
        /// </remarks>
        public static Action<Endpoint> CreateComposer(MatterDeviceType deviceType) => deviceType switch
        {
            MatterDeviceType.OnOffLight or MatterDeviceType.OnOffPlugInUnit => ComposeOnOff,
            MatterDeviceType.DimmableLight or MatterDeviceType.DimmablePlugInUnit => ComposeDimmable,
            MatterDeviceType.ColorTemperatureLight or MatterDeviceType.ExtendedColorLight => ComposeColor,
            MatterDeviceType.ContactSensor => endpoint => Compose(endpoint, new BooleanStateCluster()),
            MatterDeviceType.LightSensor => endpoint => Compose(endpoint, new IlluminanceMeasurementCluster()),
            MatterDeviceType.OccupancySensor => endpoint => Compose(endpoint, new OccupancySensingCluster()),
            MatterDeviceType.TemperatureSensor => endpoint => Compose(endpoint, new TemperatureMeasurementCluster()),
            MatterDeviceType.HumiditySensor => endpoint => Compose(endpoint, new RelativeHumidityMeasurementCluster()),
            MatterDeviceType.Thermostat => ComposeThermostat,
            _ => throw new NotSupportedException($"Matter device type '{deviceType}' is not supported by the bridge.")
        };

        /// <summary>
        /// Binds <paramref name="attribute"/> to the cluster instance carrying it on
        /// <paramref name="device"/>'s endpoint.
        /// </summary>
        /// <returns><see langword="false"/> when the endpoint's device type does not carry that attribute.</returns>
        public static bool TryBind(BridgedDevice device, MatterAttribute attribute, out MatterAttributeAccessor accessor)
        {
            ArgumentNullException.ThrowIfNull(device);
            accessor = null;

            switch (attribute)
            {
                case MatterAttribute.OnOff:
                    if (!TryGet<OnOffCluster>(device, OnOffCluster.ClusterId, out var onOff)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => onOff.OnOff ? 1d : 0d,
                        Write = value => onOff.OnOff = value != 0d,
                        Subscribe = handler => onOff.OnOffChanged += handler,
                        Unsubscribe = handler => onOff.OnOffChanged -= handler
                    };
                    return true;

                case MatterAttribute.CurrentLevel:
                    if (!TryGet<LevelControlCluster>(device, LevelControlCluster.ClusterId, out var level)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => level.CurrentLevel,
                        Write = value => level.SetCurrentLevel(ToByte(value)),
                        Subscribe = handler => level.CurrentLevelChanged += handler,
                        Unsubscribe = handler => level.CurrentLevelChanged -= handler
                    };
                    return true;

                case MatterAttribute.CurrentHue:
                    if (!TryGet<ColorControlCluster>(device, ColorControlCluster.ClusterId, out var hueColor)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => hueColor.CurrentHue,
                        Write = value => hueColor.SetCurrentHue(ToByte(value)),
                        Subscribe = handler => hueColor.CurrentHueChanged += handler,
                        Unsubscribe = handler => hueColor.CurrentHueChanged -= handler
                    };
                    return true;

                case MatterAttribute.CurrentSaturation:
                    if (!TryGet<ColorControlCluster>(device, ColorControlCluster.ClusterId, out var satColor)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => satColor.CurrentSaturation,
                        Write = value => satColor.SetCurrentSaturation(ToByte(value)),
                        Subscribe = handler => satColor.CurrentSaturationChanged += handler,
                        Unsubscribe = handler => satColor.CurrentSaturationChanged -= handler
                    };
                    return true;

                case MatterAttribute.ColorTemperatureMireds:
                    if (!TryGet<ColorControlCluster>(device, ColorControlCluster.ClusterId, out var ctColor)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => ctColor.ColorTemperatureMireds,
                        // The cluster clamps into its own physical bounds, so only the cast needs guarding.
                        Write = value => ctColor.SetColorTemperatureMireds(ToUInt16(value)),
                        Subscribe = handler => ctColor.ColorTemperatureMiredsChanged += handler,
                        Unsubscribe = handler => ctColor.ColorTemperatureMiredsChanged -= handler
                    };
                    return true;

                case MatterAttribute.TemperatureMeasuredValue:
                    if (!TryGet<TemperatureMeasurementCluster>(device, TemperatureMeasurementCluster.ClusterId, out var temperature)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => temperature.MeasuredValue ?? 0d,
                        Write = value => temperature.MeasuredValue = ToInt16(value)
                    };
                    return true;

                case MatterAttribute.HumidityMeasuredValue:
                    if (!TryGet<RelativeHumidityMeasurementCluster>(device, RelativeHumidityMeasurementCluster.ClusterId, out var humidity)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => humidity.MeasuredValue ?? 0d,
                        Write = value => humidity.MeasuredValue = ToUInt16(value)
                    };
                    return true;

                case MatterAttribute.IlluminanceMeasuredValue:
                    if (!TryGet<IlluminanceMeasurementCluster>(device, IlluminanceMeasurementCluster.ClusterId, out var illuminance)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => illuminance.MeasuredValue ?? 0d,
                        Write = value => illuminance.MeasuredValue = ToUInt16(value)
                    };
                    return true;

                case MatterAttribute.Occupancy:
                    if (!TryGet<OccupancySensingCluster>(device, OccupancySensingCluster.ClusterId, out var occupancy)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => occupancy.Occupied ? 1d : 0d,
                        Write = value => occupancy.Occupied = value != 0d
                    };
                    return true;

                case MatterAttribute.BooleanStateValue:
                    if (!TryGet<BooleanStateCluster>(device, BooleanStateCluster.ClusterId, out var booleanState)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => booleanState.StateValue ? 1d : 0d,
                        // SetStateValue also emits the StateChange event a contact sensor is expected to raise.
                        Write = value => booleanState.SetStateValue(value != 0d)
                    };
                    return true;

                case MatterAttribute.LocalTemperature:
                    if (!TryGet<ThermostatCluster>(device, ThermostatCluster.ClusterId, out var localThermostat)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => localThermostat.LocalTemperature ?? 0d,
                        Write = value => localThermostat.SetLocalTemperature(ToInt16(value))
                    };
                    return true;

                case MatterAttribute.OccupiedHeatingSetpoint:
                    if (!TryGet<ThermostatCluster>(device, ThermostatCluster.ClusterId, out var heatThermostat)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => heatThermostat.OccupiedHeatingSetpoint,
                        Write = value => heatThermostat.SetOccupiedHeatingSetpoint(ToInt16(value)),
                        Subscribe = handler => heatThermostat.OccupiedHeatingSetpointChanged += handler,
                        Unsubscribe = handler => heatThermostat.OccupiedHeatingSetpointChanged -= handler
                    };
                    return true;

                case MatterAttribute.OccupiedCoolingSetpoint:
                    if (!TryGet<ThermostatCluster>(device, ThermostatCluster.ClusterId, out var coolThermostat)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => coolThermostat.OccupiedCoolingSetpoint,
                        Write = value => coolThermostat.SetOccupiedCoolingSetpoint(ToInt16(value)),
                        Subscribe = handler => coolThermostat.OccupiedCoolingSetpointChanged += handler,
                        Unsubscribe = handler => coolThermostat.OccupiedCoolingSetpointChanged -= handler
                    };
                    return true;

                case MatterAttribute.ThermostatSystemMode:
                    if (!TryGet<ThermostatCluster>(device, ThermostatCluster.ClusterId, out var modeThermostat)) return false;
                    accessor = new MatterAttributeAccessor
                    {
                        Read = () => (byte)modeThermostat.SystemMode,
                        Write = value => modeThermostat.SetSystemMode((ThermostatSystemMode)ToByte(value)),
                        Subscribe = handler => modeThermostat.SystemModeChanged += handler,
                        Unsubscribe = handler => modeThermostat.SystemModeChanged -= handler
                    };
                    return true;

                default:
                    return false;
            }
        }

        private static void ComposeOnOff(Endpoint endpoint) => Compose(endpoint, new OnOffCluster());

        private static void ComposeDimmable(Endpoint endpoint)
        {
            var onOff = new OnOffCluster();
            var level = CreateCoupledLevel(onOff);
            Compose(endpoint, onOff, level);
        }

        private static void ComposeColor(Endpoint endpoint)
        {
            var onOff = new OnOffCluster();
            var level = CreateCoupledLevel(onOff);
            var color = new ColorControlCluster(coupling: new OnOffCouplingAdapter(onOff));
            Compose(endpoint, onOff, level, color);
        }

        private static void ComposeThermostat(Endpoint endpoint) =>
            // Heating and cooling are both advertised: the descriptor cannot say which the device supports,
            // and a heating-only device simply never has its cooling setpoint bound.
            Compose(endpoint, new ThermostatCluster(ThermostatFeature.Heating | ThermostatFeature.Cooling));

        // Level Control reads On/Off through the coupling to honour the ExecuteIfOff option, and On/Off
        // pushes its changes back so OnLevel and the level-driven on/off transitions stay in step.
        private static LevelControlCluster CreateCoupledLevel(OnOffCluster onOff)
        {
            var level = new LevelControlCluster(coupling: new OnOffCouplingAdapter(onOff));
            onOff.OnOffChanged += (_, _) => level.NotifyOnOffChanged();
            return level;
        }

        private static void Compose(Endpoint endpoint, params Cluster[] clusters)
        {
            endpoint.AddCluster(new IdentifyCluster());
            foreach (var cluster in clusters)
            {
                endpoint.AddCluster(cluster);
            }
        }

        private static bool TryGet<TCluster>(BridgedDevice device, ClusterId clusterId, out TCluster cluster)
            where TCluster : Cluster
        {
            cluster = device.Endpoint.TryGetCluster(clusterId, out var found) ? found as TCluster : null;
            return cluster is not null;
        }

        private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), byte.MinValue, byte.MaxValue);

        private static ushort ToUInt16(double value) => (ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue);

        private static short ToInt16(double value) => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
    }
}
