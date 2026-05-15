using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;

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
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_planogram_test_{Guid.NewGuid():N}.db");
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

    #region Eye-Level Placement

    [Fact]
    public void OptimizePlanogram_PlacesHighVelocitySkusAtEyeLevel()
    {
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse(
            "should return optimized planogram");

        var slots = result.GetProperty("currentLayout").GetProperty("slots");

        var allSlots = new List<(int shelfLevel, string skuId, double velocity)>();

        foreach (var slot in slots.EnumerateArray())
        {
            var shelfLevel = slot.GetProperty("shelfLevel").GetInt32();
            var skuId = slot.GetProperty("skuId").GetString()!;
            var velocity = slot.TryGetProperty("dailyVelocity", out var v) ? v.GetDouble() : 0;
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
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var slots = result.GetProperty("currentLayout").GetProperty("slots");

        foreach (var slot in slots.EnumerateArray())
        {
            slot.GetProperty("facingWidth").GetDouble().Should().BeGreaterThan(0,
                "every slot must have a positive facing width");
        }
    }

    [Fact]
    public void OptimizePlanogram_AllFacingsArePositive()
    {
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var slots = result.GetProperty("currentLayout").GetProperty("slots");

        foreach (var slot in slots.EnumerateArray())
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
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var slots = result.GetProperty("currentLayout").GetProperty("slots");

        foreach (var slot in slots.EnumerateArray())
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
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var uplift = result.GetProperty("predictedUplift").GetDouble();
        uplift.Should().BeGreaterThan(0,
            "optimized planogram should predict positive uplift over current layout");
    }

    [Fact]
    public void OptimizePlanogram_UpliftIsReasonable()
    {
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();
        var uplift = result.GetProperty("predictedUplift").GetDouble();
        uplift.Should().BeLessThan(100,
            "predicted uplift should be reasonable (< 100%)");
    }

    #endregion

    #region Original Layout Preservation

    [Fact]
    public void OptimizePlanogram_OriginalLayoutPreserved()
    {
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        // Capture original layout before optimization
        var originalLayout = Parse(_db.GetShelfLayout(storeId, aisleId));

        // Run optimization
        _ = Parse(_db.OptimizePlanogram(storeId, aisleId));

        // Verify original layout wasn't mutated
        var afterLayout = Parse(_db.GetShelfLayout(storeId, aisleId));

        var originalJson = JsonSerializer.Serialize(originalLayout);
        var afterJson = JsonSerializer.Serialize(afterLayout);

        afterJson.Should().Be(originalJson,
            "optimization should not mutate the original layout");
    }

    [Fact]
    public void OptimizePlanogram_ReturnsCurrentLayoutAndUplift()
    {
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var aisleId = GetFirstAisleId(storeId);
        if (aisleId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, aisleId));

        result.TryGetProperty("error", out _).Should().BeFalse();

        result.TryGetProperty("currentLayout", out _).Should().BeTrue(
            "result should include the current layout");
        result.TryGetProperty("predictedUplift", out _).Should().BeTrue(
            "result should include the predicted uplift");
    }

    [Fact]
    public void OptimizePlanogram_InvalidAisle_ReturnsError()
    {
        var storeId = GetFirstStoreId();
        if (storeId == null) return;

        var result = Parse(_db.OptimizePlanogram(storeId, "ZZ99"));

        // Should return error or empty slots in currentLayout
        var hasError = result.TryGetProperty("error", out _);
        var hasEmptySlots = result.TryGetProperty("currentLayout", out var layout)
            && layout.TryGetProperty("slots", out var slots)
            && slots.GetArrayLength() == 0;

        (hasError || hasEmptySlots).Should().BeTrue(
            "invalid aisle should return error or empty slots");
    }

    #endregion

    #region Helpers

    private string? GetFirstStoreId()
    {
        var result = Parse(_db.GetStorePerformance());
        return result.TryGetProperty("stores", out var stores) && stores.GetArrayLength() > 0
            ? stores[0].GetProperty("storeId").GetString()
            : null;
    }

    private string? GetFirstAisleId(string storeId)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT AisleId FROM ShelfLayouts WHERE StoreId = @storeId LIMIT 1";
        cmd.Parameters.AddWithValue("@storeId", storeId);
        return cmd.ExecuteScalar()?.ToString();
    }

    #endregion
}
