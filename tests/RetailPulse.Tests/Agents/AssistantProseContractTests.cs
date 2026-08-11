using FluentAssertions;
using RetailPulse.Api.Agents;
using Xunit;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Regression coverage for issue #74 P0 failure #2 — the assistant's final
/// user-visible prose must not contain fallback/truncation vocabulary when a
/// valid roster-complete chart was in fact produced. Publix production commit
/// 5c90ff7 shipped the correct chart but the model narrated "truncated" +
/// "fallback" language into the reply, causing the frontend to display the
/// chart as if it were a shell. The pipeline now strips that vocabulary at
/// the fulfillment layer — this test pins the contract on the actual runtime
/// helper, not on a diagnostic string constant.
/// </summary>
public sealed class AssistantProseContractTests
{
    [Theory]
    [InlineData("The dataset was truncated so I fell back to a placeholder chart.")]
    [InlineData("Falling back to a chart shell because the data is unavailable.")]
    [InlineData("This is a chart shell with placeholder zeros and should not be used for decisions.")]
    [InlineData("I was unable to rank the portfolio, so this is a fallback.")]
    public void StripFallbackClaims_RemovesBannedVocabularySentences(string reply)
    {
        string cleaned = AgentExecutionPipeline.StripFallbackClaims(reply);

        foreach (string phrase in new[]
                 {
                     "truncated", "placeholder zero", "should not be used",
                     "chart shell", "unable to rank", "falling back", "fallback",
                 })
        {
            cleaned.Should().NotContainEquivalentOf(phrase,
                $"the final assistant message must never carry '{phrase}' when a valid chart is produced");
        }
    }

    [Fact]
    public void StripFallbackClaims_PreservesLegitimateProseWhenNoBannedTokens()
    {
        const string legit = "Here is the requested ranking of all 12 tenant brands by depletion growth rate.";
        AgentExecutionPipeline.StripFallbackClaims(legit).Should().Be(legit);
    }

    [Fact]
    public void StripFallbackClaims_ReplacesFullyBannedReplyWithNeutralConfirmation()
    {
        const string allFallback = "truncated. fallback. chart shell.";
        string cleaned = AgentExecutionPipeline.StripFallbackClaims(allFallback);

        cleaned.Should().NotContainEquivalentOf("truncated");
        cleaned.Should().NotContainEquivalentOf("fallback");
        cleaned.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void StripFallbackClaims_HandlesNullAndEmpty()
    {
        AgentExecutionPipeline.StripFallbackClaims(null).Should().NotBeNullOrWhiteSpace();
        AgentExecutionPipeline.StripFallbackClaims("").Should().NotBeNullOrWhiteSpace();
    }
}
