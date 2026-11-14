using System.Text.Json.Nodes;
using RIoT2.Core;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Services.Persistence;
using CoreJson = RIoT2.Core.Utils.Json;

namespace RIoT2.Net.Orchestrator.Services.Dashboard
{
    /// <summary>Who saved a <see cref="DashboardVersion"/>.</summary>
    public static class DashboardSources
    {
        /// <summary><c>POST /api/dashboard/configuration</c>: the UI's editor, or any other REST client.</summary>
        public const string Ui = "ui";

        /// <summary>An MCP dashboard tool.</summary>
        public const string Mcp = "mcp";

        /// <summary>An earlier version, restored.</summary>
        public const string Restore = "restore";

        /// <summary>A configuration backup, restored.</summary>
        public const string Backup = "backup";

        /// <summary>The dashboard as it was before the first recorded save, so the first change can be undone.</summary>
        public const string Baseline = "baseline";
    }

    /// <summary>
    /// A saved dashboard (ADR 0018, I60): the definition as stored (templates as ids, no reports), when, by whom and why.
    /// The newest <see cref="DashboardVersionService.MaxVersions"/> are kept as <c>StoredObjects/DashboardVersion/</c>.
    /// </summary>
    public sealed class DashboardVersion : IStoredObject
    {
        public string Id { get; set; } = "";

        /// <summary>Counts up with every version; the newest has the highest.</summary>
        public long Sequence { get; set; }

        public DateTimeOffset SavedAt { get; set; }
        public string Source { get; set; } = DashboardSources.Ui;

        /// <summary>The MCP caller; empty for the UI.</summary>
        public string Caller { get; set; } = "";

        public string Summary { get; set; } = "";
        public JsonObject Dashboard { get; set; } = [];
    }

    /// <summary>A version without its dashboard, as the lists show it.</summary>
    public sealed record DashboardVersionInfo(string Id, DateTimeOffset SavedAt, string Source, string Caller, string Summary, int Pages, int Cards)
    {
        public static DashboardVersionInfo From(DashboardVersion version)
        {
            var pages = version.Dashboard["pages"] as JsonArray ?? [];
            var cards = pages.Sum(page => (page?["components"] as JsonArray)?.Count ?? 0);
            return new(version.Id, version.SavedAt, version.Source, version.Caller, version.Summary, pages.Count, cards);
        }
    }

    /// <summary>
    /// Says who saves the dashboard and why, for the version the save records: set around a save with <see cref="Use"/>. A save
    /// without one is the UI's.
    /// </summary>
    public sealed record DashboardSaveContext(string Source, string Caller, string Summary)
    {
        private static readonly AsyncLocal<DashboardSaveContext?> _current = new();

        public static DashboardSaveContext? Current => _current.Value;

        public static IDisposable Use(string source, string? caller = null, string? summary = null)
        {
            var previous = _current.Value;
            _current.Value = new DashboardSaveContext(source, caller ?? "", summary ?? "");
            return new Scope(previous);
        }

        private sealed class Scope(DashboardSaveContext? previous) : IDisposable
        {
            public void Dispose() => _current.Value = previous;
        }
    }

    /// <summary>
    /// Records a <see cref="DashboardVersion"/> for every dashboard save that changes it, whatever saved it, keeps the newest
    /// <see cref="MaxVersions"/>, and restores one. Construct it at startup, before the first save.
    /// </summary>
    public sealed class DashboardVersionService : IDisposable
    {
        public const int MaxVersions = 10;

        private readonly IStoredObjectStore _store;
        private readonly IOrchestratorConfigurationService _configuration;
        private readonly TimeProvider _time;
        private readonly ILogger<DashboardVersionService> _logger;
        private readonly Lock _gate = new();

        public DashboardVersionService(IStoredObjectStore store, IOrchestratorConfigurationService configuration, TimeProvider time,
            ILogger<DashboardVersionService> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _store.Changed += OnStoredObject;
            if (_store.GetAll<DashboardVersion>().Count == 0 && _store.GetAll<DashboardConfiguration>().FirstOrDefault() is { } dashboard)
                Record(dashboard, new DashboardSaveContext(DashboardSources.Baseline, "", "The dashboard before the first recorded change"));
        }

