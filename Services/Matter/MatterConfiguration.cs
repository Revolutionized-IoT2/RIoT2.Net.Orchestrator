namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// The operator-editable settings of the RIoT Control Bridge: whether the bridge runs at all, the
    /// identity it presents to a commissioner, and where its attestation material comes from.
    /// </summary>
    /// <remarks>
    /// Persisted through <see cref="Persistence.IObjectStore"/> by <see cref="MatterConfigurationStore"/>.
    /// The volatile half of the bridge's identity (setup passcode, PBKDF salt and the endpoint map) lives
    /// in <see cref="MatterBridgeState"/> so a configuration POST from the UI can never clobber it.
    /// </remarks>
    public class MatterConfiguration
    {
        /// <summary>The CSA test vendor id. A controller such as Google Home only accepts an uncertified device whose VID/PID is registered with it.</summary>
        public const ushort TestVendorId = 0xFFF1;

        /// <summary>Whether the bridge is started with the orchestrator.</summary>
        public bool Enabled { get; set; }

        /// <summary>The name a commissioner shows for the bridge itself (not for the bridged devices).</summary>
        public string NodeLabel { get; set; } = "RIoT2 Control Bridge";

        /// <summary>The Matter vendor id advertised in Basic Information and encoded into the onboarding codes.</summary>
        public ushort VendorId { get; set; } = TestVendorId;

        /// <summary>The Matter product id advertised in Basic Information and encoded into the onboarding codes.</summary>
        public ushort ProductId { get; set; } = 0x8000;

        /// <summary>The 12-bit setup discriminator advertised over DNS-SD and encoded into the onboarding codes.</summary>
        public ushort Discriminator { get; set; } = 0x0F00;

        /// <summary>
        /// A directory holding operator-supplied attestation material (<c>dac.der</c>, <c>pai.der</c>,
        /// <c>cd.der</c> and <c>dac-key.pem</c>). When empty, TEST credentials are generated for the
        /// configured VID/PID and cached under <see cref="CredentialsDirectory"/>.
        /// </summary>
        public string AttestationPath { get; set; }

        /// <summary>The directory generated TEST credentials and the persisted fabric table are written to, relative to the content root when not rooted.</summary>
        public string CredentialsDirectory { get; set; } = "MatterCredentials";

        /// <summary>
        /// The passphrase sealing the persisted fabric table. Change it and every commissioned fabric is
        /// lost, so it is generated once and then left alone unless the operator overrides it.
        /// </summary>
        public string FabricStoreKey { get; set; }

        /// <summary>Returns a copy so a caller cannot mutate the service's live instance.</summary>
        public MatterConfiguration Copy() => new()
        {
            Enabled = Enabled,
            NodeLabel = NodeLabel,
            VendorId = VendorId,
            ProductId = ProductId,
            Discriminator = Discriminator,
            AttestationPath = AttestationPath,
            CredentialsDirectory = CredentialsDirectory,
            FabricStoreKey = FabricStoreKey
        };
    }
}
