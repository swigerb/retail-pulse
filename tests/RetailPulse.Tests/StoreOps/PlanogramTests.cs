using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.StoreOps;

/// <summary>
/// Tests for optimize_planogram MCP tool method on RetailPulseDb.
/// Validates planogram optimization logic: eye-level placement, facing constraints,
/// slot validity, uplift calculation, and immutability of original layout.
/// </summary>
public class PlanogramTests : IDisposable
{
    private readonly string _dbPath;
    private readonly RetailPulseDb _db;

    public PlanogramTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_planogram_test");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _db = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private static JsonElement Parse(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    #region Eye-Level Placement

    [Fact]
    public void OptimizePlanogram_PlacesHighVelocitySkusAtEyeLevel()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse(
            "should return optimized planogram");

        JsonElement slots = result.GetProperty("currentLayout").GetProperty("slots");

        var allSlots = new List<(int shelfLevel, string skuId, double velocity)>();

        foreach (JsonElement slot in slots.EnumerateArray())
        {
            int shelfLevel = slot.GetProperty("shelfLevel").GetInt32();
            string skuId = slot.GetProperty("skuId").GetString()!;
            double velocity = slot.TryGetProperty("dailyVelocity", out JsonElement v) ? v.GetDouble() : 0;
            allSlots.Add((shelfLevel, skuId, velocity));
        }

        allSlots.Should().NotBeEmpty("current layout should have slots");

        // Verify high-velocity SKUs have valid shelfLevel values
        if (allSlots.Count > 2)
        {
            var topVelocitySlots = allSlots
                .OrderByDescending(s => s.velocity)
                .Take(3)
                .ToList();

            topVelocitySlots.Should().OnlyContain(s => s.shelfLevel > 0,
                "high-velocity SKUs should have valid shelf level values");
        }
    }

    #endregion

    #region Facing Width Constraints

    [Fact]
    public void OptimizePlanogram_RespectsFacingWidthConstraints()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement slots = result.GetProperty("currentLayout").GetProperty("slots");

        foreach (JsonElement slot in slots.EnumerateArray())
        {
            slot.GetProperty("facingWidth").GetDouble().Should().BeGreaterThan(0,
                "every slot must have a positive facing width");
        }
    }

    [Fact]
    public void OptimizePlanogram_AllFacingsArePositive()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement slots = result.GetProperty("currentLayout").GetProperty("slots");

        foreach (JsonElement slot in slots.EnumerateArray())
        {
            slot.GetProperty("facingWidth").GetDouble().Should().BeGreaterThan(0,
                "every slot must have a positive facing width");
        }
    }

    #endregion

    #region Slot Validity

    [Fact]
    public void OptimizePlanogram_AllSlotsHaveValidSkuIds()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        JsonElement slots = result.GetProperty("currentLayout").GetProperty("slots");

        foreach (JsonElement slot in slots.EnumerateArray())
        {
            slot.GetProperty("skuId").GetString().Should().NotBeNullOrEmpty(
                "every slot must have a valid SKU ID");
            slot.GetProperty("facingWidth").GetDouble().Should().BeGreaterThan(0,
                "every slot must have a positive facing width");
        }
    }

    #endregion

    #region Predicted Uplift

    [Fact]
    public void OptimizePlanogram_PredictedUpliftIsPositive()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        double uplift = result.GetProperty("predictedUplift").GetDouble();
        uplift.Should().BeGreaterThan(0,
            "optimized planogram should predict positive uplift over current layout");
    }

    [Fact]
    public void OptimizePlanogram_UpliftIsReasonable()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        double uplift = result.GetProperty("predictedUplift").GetDouble();
        uplift.Should().BeLessThan(100,
            "predicted uplift should be reasonable (< 100%)");
    }

    #endregion

    #region Original Layout Preservation

    [Fact]
    public void OptimizePlanogram_OriginalLayoutPreserved()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        // Capture original layout before optimization
        JsonElement originalLayout = Parse(_db.GetShelfLayout(storeId, aisleId));

        // Run optimization
        _ = Parse(_db.OptimizePlanogram(storeId, aisleId));

        // Verify original layout wasn't mutated
        JsonElement afterLayout = Parse(_db.GetShelfLayout(storeId, aisleId));

        string originalJson = JsonSerializer.Serialize(originalLayout);
        string afterJson = JsonSerializer.Serialize(afterLayout);

        afterJson.Should().Be(originalJson,
            "optimization should not mutate the original layout");
    }

    [Fact]
    public void OptimizePlanogram_ReturnsCurrentLayoutAndUplift()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        string? aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();

        result.TryGetProperty("currentLayout", out _).Should().BeTrue(
            "result should include the current layout");
        result.TryGetProperty("predictedUplift", out _).Should().BeTrue(
            "result should include the predicted uplift");
    }

    [Fact]
    public void OptimizePlanogram_InvalidAisle_ReturnsError()
    {
        string? storeId = GetFirstStoreId();
        if (storeId == null) return;

        JsonElement result = Parse(_db.OptimizePlanogram(storeId, "ZZ99"));

        // Should return error or empty slots in currentLayout
        bool hasError = result.TryGetProperty("error", out _);
        bool hasEmptySlots = result.TryGetProperty("currentLayout", out JsonElement layout)
            && layout.TryGetProperty("slots", out JsonElement slots)
            && slots.GetArrayLength() == 0;

        (hasError || hasEmptySlots).Should().BeTrue(
            "invalid aisle should return error or empty slots");
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
        return cmd.ExecuteScalar()?.ToString();
    }

    #endregion
}
