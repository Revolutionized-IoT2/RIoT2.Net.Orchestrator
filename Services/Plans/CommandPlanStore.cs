using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace RIoT2.Net.Orchestrator.Services.Plans
{
    /// <summary>The values of <see cref="CommandPlan.Status"/>.</summary>
    public static class CommandPlanStatus
    {
        /// <summary>Its steps are sent at their times.</summary>
        public const string Active = "active";

        /// <summary>Every step was sent, skipped or missed.</summary>
        public const string Completed = "completed";

        /// <summary>Ended by a user, a workflow or a tool before its last step.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>A newer plan for the same command took its place.</summary>
        public const string Replaced = "replaced";
    }

    /// <summary>The values of <see cref="CommandPlanStep.Status"/>.</summary>
    public static class CommandPlanStepStatus
    {
        /// <summary>Waiting for its time.</summary>
        public const string Pending = "pending";

        /// <summary>In the command outbox, waiting for the node's answer.</summary>
        public const string Delivering = "delivering";

        /// <summary>The device ran it (also when the node had run it before: <c>duplicate</c>).</summary>
        public const string Executed = "executed";

        /// <summary>Sent to a node that doesn't report results.</summary>
        public const string Sent = "sent";

        public const string Failed = "failed";
        public const string Expired = "expired";
        public const string UnknownCommand = "unknownCommand";

        /// <summary>A later step was due at the same time, so this one wasn't sent.</summary>
        public const string Skipped = "skipped";

        /// <summary>Its time passed while the orchestrator wasn't running, and the plan doesn't catch up.</summary>
        public const string Missed = "missed";

        /// <summary>The plan ended before its time.</summary>
        public const string Cancelled = "cancelled";
    }

    /// <summary>What <see cref="CommandPlan.CatchUp"/> does after a pause, such as a restart.</summary>
    public static class CommandPlanCatchUp
    {
        /// <summary>Send the latest step whose time passed: the value the command should have now.</summary>
        public const string Latest = "latest";

        /// <summary>Skip steps whose time passed more than <see cref="CommandPlanService.LateGrace"/> ago.</summary>
        public const string None = "none";

        public static readonly IReadOnlyList<string> All = [Latest, None];
    }

    /// <summary>One value of a plan and how its delivery went.</summary>
    public sealed class CommandPlanStep
    {
        public DateTimeOffset At { get; set; }
        public JsonNode? Value { get; set; }

        /// <summary>One of <see cref="CommandPlanStepStatus"/>.</summary>
        public string Status { get; set; } = CommandPlanStepStatus.Pending;

        /// <summary>The command delivery's id once sent (<c>GET /api/v2/commands/{correlationId}</c>).</summary>
        public string? CorrelationId { get; set; }

        public DateTimeOffset? SentAt { get; set; }
        public string? Error { get; set; }

        [JsonIgnore]
        public bool IsOpen => Status is CommandPlanStepStatus.Pending or CommandPlanStepStatus.Delivering;
    }

    /// <summary>
    /// The future values of one command (ADR 0019): the orchestrator sends each step's value at its time through the command
    /// outbox. A command has at most one active plan; a new one replaces it.
    /// </summary>
    public sealed class CommandPlan
    {
        public string Id { get; set; } = "";
        public string CommandId { get; set; } = "";
        public string? Name { get; set; }

        /// <summary>Who made it: a workflow, a language model step, an MCP client or a user.</summary>
        public string? Source { get; set; }

        /// <summary>One of <see cref="CommandPlanCatchUp"/>.</summary>
        public string CatchUp { get; set; } = CommandPlanCatchUp.Latest;

        /// <summary>One of <see cref="CommandPlanStatus"/>.</summary>
        public string Status { get; set; } = CommandPlanStatus.Active;

        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<CommandPlanStep> Steps { get; set; } = [];

        /// <summary>The time of the next pending step, if any.</summary>
        public DateTimeOffset? Next => Status == CommandPlanStatus.Active
            ? Steps.Where(x => x.Status == CommandPlanStepStatus.Pending).Select(x => (DateTimeOffset?)x.At).Min()
            : null;
    }

    /// <summary>The body of <c>POST /api/v2/command-plans</c>.</summary>
    public sealed class CommandPlanRequest
    {
        public string? CommandId { get; set; }
        public string? Name { get; set; }
        public string? Source { get; set; }
        public string? CatchUp { get; set; }
        public List<CommandPlanStepRequest>? Steps { get; set; }
    }

    /// <summary>A step of <see cref="CommandPlanRequest"/>.</summary>
    public sealed class CommandPlanStepRequest
    {
        public DateTimeOffset? At { get; set; }
        public JsonNode? Value { get; set; }
    }

    /// <summary>
    /// The plans in <c>StoredObjects/command-plans.db</c>: one row per plan with its JSON, plain SQL like the outbox. Finished plans
    /// are kept for <see cref="Retention"/>. Not thread-safe: <see cref="CommandPlanService"/> calls it under its lock.
    /// </summary>
    public sealed class CommandPlanStore : IDisposable
    {
        public const string FileName = "command-plans.db";
        public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
        private const int SchemaVersion = 1;
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly SqliteConnection _connection;

        public CommandPlanStore(string path)
        {
            FilePath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
            _connection.Open();
            Execute($"""
                PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS plans (
                  id TEXT PRIMARY KEY,
                  command_id TEXT NOT NULL,
                  status TEXT NOT NULL,
                  created_utc INTEGER NOT NULL,
                  updated_utc INTEGER NOT NULL,
                  plan TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS plans_command ON plans(command_id, status);
                CREATE INDEX IF NOT EXISTS plans_created ON plans(created_utc);
                PRAGMA user_version = {SchemaVersion};
                """);
        }

        /// <summary>Opens <c>StoredObjects/command-plans.db</c> under the content root.</summary>
        public static CommandPlanStore Open(IHostEnvironment environment) =>
            new(Path.Combine(environment.ContentRootPath, "StoredObjects", FileName));

        public string FilePath { get; }

        public void Save(CommandPlan plan)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO plans (id, command_id, status, created_utc, updated_utc, plan) VALUES ($id, $command, $status, $created, $updated, $plan)
                ON CONFLICT(id) DO UPDATE SET status = excluded.status, updated_utc = excluded.updated_utc, plan = excluded.plan
                """;
            command.Parameters.AddWithValue("$id", plan.Id);
            command.Parameters.AddWithValue("$command", plan.CommandId);
            command.Parameters.AddWithValue("$status", plan.Status);
            command.Parameters.AddWithValue("$created", plan.CreatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$updated", plan.UpdatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(plan, Json));
            command.ExecuteNonQuery();
        }

        public CommandPlan? Get(string id) =>
            Read("SELECT plan FROM plans WHERE id = $id", ("$id", id)).FirstOrDefault();

        /// <summary>Newest first; only <paramref name="commandId"/>'s, and only active ones when asked.</summary>
        public List<CommandPlan> List(string? commandId, bool activeOnly, int limit) =>
            Read("SELECT plan FROM plans WHERE ($command IS NULL OR command_id = $command) AND ($active = 0 OR status = 'active') ORDER BY created_utc DESC LIMIT $limit",
                ("$command", commandId), ("$active", activeOnly ? 1 : 0), ("$limit", limit));

        /// <summary>The plans that are active or still wait for a delivery.</summary>
        public List<CommandPlan> Live() =>
            Read("SELECT plan FROM plans WHERE status = 'active' OR plan LIKE '%\"status\":\"delivering\"%'").Where(x => x.Status == CommandPlanStatus.Active || x.Steps.Any(s => s.Status == CommandPlanStepStatus.Delivering)).ToList();

        public void Prune(DateTimeOffset now)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM plans WHERE status <> 'active' AND updated_utc < $before";
            command.Parameters.AddWithValue("$before", now.Subtract(Retention).ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();

        private List<CommandPlan> Read(string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var plans = new List<CommandPlan>();
            while (reader.Read())
            {
                var plan = JsonSerializer.Deserialize<CommandPlan>(reader.GetString(0), Json);
                if (plan != null)
                    plans.Add(plan);
            }
            return plans;
        }

        private void Execute(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
