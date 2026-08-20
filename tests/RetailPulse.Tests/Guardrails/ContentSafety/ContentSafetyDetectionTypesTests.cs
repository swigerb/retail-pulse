using FluentAssertions;
using RetailPulse.Api.Guardrails.ContentSafety;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

public class ContentSafetyDetectionTypesTests
{
    [Theory]
    [InlineData("Hate", ContentSafetyDetectionTypes.Hate)]
    [InlineData("hate", ContentSafetyDetectionTypes.Hate)]
    [InlineData("Sexual", ContentSafetyDetectionTypes.Sexual)]
    [InlineData("Violence", ContentSafetyDetectionTypes.Violence)]
    [InlineData("SelfHarm", ContentSafetyDetectionTypes.SelfHarm)]
    public void ForCategory_MapsKnownCategories(string category, string expected) => ContentSafetyDetectionTypes.ForCategory(category).Should().Be(expected);

    [Theory]
    [InlineData(ContentSafetyDetectionTypes.Hate, true)]
    [InlineData(ContentSafetyDetectionTypes.PromptShield, true)]
    [InlineData(ContentSafetyDetectionTypes.IndirectInjection, true)]
    [InlineData(ContentSafetyDetectionTypes.Unavailable, true)]
    [InlineData("jailbreak", false)]
    [InlineData("pii", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsContentSafety_MatchesPrefixOnly(string? detectionType, bool expected) => ContentSafetyDetectionTypes.IsContentSafety(detectionType).Should().Be(expected);
}
