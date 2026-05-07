using FluentAssertions;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.McpServer.Tools;
using System.Text.Json;

namespace RetailPulse.Tests;

public class UpdateMetricsToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public UpdateMetricsToolTests()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_test_{Guid.NewGuid():N}.db");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { File.Delete(_dbPath); } catch { /* file may be locked by WAL */ }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static string ToJson(object obj) => JsonSerializer.Serialize(obj);
    private static JsonElement Parse(object obj) => JsonDocument.Parse(ToJson(obj)).RootElement;

    [Fact]
    public void UpdateMetrics_CanUpdateSentimentValue_SocialCrisisScenario()
    {
        var result = Parse(UpdateMetricsTool.UpdateMetrics(_db, "Sentiment", "FreshMart", "Pacific Northwest", "Sentiment", "15"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("field").GetString().Should().Be("Sentiment");
        result.GetProperty("new_value").GetString().Should().Be("15");
    }

    [Fact]
    public void UpdateMetrics_CanUpdateDepletionsField()
    {
        var result = Parse(UpdateMetricsTool.UpdateMetrics(_db, "Depletions", "FreshMart", "Northeast", "DepletionsYoY", "+18.5%"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("field").GetString().Should().Be("DepletionsYoY");
        result.GetProperty("new_value").GetString().Should().Be("+18.5%");
    }

    [Fact]
    public void UpdateMetrics_CanUpdateShipmentField_SupplyChainRerouteScenario()
    {
        var result = Parse(UpdateMetricsTool.UpdateMetrics(_db, "Shipments", "FreshMart", "Northeast", "ShipmentsYoY", "0"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("field").GetString().Should().Be("ShipmentsYoY");
        result.GetProperty("new_value").GetString().Should().Be("0");
    }

    [Fact]
    public void UpdateMetrics_RejectsInvalidTableName()
    {
        var result = Parse(UpdateMetricsTool.UpdateMetrics(_db, "InvalidTable", "FreshMart", "Northeast", "DepletionsYoY", "10"));

        result.GetProperty("error").GetString().Should().Contain("Invalid table");
    }

    [Fact]
    public void UpdateMetrics_RejectsInvalidFieldName()
    {
        var result = Parse(UpdateMetricsTool.UpdateMetrics(_db, "Depletions", "FreshMart", "Northeast", "FakeField", "10"));

        result.GetProperty("error").GetString().Should().Contain("Invalid field");
    }

    [Fact]
    public void UpdateMetrics_RoundTrip_GetReturnsUpdatedSentiment()
    {
        UpdateMetricsTool.UpdateMetrics(_db, "Sentiment", "FreshMart", "Pacific Northwest", "Sentiment", "15");

        var readResult = Parse(_db.GetFieldSentiment("FreshMart", "Pacific Northwest"));

        readResult.GetProperty("sentiment").GetString().Should().Be("15");
    }

    [Fact]
    public void UpdateMetrics_RoundTrip_GetReturnsUpdatedDepletions()
    {
        UpdateMetricsTool.UpdateMetrics(_db, "Depletions", "FreshMart", "Northeast", "DepletionsYoY", "+99.9%");

        var readResult = Parse(_db.GetDepletionStats("FreshMart", "Northeast", "YoY"));

        readResult.GetProperty("metrics").GetProperty("depletions_yoy").GetString().Should().Be("+99.9%");
    }
}

