using Microsoft.Data.Sqlite;

namespace RIoT2.Net.Orchestrator.Services.Outbox
{
    /// <summary>
    /// The durable outbox (design 7.1, ADR 0006): one SQLite file, <c>StoredObjects/outbox.db</c>, with
    /// one table, accessed with plain SQL. Thread-safe: every call runs under one lock on one connection.
    /// </summary>
    public sealed class OutboxStore : IDisposable
    {
        public const string FileName = "outbox.db";

        /// <summary>Rows in a final state are deleted this long after they became final.</summary>
        public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

        private const int SchemaVersion = 1;
        private const string Columns = "id, kind, target, subject, payload, created_utc, expires_utc, next_attempt_utc, updated_utc, attempts, status, result, last_error";
        private const string FinalStatuses = "('sent', 'done', 'failed', 'expired')";

        private readonly SqliteConnection _connection;
        private readonly Lock _gate = new();
        private bool _disposed;

        public OutboxStore(string path)
        {
            FilePath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            // No pooling: closing the store releases the file, so a backup or a test can remove it.
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
            _connection.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            Execute($"""
                CREATE TABLE IF NOT EXISTS outbox (
                  id TEXT PRIMARY KEY,
                  kind TEXT NOT NULL,
                  target TEXT NOT NULL,
                  subject TEXT,
                  payload TEXT NOT NULL,
                  created_utc INTEGER NOT NULL,
                  expires_utc INTEGER NOT NULL,
                  next_attempt_utc INTEGER NOT NULL,
                  updated_utc INTEGER NOT NULL,
                  attempts INTEGER NOT NULL DEFAULT 0,
                  status TEXT NOT NULL,
                  result TEXT,
                  last_error TEXT);
                CREATE INDEX IF NOT EXISTS outbox_due ON outbox(status, next_attempt_utc);
                CREATE INDEX IF NOT EXISTS outbox_target ON outbox(target, status, created_utc);
                PRAGMA user_version = {SchemaVersion};
                """);
        }

        /// <summary>Opens <c>StoredObjects/outbox.db</c> under the content root.</summary>
        public static OutboxStore Open(IHostEnvironment environment) =>
            new(Path.Combine(environment.ContentRootPath, "StoredObjects", FileName));

        public string FilePath { get; }

        public void Add(OutboxMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            Execute($"INSERT INTO outbox ({Columns}) VALUES ($id, $kind, $target, $subject, $payload, $created, $expires, $next, $updated, $attempts, $status, $result, $error)",
                ("$id", message.Id), ("$kind", message.Kind), ("$target", message.Target), ("$subject", message.Subject),
                ("$payload", message.Payload), ("$created", message.CreatedUtc), ("$expires", message.ExpiresUtc),
                ("$next", message.NextAttemptUtc), ("$updated", message.UpdatedUtc), ("$attempts", message.Attempts),
                ("$status", message.Status), ("$result", message.Result), ("$error", message.LastError));
        }

        public OutboxMessage Get(string id) =>
            Query($"SELECT {Columns} FROM outbox WHERE id = $id", ("$id", id)).SingleOrDefault();

        /// <summary>The oldest pending message for a target: deliveries are FIFO per target.</summary>
        public OutboxMessage NextPending(string target) =>
            Query($"SELECT {Columns} FROM outbox WHERE target = $target AND status = 'pending' ORDER BY created_utc, rowid LIMIT 1",
                ("$target", target)).SingleOrDefault();

        /// <summary>Moves a message to a final state.</summary>
        public void Complete(string id, string status, long now, string result = null, string error = null)
        {
            if (status == OutboxStatus.Pending)
                throw new ArgumentException("A final status is required.", nameof(status));
            Execute("UPDATE outbox SET status = $status, updated_utc = $now, result = $result, last_error = COALESCE($error, last_error) WHERE id = $id",
                ("$id", id), ("$status", status), ("$now", now), ("$result", result), ("$error", error));
        }

        /// <summary>
        /// Records a receiver's answer that arrived after its delivery attempt gave up waiting: the message
        /// becomes final unless it already is. An expired message takes the answer too, because the
        /// receiver got it in time. Returns <c>false</c> when the message is unknown, belongs to
        /// another target or is already final.
        /// </summary>
        public bool CompleteLate(string id, string target, string status, long now, string result, string error = null)
        {
            if (status == OutboxStatus.Pending)
                throw new ArgumentException("A final status is required.", nameof(status));
            return Execute("UPDATE outbox SET status = $status, updated_utc = $now, result = $result, last_error = COALESCE($error, last_error) WHERE id = $id AND target = $target AND status IN ('pending', 'expired')",
                ("$id", id), ("$target", target), ("$status", status), ("$now", now), ("$result", result), ("$error", error)) > 0;
        }

