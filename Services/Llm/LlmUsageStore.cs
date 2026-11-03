using Microsoft.Data.Sqlite;

namespace RIoT2.Net.Orchestrator.Services.Llm
{
    /// <summary>One Run LLM step, as recorded for the usage view.</summary>
    public sealed record LlmUsageRecord(
        DateTimeOffset At,
        string Connection,
        string Provider,
        string Model,
        string? WorkflowDefinitionId,
        string? WorkflowInstanceId,
        string? ActivityId,
        bool Succeeded,
        string? ErrorCode,
        int InputTokens,
        int OutputTokens,
        int ToolCalls,
        int Commands,
        long DurationMs);

    /// <summary>A connection's (or a day's) totals.</summary>
    public sealed record LlmUsageTotal(string Key, string Connection, int Runs, int Failures, long InputTokens, long OutputTokens);

    /// <summary>The usage view: totals per connection and per day, and the latest steps.</summary>
    public sealed record LlmUsageSummary(DateTimeOffset From, IReadOnlyList<LlmUsageTotal> Connections, IReadOnlyList<LlmUsageTotal> Days, IReadOnlyList<LlmUsageRecord> Recent);

    /// <summary>
    /// The Run LLM usage records (ADR 0016): <c>StoredObjects/llm-usage.db</c>, one table, plain SQL like the outbox, kept
    /// for <see cref="Retention"/>. Thread-safe: every call runs under one lock on one connection.
    /// </summary>
    public sealed class LlmUsageStore : IDisposable
    {
        public const string FileName = "llm-usage.db";
        public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
        private const int SchemaVersion = 1;
        private const string Columns = "at_utc, connection, provider, model, workflow_definition_id, workflow_instance_id, activity_id, succeeded, error_code, input_tokens, output_tokens, tool_calls, commands, duration_ms";

        private readonly SqliteConnection _connection;
        private readonly Lock _gate = new();
        private readonly TimeProvider _time;
        private long _nextPrune;
        private bool _disposed;

        public LlmUsageStore(string path, TimeProvider time)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
            FilePath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
            _connection.Open();
            Execute($"""
                PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS llm_usage (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  at_utc INTEGER NOT NULL,
                  connection TEXT NOT NULL,
                  provider TEXT NOT NULL,
                  model TEXT NOT NULL,
                  workflow_definition_id TEXT,
                  workflow_instance_id TEXT,
                  activity_id TEXT,
                  succeeded INTEGER NOT NULL,
                  error_code TEXT,
                  input_tokens INTEGER NOT NULL,
                  output_tokens INTEGER NOT NULL,
                  tool_calls INTEGER NOT NULL,
                  commands INTEGER NOT NULL,
                  duration_ms INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS llm_usage_at ON llm_usage(at_utc);
                PRAGMA user_version = {SchemaVersion};
                """);
        }

        /// <summary>Opens <c>StoredObjects/llm-usage.db</c> under the content root.</summary>
        public static LlmUsageStore Open(IHostEnvironment environment, TimeProvider time) =>
            new(Path.Combine(environment.ContentRootPath, "StoredObjects", FileName), time);

        public string FilePath { get; }

        public void Add(LlmUsageRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = _connection.CreateCommand();
                command.CommandText = $"INSERT INTO llm_usage ({Columns}) VALUES ($at, $connection, $provider, $model, $definition, $instance, $activity, $succeeded, $error, $input, $output, $tools, $commands, $duration)";
                Bind(command, ("$at", record.At.ToUnixTimeMilliseconds()), ("$connection", record.Connection), ("$provider", record.Provider), ("$model", record.Model),
                    ("$definition", record.WorkflowDefinitionId), ("$instance", record.WorkflowInstanceId), ("$activity", record.ActivityId),
                    ("$succeeded", record.Succeeded ? 1 : 0), ("$error", record.ErrorCode), ("$input", record.InputTokens), ("$output", record.OutputTokens),
                    ("$tools", record.ToolCalls), ("$commands", record.Commands), ("$duration", record.DurationMs));
                command.ExecuteNonQuery();
                if (now.ToUnixTimeMilliseconds() >= _nextPrune)
                {
                    _nextPrune = now.AddHours(1).ToUnixTimeMilliseconds();
                    using var prune = _connection.CreateCommand();
                    prune.CommandText = "DELETE FROM llm_usage WHERE at_utc < $before";
                    Bind(prune, ("$before", now.Subtract(Retention).ToUnixTimeMilliseconds()));
                    prune.ExecuteNonQuery();
                }
            }
        }

        /// <summary>The usage since <paramref name="from"/>, optionally of one connection, with the <paramref name="recent"/> latest steps.</summary>
        public LlmUsageSummary Summarize(DateTimeOffset from, string? connection = null, int recent = 50)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var filter = "WHERE at_utc >= $from" + (string.IsNullOrWhiteSpace(connection) ? "" : " AND connection = $connection");
                var parameters = new (string, object?)[] { ("$from", from.ToUnixTimeMilliseconds()), ("$connection", connection) };
                var connections = Totals($"SELECT connection, connection, COUNT(*), SUM(1 - succeeded), SUM(input_tokens), SUM(output_tokens) FROM llm_usage {filter} GROUP BY connection ORDER BY connection", parameters);
                var days = Totals($"SELECT strftime('%Y-%m-%d', at_utc / 1000, 'unixepoch'), connection, COUNT(*), SUM(1 - succeeded), SUM(input_tokens), SUM(output_tokens) FROM llm_usage {filter} GROUP BY 1, 2 ORDER BY 1, 2", parameters);

                using var command = _connection.CreateCommand();
                command.CommandText = $"SELECT {Columns} FROM llm_usage {filter} ORDER BY at_utc DESC, id DESC LIMIT $limit";
                Bind(command, [.. parameters, ("$limit", Math.Clamp(recent, 0, 500))]);
                using var reader = command.ExecuteReader();
                var records = new List<LlmUsageRecord>();
                while (reader.Read())
                {
                    records.Add(new LlmUsageRecord(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetInt64(7) != 0, reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                        reader.GetInt32(12), reader.GetInt64(13)));
                }
                return new LlmUsageSummary(from, connections, days, records);
            }
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

        private List<LlmUsageTotal> Totals(string sql, (string, object?)[] parameters)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            Bind(command, parameters);
            using var reader = command.ExecuteReader();
            var totals = new List<LlmUsageTotal>();
            while (reader.Read())
                totals.Add(new LlmUsageTotal(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5)));
            return totals;
        }

        private void Execute(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void Bind(SqliteCommand command, params (string Name, object? Value)[] parameters)
        {
            foreach (var (name, value) in parameters)
            {
                if (command.CommandText.Contains(name, StringComparison.Ordinal))
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }
    }
}
