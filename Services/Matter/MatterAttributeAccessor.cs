namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// A uniform read/write/subscribe seam over one Matter cluster attribute, produced by
    /// <see cref="MatterEndpointComposer.TryBind"/>.
    /// </summary>
    /// <remarks>
    /// Values are always <see cref="double"/>, with booleans carried as 0 or 1, so a single conversion
    /// path serves every attribute. <see cref="Subscribe"/> is null for a device-driven attribute (the
    /// sensor clusters have no change events), which is also how the adapter tells apart the attributes a
    /// controller can drive from those it can only observe.
    /// </remarks>
    public sealed class MatterAttributeAccessor
    {
        /// <summary>Reads the attribute's current value.</summary>
        public required Func<double> Read { get; init; }

        /// <summary>Writes the attribute, clamping into the attribute's own numeric range.</summary>
        public required Action<double> Write { get; init; }

        /// <summary>Adds a handler to the cluster's change event, or null when the attribute raises none.</summary>
        public Action<EventHandler> Subscribe { get; init; }

        /// <summary>Removes a handler added by <see cref="Subscribe"/>.</summary>
        public Action<EventHandler> Unsubscribe { get; init; }

        /// <summary>Whether the attribute raises a change event a controller's writes can be observed through.</summary>
        public bool CanSubscribe => Subscribe is not null;
    }
}
