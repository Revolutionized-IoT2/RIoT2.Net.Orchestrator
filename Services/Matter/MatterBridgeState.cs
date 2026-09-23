namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// The half of the bridge's identity that must survive a restart but is never edited by hand: the
    /// SPAKE2+ provisioning bundle behind the onboarding codes, and the map that pins each declared
    /// Matter endpoint to a Matter endpoint id.
    /// </summary>
    /// <remarks>
    /// The passcode, PBKDF iteration count and salt are stored rather than the derived verifier, because
    /// <c>PaseVerifierGenerator.GenerateVerifier</c> reproduces the verifier deterministically from them.
    /// Re-provisioning instead of restoring would mint a new passcode and invalidate a printed QR code.
    /// The endpoint map keeps a controller's device references stable: a bridged endpoint that comes back
    /// on a different endpoint id looks to Google Home like the old device vanishing and a new one
    /// appearing.
    /// </remarks>
    public class MatterBridgeState
    {
        /// <summary>The setup passcode encoded into the QR and manual pairing codes.</summary>
        public uint Passcode { get; set; }

        /// <summary>The PBKDF iteration count of the SPAKE2+ verifier.</summary>
        public uint PbkdfIterations { get; set; }

        /// <summary>The PBKDF salt of the SPAKE2+ verifier, base64-encoded.</summary>
        public string PbkdfSalt { get; set; }

        /// <summary>
        /// <see cref="RIoT2.Core.Models.Matter.MatterEndpointTemplate.Id"/> to Matter endpoint id. A
        /// known id is offered to the aggregator as the preferred id; an unknown endpoint is allocated
        /// sequentially and its assigned id recorded here.
        /// </summary>
        public Dictionary<string, ushort> EndpointMap { get; set; } = new();
    }
}
