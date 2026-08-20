using System.Reflection;
using FluentAssertions;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A10 — configuration contract. There must be no ApiKey / Key / SecretKey
/// property on <see cref="ContentSafetyConfig"/> — the layer authenticates via
/// managed identity only. Enforced by reflection so a future contributor
/// cannot regress this without also updating this test.
/// </summary>
public class ContentSafetyContractTests
{
    [Fact]
    public void ContentSafetyConfig_HasNoKeyLikeMember()
    {
        PropertyInfo[] props = typeof(ContentSafetyConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        props.Select(p => p.Name).Should().NotContain(
            n =>
                n.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                || n.Contains("SecretKey", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Key", StringComparison.OrdinalIgnoreCase),
            "Content Safety must never accept a static key — managed identity only.");
    }

    [Fact]
    public void ContentSafetyConfig_DefaultIsDisabled()
    {
        var cfg = new ContentSafetyConfig();
        cfg.Enabled.Should().BeFalse("issue #100 mandates disabled-by-default");
    }

    [Fact]
    public void ContentSafetyConfig_DefaultOnUnavailableIsFailOpen()
    {
        var cfg = new ContentSafetyConfig();
        cfg.OnUnavailable.Should().Be(ContentSafetyFailPolicy.FailOpen,
            "regulated environments must opt in to FailClosed explicitly");
    }

    [Fact]
    public void GuardrailsStats_ExposesContentSafetyCounters()
    {
        var stats = new GuardrailsStats(
            TotalBlocked: 0, JailbreakAttempts: 0, PiiDetections: 0,
            AccessDenials: 0, Since: DateTime.UtcNow);

        stats.ContentSafetyBlocks.Should().Be(0);
        stats.ContentSafetyFlags.Should().Be(0);
    }
}
