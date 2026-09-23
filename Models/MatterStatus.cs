namespace RIoT2.Net.Orchestrator.Models
{
    /// <summary>
    /// The Matter bridge's state as presented to the UI: whether it runs, the onboarding codes a user
    /// scans in Google Home, the fabrics it has been commissioned into, and the endpoints it exposes.
    /// </summary>
    public class MatterStatus
    {
        /// <summary>Whether the bridge is configured to start with the orchestrator.</summary>
        public bool Enabled { get; set; }

        /// <summary>Whether the bridge is currently running.</summary>
        public bool Running { get; set; }

        /// <summary>The reason the bridge is not running, when it failed to start.</summary>
        public string Error { get; set; }

        /// <summary>The name a commissioner shows for the bridge.</summary>
        public string NodeLabel { get; set; }

        public ushort VendorId { get; set; }

        public ushort ProductId { get; set; }

        public ushort Discriminator { get; set; }

        /// <summary>The <c>MT:</c> onboarding payload; render this as the scannable QR code.</summary>
        public string QrCode { get; set; }

        /// <summary>The 11-digit manual pairing code, without separators.</summary>
        public string ManualCode { get; set; }

        /// <summary>The manual pairing code grouped as XXXX-XXX-XXXX for display.</summary>
        public string FormattedManualCode { get; set; }

        /// <summary>The commissioning window state: <c>WindowNotOpen</c>, <c>BasicWindowOpen</c> or <c>EnhancedWindowOpen</c>.</summary>
        public string CommissioningWindow { get; set; }

        /// <summary>The fabrics the bridge has been commissioned into; one per controller ecosystem.</summary>
        public List<MatterFabricStatus> Fabrics { get; set; } = new();

        /// <summary>The bridged endpoints currently exposed through the aggregator.</summary>
        public List<MatterEndpointStatus> Endpoints { get; set; } = new();
    }

    /// <summary>One fabric the bridge has been commissioned into.</summary>
    public class MatterFabricStatus
    {
        public byte FabricIndex { get; set; }

        /// <summary>The label the commissioning administrator assigned.</summary>
        public string Label { get; set; }

        /// <summary>The vendor id of the administrator that commissioned the fabric (Google is 0x6006).</summary>
        public ushort VendorId { get; set; }

        /// <summary>The bridge's own operational node id on the fabric.</summary>
        public string NodeId { get; set; }
    }

    /// <summary>One bridged endpoint, as declared by a RIoT device and exposed to a controller.</summary>
    public class MatterEndpointStatus
    {
        /// <summary>The Matter endpoint id the controller addresses; pinned across restarts.</summary>
        public ushort EndpointId { get; set; }

        /// <summary>The declared endpoint id from the device's <c>MatterEndpointTemplate</c>.</summary>
        public string TemplateId { get; set; }

        public string Name { get; set; }

        /// <summary>The Matter device type name, e.g. <c>ExtendedColorLight</c>.</summary>
        public string DeviceType { get; set; }

        /// <summary>The id of the node hosting the RIoT device.</summary>
        public string NodeId { get; set; }

        /// <summary>The id of the RIoT device configuration the endpoint was declared by.</summary>
        public string DeviceId { get; set; }

        /// <summary>Whether the owning node is online; a controller shows an unreachable device as offline.</summary>
        public bool Reachable { get; set; }
    }
}
