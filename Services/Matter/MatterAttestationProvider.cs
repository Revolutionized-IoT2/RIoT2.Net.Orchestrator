using System.Security.Cryptography;
using System.Text;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.Credentials;

namespace RIoT2.Net.Orchestrator.Services.Matter
{
    /// <summary>
    /// Supplies the bridge's Device Attestation material. An operator can point
    /// <see cref="MatterConfiguration.AttestationPath"/> at a real DAC/PAI/CD chain; otherwise a TEST
    /// chain is minted for the configured VID/PID and cached under
    /// <see cref="MatterConfiguration.CredentialsDirectory"/>.
    /// </summary>
    /// <remarks>
    /// TEST material is signed by the CHIP test PAA, which a production controller rejects unless the
    /// VID/PID pair has been registered with it (for Google Home, as an integration in the Google Home
    /// Developer Console).
    /// </remarks>
    public static class MatterAttestationProvider
    {
        private const string DacFile = "dac.der";
        private const string PaiFile = "pai.der";
        private const string CdFile = "cd.der";
        private static readonly string[] KeyFiles = ["dac-key.pem", "dac-key.pkcs8", "dac-key.der"];

        /// <summary>
        /// Resolves the attestation credentials for <paramref name="configuration"/>.
        /// </summary>
        /// <param name="configuration">The bridge configuration naming the VID/PID and the credential directories.</param>
        /// <param name="credentialsDirectory">The resolved absolute directory generated TEST material is cached in.</param>
        /// <param name="logger">Receives the credential source and the generator's diagnostics.</param>
        /// <exception cref="FileNotFoundException">An override directory was configured but is missing a required file.</exception>
        public static DeviceAttestationCredentials Create(
            MatterConfiguration configuration, string credentialsDirectory, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrEmpty(credentialsDirectory);

            if (!string.IsNullOrWhiteSpace(configuration.AttestationPath))
            {
                logger?.LogInformation("Loading Matter attestation credentials from {Path}", configuration.AttestationPath);
                return LoadFrom(configuration.AttestationPath);
            }

            logger?.LogWarning(
                "No attestation path configured; using TEST attestation for VID 0x{VendorId:X4} / PID 0x{ProductId:X4} cached in {Directory}. " +
                "A controller accepts this only when the VID/PID pair is registered with it.",
                configuration.VendorId, configuration.ProductId, credentialsDirectory);

            return TestAttestationFactory.Create(
                configuration.VendorId,
                configuration.ProductId,
                // The bridge itself is a Control Bridge (0x0840); the CD names the device type of the node.
                deviceTypeId: 0x0840,
                outputDirectory: credentialsDirectory,
                diagnostics: message => logger?.LogInformation("Matter attestation: {Message}", message));
        }

        private static DeviceAttestationCredentials LoadFrom(string directory)
        {
            var keyFile = KeyFiles
                .Select(name => Path.Combine(directory, name))
                .FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException(
                    $"No DAC private key ({string.Join(", ", KeyFiles)}) found in attestation directory '{directory}'.");

            return new DeviceAttestationCredentials
            {
                DeviceAttestationCertificate = ReadRequired(directory, DacFile),
                ProductAttestationIntermediateCertificate = ReadRequired(directory, PaiFile),
                CertificationDeclaration = ReadRequired(directory, CdFile),
                DeviceAttestationKey = new EcdsaOperationalKey(LoadPrivateKey(keyFile))
            };
        }

        private static byte[] ReadRequired(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Attestation file '{fileName}' was not found in '{directory}'.", path);

            return ToDer(File.ReadAllBytes(path));
        }

        // Certificates may be supplied as DER or PEM; the stack wants DER.
        private static byte[] ToDer(byte[] raw)
        {
            if (!IsPem(raw))
                return raw;

            var text = Encoding.ASCII.GetString(raw);
            var start = text.IndexOf("-----BEGIN", StringComparison.Ordinal);
            var bodyStart = text.IndexOf('\n', start) + 1;
            var end = text.IndexOf("-----END", StringComparison.Ordinal);
            return Convert.FromBase64String(text[bodyStart..end].Replace("\r", "").Replace("\n", ""));
        }

        private static ECDsa LoadPrivateKey(string path)
        {
            var raw = File.ReadAllBytes(path);
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            if (IsPem(raw))
            {
                key.ImportFromPem(Encoding.ASCII.GetString(raw)); // PKCS#8 "PRIVATE KEY" or SEC1 "EC PRIVATE KEY"
                return key;
            }

            try
            {
                key.ImportPkcs8PrivateKey(raw, out _);
            }
            catch (CryptographicException)
            {
                key.ImportECPrivateKey(raw, out _); // SEC1 DER
            }
            return key;
        }

        private static bool IsPem(byte[] raw) =>
            raw.Length > 10 && Encoding.ASCII.GetString(raw, 0, 10) == "-----BEGIN";
    }
}
