namespace RIoT2.Net.Orchestrator.Models
{
    /// <summary>
    /// The body of <c>POST /api/Nodes/{nodeId}/devices/{deviceId}/authorization</c> (Core 2.4.0): where the provider
    /// sends the browser back (<c>&lt;apiBase&gt;/api/oauth/callback</c>), and the UI window's origin, which gets the result.
    /// </summary>
    public class DeviceAuthorizationRequest
    {
        public string? RedirectUri { get; set; }
        public string? Origin { get; set; }
    }
}