        /// <summary>The targets that have pending messages of <paramref name="kind"/>.</summary>
        public IReadOnlyList<string> PendingTargets(string kind)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = Command("SELECT DISTINCT target FROM outbox WHERE kind = $kind AND status = 'pending' ORDER BY target", ("$kind", kind));
                using var reader = command.ExecuteReader();
                var targets = new List<string>();
                while (reader.Read())
                    targets.Add(reader.GetString(0));
                return targets;
            }
        }

        /// <summary>Records a failed attempt; the message stays pending until <paramref name="nextAttempt"/>.</summary>
        public void ScheduleRetry(string id, int attempts, long nextAttempt, long now, string error)
        {
            Execute("UPDATE outbox SET attempts = $attempts, next_attempt_utc = $next, updated_utc = $now, last_error = $error WHERE id = $id AND status = 'pending'",
                ("$id", id), ("$attempts", attempts), ("$next", nextAttempt), ("$now", now), ("$error", error));
        }

        /// <summary>Makes every pending message for a target due now, e.g. when the target comes back online.</summary>
        public int ResetBackoff(string target, long now) =>
            Execute("UPDATE outbox SET next_attempt_utc = $now WHERE target = $target AND status = 'pending' AND next_attempt_utc > $now",
                ("$target", target), ("$now", now));

        /// <summary>Marks the pending messages for a target whose time-to-live has passed as expired and returns them.</summary>
        public IReadOnlyList<OutboxMessage> ExpireDue(string target, long now) =>
            Query($"UPDATE outbox SET status = 'expired', updated_utc = $now WHERE target = $target AND status = 'pending' AND expires_utc <= $now RETURNING {Columns}",
                ("$target", target), ("$now", now));

        /// <summary>The earliest expiry among a target's pending messages, or <c>null</c> when none is pending.</summary>
        public long? EarliestExpiry(string target) =>
            Scalar("SELECT MIN(expires_utc) FROM outbox WHERE target = $target AND status = 'pending'", ("$target", target));

        /// <summary>Deletes final rows that became final before <paramref name="before"/>.</summary>
        public int DeleteFinishedBefore(long before) =>
            Execute($"DELETE FROM outbox WHERE status IN {FinalStatuses} AND updated_utc < $before", ("$before", before));

        public OutboxHealth GetHealth(long now)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = Command("""
                    SELECT COUNT(*), COALESCE(SUM(($now - created_utc) * 2 > expires_utc - created_utc), 0)
                    FROM outbox WHERE status = 'pending'
                    """, ("$now", now));
                using var reader = command.ExecuteReader();
                reader.Read();
                return new OutboxHealth(reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        public OutboxSummary GetSummary(long now, int lastErrors = 10)
        {
            var counts = OutboxStatus.All.ToDictionary(status => status, _ => 0);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = Command("SELECT status, COUNT(*) FROM outbox GROUP BY status");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    counts[reader.GetString(0)] = reader.GetInt32(1);
            }
            var oldest = Query($"SELECT {Columns} FROM outbox WHERE status = 'pending' ORDER BY created_utc, rowid LIMIT 1").SingleOrDefault();
            var errors = Query($"SELECT {Columns} FROM outbox WHERE last_error IS NOT NULL ORDER BY updated_utc DESC, rowid DESC LIMIT $limit",
                ("$limit", lastErrors));
            return new OutboxSummary
            {
                Counts = counts,
                OldestPending = oldest == null ? null : OutboxEntry.From(oldest, now),
                LastErrors = errors.Select(message => OutboxEntry.From(message, now)).ToList()
            };
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

        private int Execute(string sql, params (string Name, object Value)[] parameters)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = Command(sql, parameters);
                return command.ExecuteNonQuery();
            }
        }

        private long? Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = Command(sql, parameters);
                return command.ExecuteScalar() is long value ? value : null;
            }
        }

        private List<OutboxMessage> Query(string sql, params (string Name, object Value)[] parameters)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var command = Command(sql, parameters);
                using var reader = command.ExecuteReader();
                var messages = new List<OutboxMessage>();
                while (reader.Read())
                {
                    messages.Add(new OutboxMessage
                    {
                        Id = reader.GetString(0),
                        Kind = reader.GetString(1),
                        Target = reader.GetString(2),
                        Subject = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Payload = reader.GetString(4),
                        CreatedUtc = reader.GetInt64(5),
                        ExpiresUtc = reader.GetInt64(6),
                        NextAttemptUtc = reader.GetInt64(7),
                        UpdatedUtc = reader.GetInt64(8),
                        Attempts = reader.GetInt32(9),
                        Status = reader.GetString(10),
                        Result = reader.IsDBNull(11) ? null : reader.GetString(11),
                        LastError = reader.IsDBNull(12) ? null : reader.GetString(12)
                    });
                }
                return messages;
            }
        }

        private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
        {
            var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
    }

    /// <summary>Pending messages, and how many of them are older than half their time-to-live.</summary>
    public readonly record struct OutboxHealth(int Pending, int Stale);
}
