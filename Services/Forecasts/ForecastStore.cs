using Microsoft.Data.Sqlite;
using RIoT2.Core.Models;
using RIoT2.Net.Orchestrator.Services.Templates;
using CoreJson = RIoT2.Core.Utils.Json;

namespace RIoT2.Net.Orchestrator.Services.Forecasts
{
    /// <summary>A forecast report the orchestrator has a series for, without its points.</summary>
    public sealed record ForecastInfo(string Id, string? Name, string? Node, string? Device, string Kind, string? Unit, DateTimeOffset IssuedAt,
        DateTimeOffset ReceivedAt, int Points, DateTimeOffset? From, DateTimeOffset? To);

    /// <summary>The latest forecast series per report (ADR 0019), and window queries on them.</summary>
    public interface IForecastStore
    {
        /// <summary>
        /// Keeps the report's value when it is a forecast series: a valid one replaces the report's previous series. Returns
        /// the problem when the value looks like a series but isn't valid, else <c>null</c>.
        /// </summary>
        string? Receive(Report report);

        /// <summary>The report's latest series, or <c>null</c>.</summary>
        ForecastSeries? Get(string reportId);

        /// <summary>Every report with a series, with its template's name, node and device when it still has one.</summary>
        IReadOnlyList<ForecastInfo> List();

        /// <summary>
        /// A window of the report's series and its figures (<see cref="ForecastWindow"/>); today and tomorrow are the
        /// orchestrator's local days. <c>null</c> when the report has no series. Throws <see cref="ArgumentException"/>
        /// for an invalid window or rank.
        /// </summary>
        ForecastSummary? Query(string reportId, string? window, int? hours = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int rank = 0);
    }

    /// <summary>
    /// <see cref="IForecastStore"/> in <c>StoredObjects/forecasts.db</c> (plain SQL like the outbox), cached in memory, so a
    /// restart keeps the last series until the device sends a new one. Thread-safe.
    /// </summary>
    public sealed class ForecastStore : IForecastStore, IDisposable
    {
        public const string FileName = "forecasts.db";
        private const int SchemaVersion = 1;

        private readonly SqliteConnection _connection;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (ForecastSeries Series, DateTimeOffset ReceivedAt)> _series = new(StringComparer.Ordinal);
        private readonly TimeProvider _time;
        private readonly ITemplateCatalog? _templates;
        private bool _disposed;

        public ForecastStore(string path, TimeProvider time, ITemplateCatalog? templates = null)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _templates = templates;
            FilePath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
            _connection.Open();
            Execute($"""
                PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS forecasts (
                  report_id TEXT PRIMARY KEY,
                  received_utc INTEGER NOT NULL,
                  series TEXT NOT NULL);
                PRAGMA user_version = {SchemaVersion};
                """);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT report_id, received_utc, series FROM forecasts";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var series = ForecastSeries.Create(reader.GetString(2));
                if (series != null && series.Validate() == null)
                    _series[reader.GetString(0)] = (series, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));
            }
        }

        /// <summary>Opens <c>StoredObjects/forecasts.db</c> under the content root.</summary>
        public static ForecastStore Open(IHostEnvironment environment, TimeProvider time, ITemplateCatalog templates) =>
            new(Path.Combine(environment.ContentRootPath, "StoredObjects", FileName), time, templates);

        public string FilePath { get; }

        /// <summary>The zone of today and tomorrow; tests replace it.</summary>
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

        public string? Receive(Report report)
        {
            ArgumentNullException.ThrowIfNull(report);
            if (string.IsNullOrWhiteSpace(report.Id))
                return null;
            var series = ForecastSeries.From(report.Value);
            if (series == null)
                return null;
            var problem = series.Validate();
            if (problem != null)
                return problem;

            var now = _time.GetUtcNow();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO forecasts (report_id, received_utc, series) VALUES ($id, $received, $series)
                    ON CONFLICT(report_id) DO UPDATE SET received_utc = excluded.received_utc, series = excluded.series
                    """;
                command.Parameters.AddWithValue("$id", report.Id);
                command.Parameters.AddWithValue("$received", now.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$series", CoreJson.Serialize(series));
                command.ExecuteNonQuery();
                _series[report.Id] = (series, now);
            }
            return null;
        }

        public ForecastSeries? Get(string reportId)
        {
            lock (_gate)
                return _series.TryGetValue(reportId, out var entry) ? Copy(entry.Series) : null;
        }

        public IReadOnlyList<ForecastInfo> List()
        {
            List<(string Id, ForecastSeries Series, DateTimeOffset ReceivedAt)> entries;
            lock (_gate)
                entries = _series.Select(x => (x.Key, x.Value.Series, x.Value.ReceivedAt)).ToList();
            return entries
                .Select(x =>
                {
                    var template = _templates?.FindReportTemplate(x.Id);
                    var points = x.Series.Points;
                    return new ForecastInfo(x.Id, template?.Name, template?.Node, template?.Device, x.Series.Kind, x.Series.Unit, x.Series.IssuedAt, x.ReceivedAt,
                        points.Count, points.Count > 0 ? points[0].Start : null, points.Count > 0 ? points[^1].End : null);
                })
                .OrderBy(x => x.Node, StringComparer.Ordinal).ThenBy(x => x.Device, StringComparer.Ordinal).ThenBy(x => x.Name ?? x.Id, StringComparer.Ordinal)
                .ToList();
        }

        public ForecastSummary? Query(string reportId, string? window, int? hours = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int rank = 0)
        {
            if (rank is < 0 or > ForecastWindow.MaxRank)
                throw new ArgumentException($"The rank must be 0-{ForecastWindow.MaxRank}.", nameof(rank));
            var now = _time.GetUtcNow();
            var problem = ForecastWindow.TryResolve(window, now, Zone, hours, from, to, out var start, out var end);
            if (problem != null)
                throw new ArgumentException(problem, nameof(window));
            var series = Get(reportId);
            if (series == null)
                return null;
            var summary = ForecastWindow.Summarize(series, start, end, rank, now);
            summary.Id = reportId;
            return summary;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _connection.Dispose();
            }
        }

        // Callers get their own copy: the cached series is shared.
        private static ForecastSeries Copy(ForecastSeries series) => new()
        {
            IssuedAt = series.IssuedAt,
            Kind = series.Kind,
            Unit = series.Unit,
            Resolution = series.Resolution,
            Points = series.Points.Select(x => new ForecastPoint(x.Start, x.End, x.Value)).ToList()
        };

        private void Execute(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
