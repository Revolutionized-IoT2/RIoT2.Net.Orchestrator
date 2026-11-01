using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// The commissioning provisioning bundle behind the bridge's onboarding codes.
    /// </summary>
    internal static class MatterProvisioning
    {
        /// <summary>
        /// Restores the provisioning bundle from <paramref name="state"/>, or mints a new one and writes
        /// it into <paramref name="state"/>. Returns whether the state changed and must be saved.
        /// </summary>
        /// <remarks>
        /// The verifier is re-derived rather than stored: it is a deterministic function of the passcode
        /// and the PBKDF parameters. Re-provisioning instead of restoring would change the passcode and
        /// invalidate any QR code the user has already been shown.
        /// </remarks>
        public static bool Resolve(MatterBridgeState state, ILogger logger, out PaseProvisioning provisioning)
        {
            ArgumentNullException.ThrowIfNull(state);

            if (state.Passcode != 0 && state.PbkdfIterations != 0 && !string.IsNullOrEmpty(state.PbkdfSalt))
            {
                try
                {
                    var restored = new SetupPasscode(state.Passcode);
                    var parameters = new PbkdfParameters(state.PbkdfIterations, Convert.FromBase64String(state.PbkdfSalt));
                    provisioning = new PaseProvisioning(restored, parameters, PaseVerifierGenerator.GenerateVerifier(restored, parameters));
                    return false;
                }
                catch (Exception x)
                {
                    logger?.LogError(x, "The stored Matter provisioning bundle is unusable; provisioning a new one");
                }
            }

            provisioning = PaseVerifierGenerator.Provision();
            state.Passcode = provisioning.Passcode.Value;
            state.PbkdfIterations = provisioning.Parameters.Iterations;
            state.PbkdfSalt = Convert.ToBase64String(provisioning.Parameters.Salt);
            return true;
        }
    }
}
