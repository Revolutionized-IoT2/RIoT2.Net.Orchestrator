using System.Buffers.Text;
using System.Security.Cryptography;

namespace RIoT2.Net.Orchestrator.Services.DeviceSignIn
{
    /// <summary>
    /// A device sign-in waiting for the provider's redirect (Core 2.4.0): what the callback needs, keyed by its
    /// <c>state</c>. The state is the only credential of the anonymous callback.
    /// </summary>
    public sealed record PendingDeviceAuthorization(string NodeId, string DeviceId, string RedirectUri, string Origin,
        string? User, DateTimeOffset ExpiresAt);

    /// <summary>
    /// The pending device sign-ins, in memory: each state is 32 random bytes (base64url), lives
    /// <see cref="Lifetime"/> and is taken once. At most <see cref="Capacity"/> wait; a restart forgets them.
    /// </summary>
    public sealed class DeviceAuthorizationStateStore
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
        public const int Capacity = 100;

        private readonly Dictionary<string, PendingDeviceAuthorization> _pending = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();
        private readonly TimeProvider _time;

        public DeviceAuthorizationStateStore(TimeProvider time) => _time = time;

        /// <summary>The number of states waiting, expired ones included until the next purge.</summary>
        public int Count
        {
            get { lock (_lock) return _pending.Count; }
        }

        /// <summary>
        /// Stores a new sign-in and returns its state, or <c>null</c> when <see cref="Capacity"/> unexpired ones wait.
        /// </summary>
        public string? Create(string nodeId, string deviceId, string redirectUri, string origin, string? user)
        {
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                Purge(now);
                if (_pending.Count >= Capacity)
                    return null;

                string state;
                do
                    state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
                while (_pending.ContainsKey(state));

                _pending[state] = new PendingDeviceAuthorization(nodeId, deviceId, redirectUri, origin, user, now + Lifetime);
                return state;
            }
        }

        /// <summary>Takes the sign-in of a state: once, and only before it expires.</summary>
        public PendingDeviceAuthorization? TryConsume(string? state)
        {
            if (string.IsNullOrEmpty(state))
                return null;

            var now = _time.GetUtcNow();
            lock (_lock)
            {
                if (!_pending.Remove(state, out var pending))
                    return null;
                return pending.ExpiresAt > now ? pending : null;
            }
        }

        /// <summary>Forgets a state whose start failed, so it doesn't hold a place.</summary>
        public void Discard(string state)
        {
            lock (_lock)
                _pending.Remove(state);
        }

        private void Purge(DateTimeOffset now)
        {
            foreach (var expired in _pending.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList())
                _pending.Remove(expired);
        }
    }
}
