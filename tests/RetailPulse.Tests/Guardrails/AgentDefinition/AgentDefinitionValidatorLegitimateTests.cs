using FluentAssertions;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Contracts.Guardrails;
using AgentDefinition = RetailPulse.Api.Models.AgentDefinition;
using PromptConfiguration = RetailPulse.Api.Models.PromptConfiguration;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// L1 — legitimate definitions from the shipped prompts.yaml must pass in
/// every supported policy combination. Guards against over-eager pattern
/// additions or too-tight structural bounds.
/// </summary>
public class AgentDefinitionValidatorLegitimateTests
{
    public static IEnumerable<object[]> AllModes()
    {
        foreach (AgentDefinitionFailurePolicy policy in new[]
        {
            AgentDefinitionFailurePolicy.RefuseStartup,
            AgentDefinitionFailurePolicy.QuarantineOffender,
        })
        {
            foreach (bool safetyChecks in new[] { true, false })
            {
                foreach (bool contentSafety in new[] { true, false })
                {
                    yield return new object[] { policy, safetyChecks, contentSafety };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task BenignCorpus_IsAccepted_UnderEveryModeCombination(
        AgentDefinitionFailurePolicy failurePolicy,
        bool safetyChecksEnabled,
        bool contentSafetyEnabled)
    {
        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig(
            failurePolicy: failurePolicy,
            safetyChecksEnabled: safetyChecksEnabled,
            contentSafetyEnabled: contentSafetyEnabled);
        (AgentDefinitionValidator validator, Api.Guardrails.InMemorySuspiciousRequestLog audit,
            _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.BenignConfig();
        int startingCount = promptConfig.Agents.Count;

        AgentDefinitionValidationReport report = await validator.ValidateAsync(promptConfig);

        report.Violations.Should().BeEmpty();
        report.QuarantinedAgentKeys.Should().BeEmpty();
        promptConfig.Agents.Should().HaveCount(startingCount);
        (await audit.GetRecentAsync(50)).Should().BeEmpty();
    }
}
