using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RIoT2.Net.Orchestrator.Services.DeviceSignIn
{
    /// <summary>
    /// The small page the device sign-in popup ends on (Core 2.4.0): it shows the result and, when the sign-in's
    /// state was known, posts it to the window that opened it (only to that window's stored origin) and closes.
    /// </summary>
    public static class DeviceAuthorizationPage
    {
        /// <summary>The <c>type</c> of the message posted to the opener.</summary>
        public const string MessageType = "riot2-device-authorization";

        private static readonly JsonSerializerOptions ScriptJson = new() { Encoder = JavaScriptEncoder.Default };

        /// <summary>The page's HTML and the nonce its one script runs with.</summary>
        public static (string Html, string Nonce) Render(DeviceAuthorizationCallbackOutcome outcome)
        {
            ArgumentNullException.ThrowIfNull(outcome);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var title = outcome.Success ? "Connected" : "Sign-in failed";

            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
                .Append("<title>RIoT2 device sign-in</title>")
                .Append("<style>body{font-family:system-ui,sans-serif;margin:2rem;line-height:1.4}</style></head><body>")
                .Append("<h1>").Append(HtmlEncoder.Default.Encode(title)).Append("</h1>")
                .Append("<p>").Append(HtmlEncoder.Default.Encode(outcome.Message)).Append("</p>");

            if (outcome.Pending != null)
            {
                var message = ScriptValue(new
                {
                    type = MessageType,
                    nodeId = outcome.Pending.NodeId,
                    deviceId = outcome.Pending.DeviceId,
                    success = outcome.Success,
                    message = outcome.Message
                });
                html.Append("<script nonce=\"").Append(nonce).Append("\">")
                    .Append("window.opener && window.opener.postMessage(").Append(message).Append(", ")
                    .Append(ScriptValue(outcome.Pending.Origin)).Append("); window.close();</script>");
            }

            html.Append("</body></html>");
            return (html.ToString(), nonce);
        }

        /// <summary>The response headers: never cached, no referrer, and only the page's own script.</summary>
        public static void ApplyHeaders(IHeaderDictionary headers, string nonce)
        {
            ArgumentNullException.ThrowIfNull(headers);
            headers.CacheControl = "no-store";
            headers.Pragma = "no-cache";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'";
        }

        // JavaScriptEncoder.Default already escapes <, > and & (and every non-ASCII character); "<" is replaced again so
        // a "</script>" can never close the script, whatever the encoder.
        private static string ScriptValue<T>(T value) => JsonSerializer.Serialize(value, ScriptJson).Replace("<", "\\u003C", StringComparison.Ordinal);
    }
}
