using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetailPulse.Api.Data;
using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Api.Alerts;

/// <summary>
/// SQLite-backed alert service — stores alerts, manages throttles and snoozes.
/// Every connection is opened through <see cref="SqliteMount"/>, which applies
/// the centralized SMB-safe pragmas (busy_timeout, DELETE journaling,
/// synchronous=FULL) — the same pattern as SqliteApprovalGate /
/// SqliteConversationMemory; durable on the Azure Files mount, single-writer only
/// (API runs maxReplicas: 1).
/// </summary>
public sealed class SqliteAlertService : IAlertService, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteAlertService> _logger;
    private readonly TimeSpan _defaultThrottleWindow;

    private const string _iso8601 = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    public SqliteAlertService(
        string dbPath,
        ILogger<SqliteAlertService> logger,
        TimeSpan? defaultThrottleWindow = null)
    {
        _logger = logger;
        _defaultThrottleWindow = defaultThrottleWindow ?? TimeSpan.FromHours(1);

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();
    }

    private SqliteConnection OpenConnection() => SqliteMount.Open(_connectionString);

    private void InitializeSchema()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = AlertDbSchema.CreateTables;
        cmd.ExecuteNonQuery();
    }

    // ── IAlertService ────────────────────────────────────────────────────

    /// <summary>
    /// Called by ProactiveAlertService — not used directly. The hosted service
    /// does its own anomaly detection and calls <see cref="PersistAlertAsync"/> directly.
    /// This implementation returns active alerts from the last 24 hours.
    /// </summary>
    public async Task<IReadOnlyList<Alert>> CheckForAlertsAsync(CancellationToken ct = default) => await GetActiveAlertsAsync(ct);

    public Task<IReadOnlyList<Alert>> GetActiveAlertsAsync(CancellationToken ct = default)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-24);

        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Type, Severity, Title, Description, Brand, Region, RecommendedAction, DetectedAt, Metadata
            FROM Alerts
            WHERE DetectedAt >= @cutoff
            ORDER BY DetectedAt DESC
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString(_iso8601, CultureInfo.InvariantCulture));

        List<Alert> alerts = ReadAlerts(cmd);
        return Task.FromResult<IReadOnlyList<Alert>>(alerts);
    }

    public Task SnoozeAsync(string alertType, string userId, TimeSpan duration, CancellationToken ct = default)
    {
        DateTimeOffset snoozedUntil = DateTimeOffset.UtcNow.Add(duration);

        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AlertSnoozes (UserId, AlertType, Brand, Region, SnoozedUntil)
            VALUES (@userId, @alertType, NULL, NULL, @snoozedUntil)
            """;
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@alertType", alertType);
        cmd.Parameters.AddWithValue("@snoozedUntil", snoozedUntil.ToString(_iso8601, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();

        _logger.LogInformation("User {UserId} snoozed {AlertType} until {SnoozedUntil}", userId, alertType, snoozedUntil);
        return Task.CompletedTask;
    }

    public Task DismissAsync(string alertId, string userId, CancellationToken ct = default)
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO AlertDismissals (AlertId, UserId, DismissedAt)
            VALUES (@alertId, @userId, @now)
            """;
        cmd.Parameters.AddWithValue("@alertId", alertId);
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString(_iso8601, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();

        _logger.LogInformation("User {UserId} dismissed alert {AlertId}", userId, alertId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Alert>> GetHistoryAsync(string userId, int limit = 50, CancellationToken ct = default)
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Type, Severity, Title, Description, Brand, Region, RecommendedAction, DetectedAt, Metadata
            FROM Alerts
            ORDER BY DetectedAt DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        List<Alert> alerts = ReadAlerts(cmd);
        return Task.FromResult<IReadOnlyList<Alert>>(alerts);
    }

    // ── Internal methods used by ProactiveAlertService ────────────────────

    /// <summary>Check if a (type, brand, region) combination was fired within the throttle window.</summary>
    internal bool IsThrottled(string type, string brand, string region)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(_defaultThrottleWindow);

        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT LastFiredAt FROM AlertThrottles
            WHERE Type = @type AND Brand = @brand AND Region = @region
            """;
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@brand", brand);
        cmd.Parameters.AddWithValue("@region", region);

        object? result = cmd.ExecuteScalar();
        return result is string lastFiredStr &&
            DateTimeOffset.TryParseExact(lastFiredStr, _iso8601, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset lastFired) && lastFired >= cutoff;
    }

    /// <summary>Record that an alert of this type was just fired (update throttle).</summary>
    internal void UpdateThrottle(string type, string brand, string region)
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO AlertThrottles (Type, Brand, Region, LastFiredAt)
            VALUES (@type, @brand, @region, @now)
            """;
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@brand", brand);
        cmd.Parameters.AddWithValue("@region", region);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString(_iso8601, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Persist a new alert and update its throttle entry.</summary>
    internal void PersistAlert(Alert alert)
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Alerts (Id, Type, Severity, Title, Description, Brand, Region, RecommendedAction, DetectedAt, Metadata)
            VALUES (@id, @type, @severity, @title, @description, @brand, @region, @action, @detectedAt, @metadata)
            """;
        cmd.Parameters.AddWithValue("@id", alert.Id);
        cmd.Parameters.AddWithValue("@type", alert.Type);
        cmd.Parameters.AddWithValue("@severity", alert.Severity);
        cmd.Parameters.AddWithValue("@title", alert.Title);
        cmd.Parameters.AddWithValue("@description", alert.Description);
        cmd.Parameters.AddWithValue("@brand", alert.Brand);
        cmd.Parameters.AddWithValue("@region", alert.Region);
        cmd.Parameters.AddWithValue("@action", alert.RecommendedAction);
        cmd.Parameters.AddWithValue("@detectedAt", alert.DetectedAt.ToString(_iso8601, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@metadata", alert.Metadata is not null ? JsonSerializer.Serialize(alert.Metadata) : DBNull.Value);
        cmd.ExecuteNonQuery();

        UpdateThrottle(alert.Type, alert.Brand, alert.Region);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<Alert> ReadAlerts(SqliteCommand cmd)
    {
        var alerts = new List<Alert>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string? metadataStr = reader.IsDBNull(9) ? null : reader.GetString(9);
            Dictionary<string, object>? metadata = null;
            if (metadataStr is not null)
            {
                try
                {
                    metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataStr);
                }
                catch
                {
                    // ignore malformed metadata
                }
            }

            alerts.Add(new Alert(
                Id: reader.GetString(0),
                Type: reader.GetString(1),
                Severity: reader.GetString(2),
                Title: reader.GetString(3),
                Description: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Brand: reader.IsDBNull(5) ? "" : reader.GetString(5),
                Region: reader.IsDBNull(6) ? "" : reader.GetString(6),
                RecommendedAction: reader.IsDBNull(7) ? "" : reader.GetString(7),
                DetectedAt: DateTimeOffset.TryParseExact(reader.GetString(8), _iso8601, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
                    ? dt : DateTimeOffset.UtcNow,
                Metadata: metadata
            ));
        }
        return alerts;
    }

    public void Dispose() { /* no persistent connections to dispose */ }
}
