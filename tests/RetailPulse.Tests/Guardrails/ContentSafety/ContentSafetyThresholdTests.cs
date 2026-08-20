using FluentAssertions;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A2 — per-category severity threshold semantics. A severity greater than or
/// equal to the configured threshold is a block; below is a pass or flag; an
/// unknown category resolves to <see cref="int.MaxValue"/> so it can never
/// accidentally block.
/// </summary>
public class ContentSafetyThresholdTests
{
    [Theory]
    [InlineData("Hate", 4, 4, true)]
    [InlineData("Hate", 4, 6, true)]
    [InlineData("Hate", 4, 2, false)]
    [InlineData("Sexual", 6, 4, false)]
    [InlineData("Violence", 2, 2, true)]
    [InlineData("SelfHarm", 4, 0, false)]
    public void Resolve_MatchesConfiguredThreshold(string category, int threshold, int severity, bool shouldBlock)
    {
        var thresholds = new ContentSafetyCategoryThresholds
        {
            Hate = category == "Hate" ? threshold : 4,
            Sexual = category == "Sexual" ? threshold : 4,
            Violence = category == "Violence" ? threshold : 4,
            SelfHarm = category == "SelfHarm" ? threshold : 4,
        };

        int configured = thresholds.Resolve(category);
        (severity >= configured).Should().Be(shouldBlock);
    }

    [Fact]
    public void Resolve_UnknownCategory_ReturnsIntMaxValue()
    {
        var thresholds = new ContentSafetyCategoryThresholds();
        thresholds.Resolve("Politics").Should().Be(int.MaxValue);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var thresholds = new ContentSafetyCategoryThresholds { Hate = 2 };
        thresholds.Resolve("HATE").Should().Be(2);
        thresholds.Resolve("hate").Should().Be(2);
    }
}
