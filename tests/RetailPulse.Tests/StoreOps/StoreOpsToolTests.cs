using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;

namespace RetailPulse.Tests.StoreOps;

/// <summary>
/// Tests for store operations MCP tool methods on RetailPulseDb:
/// get_store_performance, get_shelf_layout, predict_stockout.
/// Uses a real SQLite DB with seeded data from tenant.yaml.
/// Test-first: defines expected contracts before Phase 4.1 implementation.
/// </summary>
public class StoreOpsToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public StoreOpsToolTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_storeops_test_{Guid.NewGuid():N}.db");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static JsonElement Parse(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    #region get_store_performance

    [Fact]
    public void GetStorePerformance_ReturnsStoresWithValidPerformanceIndex()
    {
        JsonElement result = Parse(_db.GetStorePerformance());

        result.TryGetProperty("error", out _).Should().BeFalse("should return store performance data");
        JsonElement stores = result.GetProperty("stores");
        stores.GetArrayLength().Should().BeGreaterThan(0, "should return at least one store");

        foreach (JsonElement store in stores.EnumerateArray())
        {
            store.GetProperty("storeId").GetString().Should().NotBeNullOrEmpty();
            double perfIndex = store.GetProperty("performanceIndex").GetDouble();
            perfIndex.Should().BeGreaterThan(0, "performance index should be positive");
            perfIndex.Should().BeLessThan(5.0, "performance index should be reasonable (< 5x target)");
        }
    }

    [Theory]
    [InlineData("Northeast")]
    [InlineData("Southeast")]
    [InlineData("Midwest")]
    [InlineData("Southwest")]
    public void GetStorePerformance_FiltersByRegion(string region)
    {
        JsonElement result = Parse(_db.GetStorePerformance(region: region));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement stores = result.GetProperty("stores");
        stores.GetArrayLength().Should().BeGreaterThan(0,
            $"should return stores for region '{region}'");

        foreach (JsonElement store in stores.EnumerateArray())
        {
            store.GetProperty("region").GetString().Should().Be(region,
                $"all stores should be in region '{region}'");
        }
    }

    [Fact]
    public void GetStorePerformance_PerformanceIndex_IsRevenueOverTargetRatio()
    {
        JsonElement result = Parse(_db.GetStorePerformance());
        JsonElement stores = result.GetProperty("stores");

        foreach (JsonElement store in stores.EnumerateArray())
        {
            double revenue = store.GetProperty("revenue").GetDouble();
            double target = store.GetProperty("target").GetDouble();
            double perfIndex = store.GetProperty("performanceIndex").GetDouble();

            target.Should().BeGreaterThan(0, "target should be positive");
            double expectedIndex = revenue / target;
            perfIndex.Should().BeApproximately(expectedIndex, 0.01,
                "performanceIndex should equal revenue / target");
        }
    }

    [Fact]
    public void GetStorePerformance_InvalidRegion_ReturnsEmptyOrError()
    {
        JsonElement result = Parse(_db.GetStorePerformance(region: "Narnia"));

        // Either returns empty stores array or an error
        if (result.TryGetProperty("stores", out JsonElement stores))
        {
            stores.GetArrayLength().Should().Be(0, "invalid region should return no stores");
        }
        else
        {
            result.TryGetProperty("error", out _).Should().BeTrue();
        }
    }

    #endregion

    #region get_shelf_layout

    [Fact]
    public void GetShelfLayout_ValidAisle_ReturnsCorrectLayout()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.GetShelfLayout(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse(
            $"should return shelf layout for aisle '{aisleId}'");

        JsonElement slots = result.GetProperty("slots");
        slots.GetArrayLength().Should().BeGreaterThan(0, "aisle should have slots");

        foreach (JsonElement slot in slots.EnumerateArray())
        {
            slot.GetProperty("shelfLevel").ValueKind.Should().NotBe(JsonValueKind.Undefined);
            slot.GetProperty("position").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        }
    }

    [Fact]
    public void GetShelfLayout_InvalidAisle_ReturnsEmptyOrError()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        JsonElement result = Parse(_db.GetShelfLayout(storeId, "ZZ99"));

        // Either returns empty slots or an error object
        if (result.TryGetProperty("slots", out JsonElement slots))
        {
            slots.GetArrayLength().Should().Be(0,
                "invalid aisle should return no slots");
        }
        else
        {
            result.TryGetProperty("error", out _).Should().BeTrue();
        }
    }

    [Fact]
    public void GetShelfLayout_SlotsHaveSkuAndFacingWidth()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.GetShelfLayout(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement slots = result.GetProperty("slots");

        foreach (JsonElement slot in slots.EnumerateArray())
        {
            slot.GetProperty("skuId").GetString().Should().NotBeNullOrEmpty(
                "each slot should have a SKU identifier");
            slot.GetProperty("facingWidth").GetDouble().Should().BeGreaterThan(0,
                "each slot should have a positive facing width");
        }
    }

    #endregion

    #region predict_stockout

    [Fact]
    public void PredictStockout_HasValidDaysUntilStockout()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        JsonElement result = Parse(_db.PredictStockout(storeId));

        result.TryGetProperty("error", out _).Should().BeFalse("should return stockout predictions");
        JsonElement predictions = result.GetProperty("predictions");
        predictions.GetArrayLength().Should().BeGreaterThan(0);

        foreach (JsonElement prediction in predictions.EnumerateArray())
        {
            int daysUntilStockout = prediction.GetProperty("daysUntilStockout").GetInt32();
            daysUntilStockout.Should().BeGreaterThanOrEqualTo(0,
                "daysUntilStockout should be non-negative");
        }
    }

    [Fact]
    public void PredictStockout_ReturnsSortedByVelocityDescending()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        JsonElement result = Parse(_db.PredictStockout(storeId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement predictions = result.GetProperty("predictions");
        int count = predictions.GetArrayLength();

        if (count > 1)
        {
            var velocities = new List<double>();
            foreach (JsonElement p in predictions.EnumerateArray())
            {
                velocities.Add(p.GetProperty("currentVelocity").GetDouble());
            }

            velocities.Should().BeInDescendingOrder(
                "predictions should be sorted by velocity descending (highest first)");
        }
    }

    [Fact]
    public void PredictStockout_EachPredictionHasRequiredFields()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        JsonElement result = Parse(_db.PredictStockout(storeId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement predictions = result.GetProperty("predictions");

        foreach (JsonElement p in predictions.EnumerateArray())
        {
            p.GetProperty("skuId").GetString().Should().NotBeNullOrEmpty();
            p.GetProperty("daysUntilStockout").GetInt32().Should().BeGreaterThanOrEqualTo(0);
            p.GetProperty("currentVelocity").GetDouble().Should().BeGreaterThan(0);
            p.GetProperty("riskLevel").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void PredictStockout_FiltersBySku_ReturnsOnlyThatSku()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        // Get all predictions first to find a valid SKU
        JsonElement allResult = Parse(_db.PredictStockout(storeId));
        JsonElement allPredictions = allResult.GetProperty("predictions");
        if (allPredictions.GetArrayLength() == 0) return;

        string? firstSku = allPredictions[0].GetProperty("skuId").GetString();
        JsonElement filteredResult = Parse(_db.PredictStockout(storeId, skuId: firstSku));
        JsonElement filtered = filteredResult.GetProperty("predictions");

        foreach (JsonElement p in filtered.EnumerateArray())
        {
            p.GetProperty("skuId").GetString().Should().Be(firstSku,
                "filtered results should only contain the requested SKU");
        }
    }

    #endregion

    #region Helpers

    private string? GetFirstStoreId()
    {
        JsonElement result = Parse(_db.GetStorePerformance());
        return result.TryGetProperty("stores", out JsonElement stores) && stores.GetArrayLength() > 0
            ? stores[0].GetProperty("storeId").GetString()
            : null;
    }

    private string? GetFirstAisleId(string storeId)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT AisleId FROM ShelfLayouts WHERE StoreId = @storeId LIMIT 1";
        cmd.Parameters.AddWithValue("@storeId", storeId);
        object? result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    #endregion
}
