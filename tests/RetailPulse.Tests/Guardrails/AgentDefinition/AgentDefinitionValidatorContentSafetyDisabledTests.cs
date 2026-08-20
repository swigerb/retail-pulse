using FluentAssertions;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Tests.Guardrails.ContentSafety;
using AgentDefinition = RetailPulse.Api.Models.AgentDefinition;
using PromptConfiguration = RetailPulse.Api.Models.PromptConfiguration;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// D1 — Content-Safety-disabled path. Structural + pattern verdict is
/// authoritative. A plain-text jailbreak still rejects; an encoded payload
/// documents the honest limit — it passes on the disabled path because the
/// regex layer cannot decode arbitrary payloads.
/// </summary>
public class AgentDefinitionValidatorContentSafetyDisabledTests
{
    [Fact]
    public async Task DisabledContentSafety_StructuralOnly_MatchesEnabled_ForBenignConfig()
    {
        (AgentDefinitionValidator disabledValidator, _, _, _) =
            ValidatorTestHarness.Build(ValidatorTestHarness.DefaultConfig(contentSafetyEnabled: false));
        (AgentDefinitionValidator enabledValidator, _, _, _) =
            ValidatorTestHarness.Build(ValidatorTestHarness.DefaultConfig(contentSafetyEnabled: true));

        AgentDefinitionValidationReport disabled = await disabledValidator.ValidateAsync(
            ValidatorTestHarness.BenignConfig());
        AgentDefinitionValidationReport enabled = await enabledValidator.ValidateAsync(
            ValidatorTestHarness.BenignConfig());

        disabled.Violations.Should().BeEquivalentTo(enabled.Violations);
    }

    [Fact]
    public async Task DisabledContentSafety_StillRejects_PlainTextJailbreak()
    {
        HostileCorpus.HostileCase hostile = HostileCorpus.InstructionOverride[0];
        AgentDefinition def = ValidatorTestHarness.MakeAgent("victim", d =>
        {
            d.SystemPrompt = hostile.Payload;
        });

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig(contentSafetyEnabled: false);
        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        await FluentActions.Invoking(() => validator.ValidateAsync(promptConfig))
            .Should().ThrowAsync<AgentDefinitionValidationException>()
            .Where(e => e.Violations.Any(v =>
                v.DetectionType == AgentDefinitionDetectionTypes.Jailbreak));
    }

    [Fact]
    public async Task DisabledContentSafety_LetsEncodedPayloadThrough_DocumentedLimit()
    {
        // With Content Safety disabled, the base64 residual jailbreak passes.
        // This is intentional — the disabled path is documented as pattern-only.
        HostileCorpus.HostileCase hostile = HostileCorpus.Encoded[1];
        AgentDefinition def = ValidatorTestHarness.MakeAgent("residual", d =>
        {
            d.SystemPrompt = "You are a benign agent. Log entry token: " + hostile.Payload;
        });

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig(contentSafetyEnabled: false);
        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        AgentDefinitionValidationReport report = await validator.ValidateAsync(promptConfig);

        report.Violations.Should().BeEmpty(
            "encoded payloads slip past the pattern layer — this is the documented honest limit of the disabled path.");
    }

    [Fact]
    public async Task SafetyChecksEnabled_False_SkipsContentSafetyEntirely()
    {
        AgentDefinition def = ValidatorTestHarness.MakeAgent("skip");
        var evaluator = new FakeContentSafetyEvaluator();

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig(safetyChecksEnabled: false);
        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config, evaluator);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        AgentDefinitionValidationReport report = await validator.ValidateAsync(promptConfig);

        report.Violations.Should().BeEmpty();
        evaluator.Calls.Should().BeEmpty(
            "SafetyChecksEnabled=false must skip the Content Safety second pass entirely.");
    }
}
