using System.Globalization;
using Microsoft.Data.Sqlite;
using RetailPulse.Api.Data;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// SQLite-backed implementation of <see cref="IApprovalGate"/>.
/// Thread-safe — each operation opens its own connection over a shared cache
/// through <see cref="SqliteMount.OpenAsync"/>, which applies the centralized
/// SMB-safe pragmas (busy_timeout, DELETE journaling, synchronous=FULL) to every
/// connection so it is durable on the Azure Files mount and does not fail fast on a
/// transient lock; single-writer only (API runs maxReplicas: 1). All database I/O
/// uses async APIs with CancellationToken support.
/// WaitForApprovalAsync uses exponential backoff (driven by an injected
/// <see cref="TimeProvider"/>) instead of fixed-interval polling, and every
/// Pending → terminal transition is a single conditional SQL update so a
/// simultaneous human response and timeout race resolves to exactly one persisted
/// winner. Both <see cref="WaitForApprovalAsync"/> and <see cref="RespondAsync"/>
/// re-read the row after their conditional UPDATE and return the actual persisted
/// winner, so the returned <see cref="ApprovalResult"/> and the stored row always
/// agree — no double-resolution is possible, and the endpoint layer can echo the
/// returned result verbatim to HTTP callers and SignalR subscribers.
///
/// <para>
/// Restart safety: every row carries the id of the process that owns its waiter
/// (<c>AgentInstanceId</c>) plus a heartbeat and the authoritative stored timeout.
/// <see cref="ReconcilePendingAsync"/> is called by
/// <see cref="ApprovalReconciliationBackgroundService"/> before traffic and closes
/// any Pending row whose owning process is gone through the configured
/// <see cref="IApprovalResumeStrategy"/>. Wave 1 orphans terminally; Wave 2 will
/// use the persisted <see cref="ApprovalContext.SessionId"/> /
/// <see cref="ApprovalContext.ConversationId"/> correlation to resume a
/// checkpointed execution without any gate-side change (see #93/#94).
/// </para>
/// </summary>
public sealed class SqliteApprovalGate : IApprovalGate
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteApprovalGate> _logger;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeProvider _timeProvider;

    private const string _iso8601 = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    // Terminal reason string constants written into the durable TerminalReason column.
    // These are persisted verbatim into the endpoint response so the UI can render a
    // specific outcome (Timeout vs OrphanedOnRestart) instead of a generic
    // "not-approved" label.
    internal const string ReasonHumanApproved = "HumanApproved";
    internal const string ReasonHumanRejected = "HumanRejected";
    internal const string ReasonHumanModified = "HumanModified";
    internal const string ReasonTimeout = "Timeout";
    internal const string ReasonOrphanedOnRestart = "OrphanedOnRestart";

    // Exponential backoff parameters for WaitForApprovalAsync
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(4);
    internal const double BackoffMultiplier = 2.0;

    /// <summary>
    /// Stable identity of the process that owns this gate. New GUID per gate
    /// instance so two gates (e.g., two independent tests) never collide, and a
    /// real restart gets a fresh id so reconciliation can see the previous owner
    /// is gone. Exposed so the reconciliation service and tests can assert that
    /// reconciliation only touches rows written by a DIFFERENT process.
    /// </summary>
    public string InstanceId { get; }

    public SqliteApprovalGate(
        string dbPath,
        ILogger<SqliteApprovalGate> logger,
        TimeSpan? defaultTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);
        _timeProvider = timeProvider ?? TimeProvider.System;
        InstanceId = Guid.NewGuid().ToString("N");

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    private async Task InitializeSchemaAsync()
    {
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString);

        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS ApprovalRequests (
                    RequestId        TEXT PRIMARY KEY,
                    AgentId          TEXT NOT NULL,
                    UserId           TEXT NOT NULL,
                    Action           TEXT NOT NULL,
                    Impact           TEXT,
                    Urgency          TEXT DEFAULT 'medium',
                    Reasoning        TEXT,
                    SessionId        TEXT,
                    ConversationId   TEXT,
                    AgentInstanceId  TEXT NOT NULL DEFAULT '',
                    HeartbeatAt      TEXT,
                    TimeoutSeconds   INTEGER NOT NULL DEFAULT 300,
                    Decision         TEXT DEFAULT 'Pending',
                    TerminalReason   TEXT,
                    Comment          TEXT,
                    CreatedAt        TEXT NOT NULL,
                    ExpiresAt        TEXT NOT NULL,
                    RespondedAt      TEXT,
                    Kind             TEXT NOT NULL DEFAULT 'tool',
                    PlanId           TEXT,
                    RoundNumber      INTEGER NOT NULL DEFAULT 0,
                    Payload          TEXT,
                    ResponsePayload  TEXT
                );

                CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_UserId_Decision
                    ON ApprovalRequests (UserId, Decision);

                CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_CreatedAt
                    ON ApprovalRequests (CreatedAt DESC);

                CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_Decision_Instance
                    ON ApprovalRequests (Decision, AgentInstanceId);

                CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_PlanId_Decision
                    ON ApprovalRequests (PlanId, Decision);

                CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_UserId_Kind_Decision
                    ON ApprovalRequests (UserId, Kind, Decision);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Additive migration for databases created before the lifecycle-hardening schema
        // landed. SQLite's ALTER TABLE ADD COLUMN is idempotent when guarded by the
        // PRAGMA table_info sniff. Every added column has a defaultable value so the row
        // set stays queryable during the migration.
        HashSet<string> existingColumns = await ReadColumnsAsync(conn);
        (string name, string ddl)[] additions =
        [
            ("SessionId",       "ALTER TABLE ApprovalRequests ADD COLUMN SessionId TEXT"),
            ("ConversationId",  "ALTER TABLE ApprovalRequests ADD COLUMN ConversationId TEXT"),
            ("AgentInstanceId", "ALTER TABLE ApprovalRequests ADD COLUMN AgentInstanceId TEXT NOT NULL DEFAULT ''"),
            ("HeartbeatAt",     "ALTER TABLE ApprovalRequests ADD COLUMN HeartbeatAt TEXT"),
            ("TimeoutSeconds",  "ALTER TABLE ApprovalRequests ADD COLUMN TimeoutSeconds INTEGER NOT NULL DEFAULT 300"),
            ("TerminalReason",  "ALTER TABLE ApprovalRequests ADD COLUMN TerminalReason TEXT"),
            ("Kind",            "ALTER TABLE ApprovalRequests ADD COLUMN Kind TEXT NOT NULL DEFAULT 'tool'"),
            ("PlanId",          "ALTER TABLE ApprovalRequests ADD COLUMN PlanId TEXT"),
            ("RoundNumber",     "ALTER TABLE ApprovalRequests ADD COLUMN RoundNumber INTEGER NOT NULL DEFAULT 0"),
            ("Payload",         "ALTER TABLE ApprovalRequests ADD COLUMN Payload TEXT"),
            ("ResponsePayload", "ALTER TABLE ApprovalRequests ADD COLUMN ResponsePayload TEXT"),
        ];
        foreach ((string name, string ddl) in additions)
        {
            if (existingColumns.Contains(name))
                continue;
            await using SqliteCommand alter = conn.CreateCommand();
            alter.CommandText = ddl;
            await alter.ExecuteNonQueryAsync();
        }

        // Create the plan-review helper indexes AFTER any additive column
        // migration completes so older databases that predate the Kind/PlanId
        // columns still gain the same indexes on next boot.
        await using SqliteCommand idx = conn.CreateCommand();
        idx.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_PlanId_Decision
                    ON ApprovalRequests (PlanId, Decision);
                CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_UserId_Kind_Decision
                    ON ApprovalRequests (UserId, Kind, Decision);
                """;
        await idx.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection conn)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(ApprovalRequests)";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            cols.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        return cols;
    }

    public async Task<ApprovalRequest> RequestApprovalAsync(ApprovalContext context, CancellationToken ct = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now.Add(_defaultTimeout);

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ApprovalRequests (
                RequestId, AgentId, UserId, Action, Impact, Urgency, Reasoning,
                SessionId, ConversationId, AgentInstanceId, HeartbeatAt, TimeoutSeconds,
                Decision, CreatedAt, ExpiresAt,
                Kind, PlanId, RoundNumber, Payload)
            VALUES (
                @id, @agentId, @userId, @action, @impact, @urgency, @reasoning,
                @sessionId, @conversationId, @instanceId, @heartbeatAt, @timeoutSeconds,
                'Pending', @createdAt, @expiresAt,
                @kind, @planId, @roundNumber, @payload)
            """;
        cmd.Parameters.AddWithValue("@id", requestId);
        cmd.Parameters.AddWithValue("@agentId", context.AgentId);
        cmd.Parameters.AddWithValue("@userId", context.UserId);
        cmd.Parameters.AddWithValue("@action", context.Action);
        cmd.Parameters.AddWithValue("@impact", (object?)context.Impact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@urgency", context.Urgency);
        cmd.Parameters.AddWithValue("@reasoning", (object?)context.Reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sessionId", (object?)context.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conversationId", (object?)context.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@instanceId", InstanceId);
        cmd.Parameters.AddWithValue("@heartbeatAt", now.ToString(_iso8601, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@timeoutSeconds", (long)_defaultTimeout.TotalSeconds);
        cmd.Parameters.AddWithValue("@createdAt", now.ToString(_iso8601, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@expiresAt", expiresAt.ToString(_iso8601, CultureInfo.InvariantCulture));
        // Kind defaults to the tool tag so any pre-#94 producer (ApprovalTool)
        // that still builds ApprovalContext without setting Kind lands with the
        // exact same value the schema default would have used — no behavior
        // change for tool approvals.
        cmd.Parameters.AddWithValue(
            "@kind",
            string.IsNullOrWhiteSpace(context.Kind) ? ApprovalKind.Tool : context.Kind);
        cmd.Parameters.AddWithValue("@planId", (object?)context.PlanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@roundNumber", context.RoundNumber);
        cmd.Parameters.AddWithValue("@payload", (object?)context.Payload ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation(
            "Approval request {RequestId} created (kind={Kind}, planId={PlanId}, round={Round}) for agent {AgentId}, user {UserId}, urgency {Urgency}, instance {InstanceId}, timeout {TimeoutSeconds}s",
            requestId, context.Kind, context.PlanId, context.RoundNumber,
            context.AgentId, context.UserId, context.Urgency, InstanceId, (long)_defaultTimeout.TotalSeconds);

        return new ApprovalRequest(requestId, context, now, expiresAt);
    }

    public async Task<ApprovalResult> GetResultAsync(string requestId, CancellationToken ct = default)
    {
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        ApprovalRequest row = await ReadRowAsync(conn, requestId, ct)
            ?? throw new KeyNotFoundException($"Approval request '{requestId}' not found.");

        return new ApprovalResult(
            row.RequestId,
            row.Decision,
            row.Comment,
            row.RespondedAt,
            row.TerminalReason,
            row.ResponsePayload);
    }

    public async Task<ApprovalResult> WaitForApprovalAsync(string requestId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan effectiveTimeout = timeout ?? _defaultTimeout;
        DateTimeOffset deadline = _timeProvider.GetUtcNow().Add(effectiveTimeout);
        TimeSpan backoff = InitialBackoff;

        _logger.LogInformation(
            "Waiting for approval {RequestId} with timeout {Timeout}",
            requestId, effectiveTimeout);

        while (!ct.IsCancellationRequested)
        {
            await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

            ApprovalRequest row = await ReadRowAsync(conn, requestId, ct)
                ?? throw new KeyNotFoundException($"Approval request '{requestId}' not found.");

            if (row.Decision != ApprovalDecision.Pending)
            {
                return new ApprovalResult(row.RequestId, row.Decision, row.Comment, row.RespondedAt, row.TerminalReason, row.ResponsePayload);
            }

            // Heartbeat the row so operators can observe that this waiter is alive.
            await UpdateHeartbeatAsync(conn, requestId, ct);

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                // Race-safe timeout: conditional UPDATE from Pending. Whether we win or a
                // human beat us to it, we then read the row and return the ACTUAL winner
                // so the persisted row and this waiter agree on exactly one terminal
                // outcome. Never returns TimedOut when a human decision landed first.
                return await TransitionAndReadAsync(
                    requestId,
                    ApprovalDecision.TimedOut,
                    ReasonTimeout,
                    comment: "Approval timed out — no response received.",
                    responsePayload: null,
                    ct);
            }

            await Task.Delay(backoff, _timeProvider, ct);
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * BackoffMultiplier, MaxBackoff.TotalMilliseconds));
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    public async Task<ApprovalResult> RespondAsync(
        string requestId,
        ApprovalDecision decision,
        string? comment = null,
        string? responsePayload = null,
        CancellationToken ct = default)
    {
        string reason = decision switch
        {
            ApprovalDecision.Approved => ReasonHumanApproved,
            ApprovalDecision.Rejected => ReasonHumanRejected,
            ApprovalDecision.Modified => ReasonHumanModified,
            ApprovalDecision.TimedOut => ReasonTimeout,
            ApprovalDecision.Orphaned => ReasonOrphanedOnRestart,
            ApprovalDecision.Pending => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Cannot record Pending as a terminal decision."),
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Cannot record Pending as a terminal decision.")
        };

        (bool won, ApprovalRequest? current) = await TryConditionalTransitionAsync(requestId, decision, reason, comment, responsePayload, ct);
        if (current is null)
        {
            _logger.LogWarning("Approval {RequestId} was not updated — the request does not exist.", requestId);
            throw new KeyNotFoundException($"Approval request '{requestId}' not found.");
        }

        if (won)
        {
            _logger.LogInformation(
                "Approval {RequestId} resolved as {Decision} ({Reason}) at {RespondedAt}",
                requestId, decision, reason, current.RespondedAt);
        }
        else
        {
            // Requested decision lost the race — an earlier terminal write already
            // owned this row (timeout, orphan reconciliation, or another human).
            // Return the persisted winner so the caller (endpoint + SignalR) reports
            // the actual user-visible outcome instead of a decision it did not win.
            _logger.LogWarning(
                "Approval {RequestId} response '{Requested}' rejected — row already terminal as {Decision} ({Reason}).",
                requestId, decision, current.Decision, current.TerminalReason);
        }

        return new ApprovalResult(
            current.RequestId,
            current.Decision,
            current.Comment,
            current.RespondedAt,
            current.TerminalReason,
            current.ResponsePayload);
    }

    /// <summary>
    /// Called by <see cref="ApprovalReconciliationBackgroundService"/> before traffic.
    /// Walks every Pending row and applies the configured
    /// <see cref="IApprovalResumeStrategy"/> to rows whose <c>AgentInstanceId</c> is
    /// not this process. Idempotent — a second call is a no-op because the earlier
    /// call terminated (or resumed) every eligible row. Rows owned by the current
    /// instance are always left alone: a genuinely live current-instance waiter
    /// keeps its Pending row until it wins the terminal update itself.
    /// </summary>
    /// <returns>How many Pending rows were terminated during this pass.</returns>
    public async Task<int> ReconcilePendingAsync(IApprovalResumeStrategy strategy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        List<ApprovalRequest> pending = await LoadPendingAsync(ct);
        int terminated = 0;
        foreach (ApprovalRequest row in pending)
        {
            if (row.Decision != ApprovalDecision.Pending)
                continue;

            // Read the owning instance id via a fresh probe — we deliberately do NOT hoist
            // AgentInstanceId onto the ApprovalRequest record because the contract (used
            // by the tool + endpoints) should stay lean. The probe is bounded by the
            // number of Pending rows at startup, which is tiny.
            string? owningInstance = await ReadOwningInstanceAsync(row.RequestId, ct);
            if (owningInstance is null)
                continue;
            if (string.Equals(owningInstance, InstanceId, StringComparison.Ordinal))
                continue;

            ApprovalResumeAction action = await strategy.DecideAsync(row, ct);
            switch (action)
            {
                case ApprovalResumeAction.OrphanTerminal:
                    (bool won, _) = await TryConditionalTransitionAsync(
                        row.RequestId,
                        ApprovalDecision.Orphaned,
                        ReasonOrphanedOnRestart,
                        comment: "Approval closed by restart reconciliation — no in-process waiter survived.",
                        responsePayload: null,
                        ct);
                    if (won)
                    {
                        terminated++;
                        _logger.LogWarning(
                            "Approval {RequestId} orphaned on restart (previous instance {PreviousInstance}, current {CurrentInstance})",
                            row.RequestId, owningInstance, InstanceId);
                    }
                    break;
                case ApprovalResumeAction.Resume:
                    // Wave 2 seam. Refresh the owning instance so the resumed execution
                    // takes ownership and the next reconciliation pass leaves the row alone.
                    await AdoptRowAsync(row.RequestId, ct);
                    _logger.LogInformation(
                        "Approval {RequestId} adopted by current instance {CurrentInstance} for resume (was {PreviousInstance})",
                        row.RequestId, InstanceId, owningInstance);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown {nameof(ApprovalResumeAction)} value '{action}' returned by resume strategy.");
            }
        }

        return terminated;
    }

    public async Task<IReadOnlyList<ApprovalRequest>> GetPendingAsync(string userId, CancellationToken ct = default)
    {
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ApprovalRequests
            WHERE UserId = @userId AND Decision = 'Pending'
            ORDER BY CreatedAt DESC
            """;
        cmd.Parameters.AddWithValue("@userId", userId);

        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> GetHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ApprovalRequests
            WHERE Decision != 'Pending'
            ORDER BY RespondedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        return await ReadAllAsync(cmd, ct);
    }

    // ── Race-safe transitions ────────────────────────────────────────────

    private async Task<ApprovalResult> TransitionAndReadAsync(
        string requestId,
        ApprovalDecision decision,
        string reason,
        string? comment,
        string? responsePayload,
        CancellationToken ct)
    {
        (bool _, ApprovalRequest? current) = await TryConditionalTransitionAsync(requestId, decision, reason, comment, responsePayload, ct);
        return current is null
            ? throw new KeyNotFoundException($"Approval request '{requestId}' not found.")
            : new ApprovalResult(current.RequestId, current.Decision, current.Comment, current.RespondedAt, current.TerminalReason, current.ResponsePayload);
    }

    private async Task<(bool won, ApprovalRequest? current)> TryConditionalTransitionAsync(
        string requestId,
        ApprovalDecision decision,
        string reason,
        string? comment,
        string? responsePayload,
        CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);

        int affected;
        await using (SqliteCommand update = conn.CreateCommand())
        {
            update.CommandText = """
                UPDATE ApprovalRequests
                SET Decision = @decision,
                    TerminalReason = @reason,
                    Comment = @comment,
                    RespondedAt = @respondedAt,
                    ResponsePayload = @responsePayload
                WHERE RequestId = @id AND Decision = 'Pending'
                """;
            update.Parameters.AddWithValue("@id", requestId);
            update.Parameters.AddWithValue("@decision", decision.ToString());
            update.Parameters.AddWithValue("@reason", reason);
            update.Parameters.AddWithValue("@comment", (object?)comment ?? DBNull.Value);
            update.Parameters.AddWithValue("@respondedAt", now.ToString(_iso8601, CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("@responsePayload", (object?)responsePayload ?? DBNull.Value);
            affected = await update.ExecuteNonQueryAsync(ct);
        }

        ApprovalRequest? current = await ReadRowAsync(conn, requestId, ct);
        return (affected == 1, current);
    }

    private async Task UpdateHeartbeatAsync(SqliteConnection conn, string requestId, CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ApprovalRequests
            SET HeartbeatAt = @heartbeatAt
            WHERE RequestId = @id AND AgentInstanceId = @instanceId AND Decision = 'Pending'
            """;
        cmd.Parameters.AddWithValue("@id", requestId);
        cmd.Parameters.AddWithValue("@instanceId", InstanceId);
        cmd.Parameters.AddWithValue("@heartbeatAt", now.ToString(_iso8601, CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task AdoptRowAsync(string requestId, CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ApprovalRequests
            SET AgentInstanceId = @instanceId, HeartbeatAt = @heartbeatAt
            WHERE RequestId = @id AND Decision = 'Pending'
            """;
        cmd.Parameters.AddWithValue("@id", requestId);
        cmd.Parameters.AddWithValue("@instanceId", InstanceId);
        cmd.Parameters.AddWithValue("@heartbeatAt", now.ToString(_iso8601, CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<ApprovalRequest>> LoadPendingAsync(CancellationToken ct)
    {
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ApprovalRequests
            WHERE Decision = 'Pending'
            ORDER BY CreatedAt ASC
            """;
        return await ReadAllAsync(cmd, ct);
    }

    private async Task<string?> ReadOwningInstanceAsync(string requestId, CancellationToken ct)
    {
        await using SqliteConnection conn = await SqliteMount.OpenAsync(_connectionString, ct);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AgentInstanceId FROM ApprovalRequests WHERE RequestId = @id";
        cmd.Parameters.AddWithValue("@id", requestId);
        object? raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null or DBNull ? null : (string?)raw;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task<ApprovalRequest?> ReadRowAsync(SqliteConnection conn, string requestId, CancellationToken ct = default)
    {
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ApprovalRequests WHERE RequestId = @id";
        cmd.Parameters.AddWithValue("@id", requestId);

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return !await reader.ReadAsync(ct) ? null : MapRow(reader);
    }

    private static async Task<List<ApprovalRequest>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct = default)
    {
        var results = new List<ApprovalRequest>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static ApprovalRequest MapRow(SqliteDataReader reader)
    {
        string requestId = reader.GetString(reader.GetOrdinal("RequestId"));
        string? sessionId = TryGetString(reader, "SessionId");
        string? conversationId = TryGetString(reader, "ConversationId");
        string kind = TryGetString(reader, "Kind") ?? ApprovalKind.Tool;
        string? planId = TryGetString(reader, "PlanId");
        int roundNumber = TryGetInt(reader, "RoundNumber") ?? 0;
        string? payload = TryGetString(reader, "Payload");
        string? responsePayload = TryGetString(reader, "ResponsePayload");
        var context = new ApprovalContext(
            AgentId: reader.GetString(reader.GetOrdinal("AgentId")),
            UserId: reader.GetString(reader.GetOrdinal("UserId")),
            Action: reader.GetString(reader.GetOrdinal("Action")),
            Impact: reader.IsDBNull(reader.GetOrdinal("Impact")) ? "" : reader.GetString(reader.GetOrdinal("Impact")),
            Urgency: reader.IsDBNull(reader.GetOrdinal("Urgency")) ? "medium" : reader.GetString(reader.GetOrdinal("Urgency")),
            Reasoning: reader.IsDBNull(reader.GetOrdinal("Reasoning")) ? "" : reader.GetString(reader.GetOrdinal("Reasoning")),
            SessionId: sessionId,
            ConversationId: conversationId,
            Kind: kind,
            PlanId: planId,
            RoundNumber: roundNumber,
            Payload: payload
        );

        string decisionStr = reader.GetString(reader.GetOrdinal("Decision"));
        ApprovalDecision decision = Enum.TryParse(decisionStr, true, out ApprovalDecision d) ? d : ApprovalDecision.Pending;

        string? comment = reader.IsDBNull(reader.GetOrdinal("Comment")) ? null : reader.GetString(reader.GetOrdinal("Comment"));
        var createdAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt")), CultureInfo.InvariantCulture);
        var expiresAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("ExpiresAt")), CultureInfo.InvariantCulture);
        DateTimeOffset? respondedAt = reader.IsDBNull(reader.GetOrdinal("RespondedAt"))
            ? null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("RespondedAt")), CultureInfo.InvariantCulture);
        string? terminalReason = TryGetString(reader, "TerminalReason");

        return new ApprovalRequest(
            requestId, context, createdAt, expiresAt, decision, comment, respondedAt, terminalReason, responsePayload);
    }

    private static string? TryGetString(SqliteDataReader reader, string columnName)
    {
        int ordinal;
        try { ordinal = reader.GetOrdinal(columnName); }
        catch (IndexOutOfRangeException) { return null; }
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? TryGetInt(SqliteDataReader reader, string columnName)
    {
        int ordinal;
        try { ordinal = reader.GetOrdinal(columnName); }
        catch (IndexOutOfRangeException) { return null; }
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}
