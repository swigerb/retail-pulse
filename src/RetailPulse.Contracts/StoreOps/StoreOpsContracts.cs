namespace RetailPulse.Contracts.StoreOps;

public record StorePerformance(string StoreId, string StoreName, string Region,
    decimal Revenue, decimal Target, double PerformanceIndex, string[] Issues);

public record PlanogramSlot(int ShelfLevel, int Position, string SkuId, string SkuName,
    string Brand, double FacingWidth, double UpliftPrediction);

public record Planogram(string AisleId, string AisleName, IReadOnlyList<PlanogramSlot> Slots,
    double PredictedUplift, string[] OptimizationNotes);

public record StockoutPrediction(string SkuId, string SkuName, string StoreId,
    int DaysUntilStockout, double CurrentVelocity, double SafetyStock);
