namespace RetailPulse.Contracts.Margin;

public record MarginAnalysis(string BrandId, string BrandName, decimal Revenue,
    decimal Cogs, decimal GrossMargin, decimal MarginPercent,
    IReadOnlyList<MarginDriver> Drivers, IReadOnlyList<string> Risks);

public record MarginDriver(string Category, decimal Amount, decimal Impact, string Trend);

public enum EscalationLevel { L1_General, L2_Specialist, L3_Executive }

public record EscalationResult(EscalationLevel Level, string Response,
    string? EscalatedFrom, string? EscalationReason, string[] ContextAdded);
