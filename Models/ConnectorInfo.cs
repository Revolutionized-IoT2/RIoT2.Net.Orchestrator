using System.Text.Json;
using RIoT2.Core.Models;

namespace RIoT2.Net.Orchestrator.Models
{
    /// <summary>
    /// An online connector as <c>GET /api/v2/connectors</c> lists it (design 7.3 phase 3): its presence, and its own
    /// <c>/health</c> answer when it announced a URL.
    /// </summary>
    public class ConnectorInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>The connector's <c>RIOT2_CONNECTOR_URL</c>; <c>null</c> when it announced none.</summary>
        public string? NodeBaseUrl { get; set; }

        /// <summary>The connector's name and version, and the connector SDK's version as <c>coreVersion</c>.</summary>
        public PackageManifest? Manifest { get; set; }

        /// <summary>
        /// The connector's <c>/health</c> body as it answered (<c>status</c>, <c>mqtt</c>, <c>sink</c>, <c>queue</c>,
        /// <c>spool</c>); <c>null</c> without a URL, or when it didn't answer with JSON.
        /// </summary>
        public JsonElement? Health { get; set; }

        /// <summary>Why <see cref="Health"/> is missing although the connector has a URL; otherwise <c>null</c>.</summary>
        public string? HealthError { get; set; }
    }
}