        /// <summary>The versions, newest first.</summary>
        public IReadOnlyList<DashboardVersion> List() => [.. _store.GetAll<DashboardVersion>().OrderByDescending(x => x.Sequence)];

        public DashboardVersion? Latest => _store.GetAll<DashboardVersion>().MaxBy(x => x.Sequence);

        public DashboardVersion? Get(string id) => string.IsNullOrWhiteSpace(id) ? null : _store.Get<DashboardVersion>(id);

        /// <summary>
        /// Saves version <paramref name="id"/>'s dashboard as the dashboard, which records a new version (history is never
        /// rewritten). <c>null</c> when there is no such version; the latest version when the dashboard already is that one.
        /// </summary>
        /// <exception cref="InvalidOperationException">The dashboard could not be saved.</exception>
        public DashboardVersion? Restore(string id, string? caller = null)
        {
            var version = Get(id);
            if (version == null)
                return null;
            using (DashboardSaveContext.Use(DashboardSources.Restore, caller, $"Restored the version of {version.SavedAt:yyyy-MM-dd HH:mm:ss} UTC"))
            {
                if (string.IsNullOrEmpty(_configuration.SaveDashboardConfiguration(version.Dashboard.ToJsonString())))
                    throw new InvalidOperationException("The dashboard could not be saved.");
            }
            return Latest;
        }

        /// <summary>The dashboard as a version keeps it: templates as their ids, without reports.</summary>
        public static JsonObject Definition(DashboardConfiguration dashboard)
        {
            var json = JsonNode.Parse(CoreJson.Serialize(dashboard)) as JsonObject ?? [];
            foreach (var element in Elements(json))
            {
                element.Remove("previousReports");
                foreach (var key in new[] { "reportTemplate", "commandTemplate" })
                    if (element[key] is JsonObject template)
                        element[key] = new JsonObject { ["id"] = template["id"]?.DeepClone() };
            }
            return json;
        }

        internal static IEnumerable<JsonObject> Elements(JsonObject dashboard) =>
            (dashboard["pages"] as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(page => (page["components"] as JsonArray ?? []).OfType<JsonObject>())
                .SelectMany(card => (card["elements"] as JsonArray ?? []).OfType<JsonObject>());

        private void OnStoredObject(StoredObjectChange change)
        {
            if (!change.Is<DashboardConfiguration>() || change.Operation is not (OperationType.Created or OperationType.Updated)
                || change.Object is not DashboardConfiguration dashboard)
                return;
            try
            {
                Record(dashboard, DashboardSaveContext.Current ?? new DashboardSaveContext(DashboardSources.Ui, "", "Saved in the dashboard editor"));
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not record a dashboard version");
            }
        }

        private void Record(DashboardConfiguration dashboard, DashboardSaveContext context)
        {
            lock (_gate)
            {
                var versions = _store.GetAll<DashboardVersion>();
                var version = new DashboardVersion
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Sequence = versions.Count == 0 ? 1 : versions.Max(x => x.Sequence) + 1,
                    SavedAt = _time.GetUtcNow(),
                    Source = context.Source,
                    Caller = context.Caller,
                    Summary = context.Summary,
                    Dashboard = Definition(dashboard)
                };
                if (_store.Save(version) == null)
                {
                    _logger.LogError("Could not store the dashboard version from {Source}", context.Source);
                    return;
                }
                foreach (var old in _store.GetAll<DashboardVersion>().OrderByDescending(x => x.Sequence).Skip(MaxVersions).ToList())
                    _store.Delete<DashboardVersion>(old.Id);
                _logger.LogInformation("Dashboard version {Version} saved by {Source} {Caller}: {Summary}", version.Sequence, context.Source,
                    context.Caller, context.Summary);
            }
        }

        public void Dispose() => _store.Changed -= OnStoredObject;
    }

    /// <summary>The MQTT notice the orchestrator publishes after every dashboard change (not retained), so open UIs reload.</summary>
    public static class DashboardNotice
    {
        public const string Topic = "riot2/orchestrator/dashboard";

        public static string Payload(DashboardVersion version) => new JsonObject
        {
            ["versionId"] = version.Id,
            ["savedAt"] = version.SavedAt.ToString("O"),
            ["source"] = version.Source
        }.ToJsonString();
    }
}
