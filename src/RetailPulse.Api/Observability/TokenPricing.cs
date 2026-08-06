using Microsoft.Extensions.Configuration;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Observability;

/// <summary>
/// Model-specific token pricing table sourced from the <c>TokenPricing</c>
/// configuration section. Shared by every <see cref="ICostTracker"/>
/// implementation so cost math is identical regardless of the backing store.
/// </summary>
public sealed class TokenPricing
{
    private readonly Dictionary<string, (decimal InputPer1M, decimal OutputPer1M)> _pricing;

    private static readonly (decimal InputPer1M, decimal OutputPer1M) _defaultPricing = (1.00m, 5.00m);

    private TokenPricing(Dictionary<string, (decimal InputPer1M, decimal OutputPer1M)> pricing)
    {
        _pricing = pricing;
    }

    public static TokenPricing FromConfiguration(IConfiguration configuration)
    {
        var pricing = new Dictionary<string, (decimal InputPer1M, decimal OutputPer1M)>(StringComparer.OrdinalIgnoreCase);
        IConfigurationSection section = configuration.GetSection("TokenPricing");

        foreach (IConfigurationSection child in section.GetChildren())
        {
            decimal inputRate = child.GetValue<decimal>("InputPerMillion");
            decimal outputRate = child.GetValue<decimal>("OutputPerMillion");
            pricing[child.Key] = (inputRate, outputRate);
        }

        return new TokenPricing(pricing);
    }

    /// <summary>Cost in USD for a single usage event. Cache hits consume zero new model tokens and cost nothing.</summary>
    public decimal Calculate(UsageEvent usage)
    {
        if (usage.CacheHit || (usage.InputTokens == 0 && usage.OutputTokens == 0))
            return 0m;

        (decimal inputPer1M, decimal outputPer1M) = _pricing.GetValueOrDefault(usage.Model, _defaultPricing);
        decimal inputCost = usage.InputTokens / 1_000_000m * inputPer1M;
        decimal outputCost = usage.OutputTokens / 1_000_000m * outputPer1M;
        return inputCost + outputCost;
    }
}
