using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Data;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Observability;

/// <summary>
/// SQLite-backed cost tracker whose history survives process restarts and — when
/// the data directory is a mounted Azure Files share — real Azure Container Apps
/// replica replacement and scale-to-zero. The DB lives in the shared writable
/// data directory alongside audit.db / memory.db (see <see cref="DataDirectoryResolver"/>).
/// <para>
/// Durability across replica replacement depends entirely on the data directory
/// being persistent. In deployed ACA that is the Azure Files mount at
/// <c>/mnt/retailpulse-data</c>; in local development it is an ephemeral temp
/// directory. The store itself is single-writer safe only (the API runs
/// <c>maxReplicas: 1</c>) and uses SMB-safe rollback journaling — see
/// <see cref="SqliteMount"/>.
/// </para>
/// <para>
/// Bounded like the in-memory tracker: on every write, events older than the
/// configured TTL are pruned and the row count is capped at MaxCostEvents by
/// deleting the oldest rows. All DB access is serialized through a semaphore
/// because a single <see cref="SqliteConnection"/> is not safe for concurrent
/// commands.
/// </para>
/// </summary>
public sealed class DurableCostTracker : ICostTracker, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ObservabilityOptions _options;
    private readonly TokenPricing _pricing;

    public DurableCostTracker(string dbPath, IOptions<ObservabilityOptions> options, IConfiguration configuration)
    {
        _options = options.Value;
        _pricing = TokenPricing.FromConfiguration(configuration);

        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        SqliteMount.ApplySmbSafePragmas(_connection);
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cost_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id TEXT NOT NULL,
                model TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                tool_name TEXT NULL,
                timestamp TEXT NOT NULL,
                is_cache_hit INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_cost_timestamp ON cost_events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_cost_agent ON cost_events(agent_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task TrackUsageAsync(UsageEvent usage, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using (SqliteCommand cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO cost_events (agent_id, model, input_tokens, output_tokens, tool_name, timestamp, is_cache_hit)
                    VALUES (@agent, @model, @in, @out, @tool, @ts, @cache)
                    """;
                cmd.Parameters.AddWithValue("@agent", usage.AgentId);
                cmd.Parameters.AddWithValue("@model", usage.Model);
                cmd.Parameters.AddWithValue("@in", usage.InputTokens);
                cmd.Parameters.AddWithValue("@out", usage.OutputTokens);
                cmd.Parameters.AddWithValue("@tool", (object?)usage.ToolName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", usage.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@cache", usage.CacheHit ? 1 : 0);
                cmd.ExecuteNonQuery();
            }

            PruneUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Evict TTL-expired rows and cap total rows. Caller must hold the gate.</summary>
    private void PruneUnsafe()
    {
        DateTime cutoff = DateTime.UtcNow.AddHours(-_options.CostEventTtlHours);
        using (SqliteCommand ttl = _connection.CreateCommand())
        {
            ttl.CommandText = "DELETE FROM cost_events WHERE timestamp < @cutoff";
            ttl.Parameters.AddWithValue("@cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
            ttl.ExecuteNonQuery();
        }

        using SqliteCommand cap = _connection.CreateCommand();
        cap.CommandText = """
            DELETE FROM cost_events
            WHERE id NOT IN (
                SELECT id FROM cost_events ORDER BY id DESC LIMIT @max
            )
            """;
        cap.Parameters.AddWithValue("@max", _options.MaxCostEvents);
        cap.ExecuteNonQuery();
    }

    public async Task<CostSummary> GetSummaryAsync(CostPeriod period, CancellationToken ct = default)
    {
        List<UsageEvent> filtered = await QueryByPeriodAsync(period, ct);
        int totalTokens = filtered.Sum(e => e.InputTokens + e.OutputTokens);
        decimal totalCost = filtered.Sum(e => _pricing.Calculate(e));
        return new CostSummary(totalTokens, totalCost, filtered.Count, period);
    }

    public async Task<IReadOnlyList<AgentCostBreakdown>> GetByAgentAsync(CostPeriod period, CancellationToken ct = default)
    {
        List<UsageEvent> filtered = await QueryByPeriodAsync(period, ct);
        var grouped = filtered
            .GroupBy(e => e.AgentId)
            .Select(g =>
            {
                int tokens = g.Sum(e => e.InputTokens + e.OutputTokens);
                decimal cost = g.Sum(e => _pricing.Calculate(e));
                string topTool = g
                    .Where(e => e.ToolName != null)
                    .GroupBy(e => e.ToolName)
                    .OrderByDescending(tg => tg.Count())
                    .Select(tg => tg.Key)
                    .FirstOrDefault() ?? "none";

                return new AgentCostBreakdown(g.Key, tokens, cost, g.Count(), topTool);
            })
            .OrderByDescending(a => a.Cost)
            .ToList();

        return grouped;
    }

    public async Task<CostTrend> GetTrendAsync(int days = 7, CancellationToken ct = default)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        List<UsageEvent> events = await QueryFromAsync(cutoff, ct);

        var dailyCosts = Enumerable.Range(0, days)
            .Select(i =>
            {
                DateTime date = DateTime.UtcNow.Date.AddDays(-days + 1 + i);
                var dayEvents = events.Where(e => e.Timestamp.Date == date).ToList();
                decimal cost = dayEvents.Sum(e => _pricing.Calculate(e));
                int tokens = dayEvents.Sum(e => e.InputTokens + e.OutputTokens);
                return new DailyCost(date, cost, tokens);
            })
            .ToList();

        return new CostTrend(dailyCosts);
    }

    private Task<List<UsageEvent>> QueryByPeriodAsync(CostPeriod period, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        DateTime cutoff = period switch
        {
            CostPeriod.Today => now.Date,
            CostPeriod.Week => now.AddDays(-7),
            CostPeriod.Month => now.AddDays(-30),
            CostPeriod.All => DateTime.MinValue,
            _ => DateTime.MinValue
        };
        return QueryFromAsync(cutoff, ct);
    }

    private async Task<List<UsageEvent>> QueryFromAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT agent_id, model, input_tokens, output_tokens, tool_name, timestamp, is_cache_hit
                FROM cost_events
                WHERE timestamp >= @cutoff
                """;
            cmd.Parameters.AddWithValue("@cutoff", cutoffUtc.ToString("O", CultureInfo.InvariantCulture));

            var results = new List<UsageEvent>();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new UsageEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt32(6) != 0));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _connection.Dispose();
    }
}
