using System.Security.Claims;
using RIoT2.Core.Security;

namespace RIoT2.Net.Orchestrator.Services.Mcp
{
    /// <summary>Where an MCP call arrived (ADR 0018).</summary>
    public static class McpListeners
    {
        /// <summary><c>/mcp</c> on the API port, as trusted as the REST API.</summary>
        public const string Local = "local";

        /// <summary>The remote listener's port, with a validated token.</summary>
        public const string Remote = "remote";
    }

    /// <summary>The claims the remote listener adds to a validated token.</summary>
    public static class McpClaims
    {
        /// <summary>Marks the identity as the remote listener's; its value is <see cref="McpListeners.Remote"/>.</summary>
        public const string Listener = "riot2:mcp-listener";
    }

    /// <summary>Who calls an MCP tool: the listener, a user name for the activity log, and the highest RIoT2 role.</summary>
    public sealed record McpCaller(string Listener, string User, string Role)
    {
        /// <summary>
        /// The caller behind <paramref name="user"/>. The local listener's user is the security mode's (<c>local-admin</c> while it is
        /// off); the remote listener's carries <see cref="McpClaims.Listener"/> and one role, already capped. Without a role, the
        /// caller can't use any tool.
        /// </summary>
        public static McpCaller From(ClaimsPrincipal? user)
        {
            var listener = user?.FindFirst(McpClaims.Listener)?.Value == McpListeners.Remote ? McpListeners.Remote : McpListeners.Local;
            var name = user?.FindFirst("email")?.Value ?? user?.FindFirst(ClaimTypes.Email)?.Value ?? user?.Identity?.Name
                ?? user?.FindFirst("sub")?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
            var role = Roles.Highest(user?.FindAll(ClaimTypes.Role).Select(x => x.Value) ?? []);
            return new McpCaller(listener, name, role ?? "");
        }

        public bool Has(string role) => Roles.Rank(Role) >= Roles.Rank(role) && Roles.Rank(role) > 0;
    }

    /// <summary>The order of the <see cref="SecurityPolicies"/> roles.</summary>
    public static class Roles
    {
        public static int Rank(string? role) => role switch
        {
            SecurityPolicies.Viewer => 1,
            SecurityPolicies.Operator => 2,
            SecurityPolicies.Admin => 3,
            _ => 0
        };

        public static string? Highest(IEnumerable<string> roles) => roles.Where(x => Rank(x) > 0).OrderByDescending(Rank).FirstOrDefault();

        public static string Lowest(string a, string b) => Rank(a) <= Rank(b) ? a : b;
    }

    /// <summary>One MCP tool call, as the activity log and the UI show it.</summary>
    public sealed record McpActivityEntry(DateTimeOffset Time, string Listener, string User, string Tool, string? Target, string Outcome,
        long DurationMs, string? Error);

    /// <summary>The <see cref="McpActivityEntry.Outcome"/> values.</summary>
    public static class McpOutcomes
    {
        public const string Ok = "ok";

        /// <summary>The tool answered with an error (an unknown id, a command that isn't allowed, a failed delivery).</summary>
        public const string Error = "error";

        /// <summary>The listener, the tool's group or the caller's role doesn't allow the tool.</summary>
        public const string Denied = "denied";

        /// <summary>The writes per minute were used up.</summary>
        public const string Limited = "limited";

        /// <summary>The tool threw.</summary>
        public const string Failed = "failed";
    }

    /// <summary>The latest MCP tool calls, in memory since the start; the oldest are dropped.</summary>
    public sealed class McpActivity
    {
        public const int Capacity = 200;

        private readonly Queue<McpActivityEntry> _entries = new();
        private readonly Lock _gate = new();

        public void Add(McpActivityEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            lock (_gate)
            {
                _entries.Enqueue(entry);
                while (_entries.Count > Capacity)
                    _entries.Dequeue();
            }
        }

        /// <summary>The latest <paramref name="max"/> calls, newest first.</summary>
        public IReadOnlyList<McpActivityEntry> Recent(int max = Capacity)
        {
            lock (_gate)
                return _entries.Reverse().Take(Math.Clamp(max, 0, Capacity)).ToList();
        }
    }

    /// <summary>
    /// The writes per minute of <see cref="McpConfiguration.WritesPerMinute"/>, across every MCP client: a sliding window of
    /// the last minute's writes.
    /// </summary>
    public sealed class McpWriteLimiter(TimeProvider time)
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
        private readonly Queue<DateTimeOffset> _writes = new();
        private readonly Lock _gate = new();

        /// <summary>Counts a write and returns <c>true</c>, or returns <c>false</c> when <paramref name="perMinute"/> are used up.</summary>
        public bool TryAcquire(int perMinute)
        {
            var now = time.GetUtcNow();
            lock (_gate)
            {
                while (_writes.Count > 0 && now - _writes.Peek() >= Window)
                    _writes.Dequeue();
                if (_writes.Count >= perMinute)
                    return false;
                _writes.Enqueue(now);
                return true;
            }
        }
    }
}
