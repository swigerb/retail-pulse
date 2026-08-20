using FluentAssertions;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Contracts.Guardrails;
using AgentDefinition = RetailPulse.Api.Models.AgentDefinition;
using PromptConfiguration = RetailPulse.Api.Models.PromptConfiguration;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// P1 — failure policy semantics. RefuseStartup surfaces EVERY offender in
/// one exception, and QuarantineOffender emits one LogWarning per removed
/// definition while leaving the benign roster intact.
/// </summary>
public class AgentDefinitionValidatorPolicyTests
{
    [Fact]
    public async Task RefuseStartup_AggregatesAllOffendersIntoSingleException()
    {
        AgentDefinition benign = ValidatorTestHarness.MakeAgent("benign");
        AgentDefinition badTemp = ValidatorTestHarness.MakeAgent("bad-temp", d => d.Temperature = 2.5);
        AgentDefinition badModel = ValidatorTestHarness.MakeAgent("bad-model", d => d.Model = "not-in-allow-list");
        AgentDefinition badRole = ValidatorTestHarness.MakeAgent("bad-role", d => d.Role = "hacker");

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(benign, badTemp, badModel, badRole);

        AgentDefinitionValidationException ex = (await FluentActions
            .Invoking(() => validator.ValidateAsync(promptConfig))
            .Should().ThrowAsync<AgentDefinitionValidationException>()).Which;

        ex.Violations.Should().Contain(v => v.AgentKey == badTemp.Key);
        ex.Violations.Should().Contain(v => v.AgentKey == badModel.Key);
        ex.Violations.Should().Contain(v => v.AgentKey == badRole.Key);
        ex.Violations.Should().NotContain(v => v.AgentKey == benign.Key);

        promptConfig.Agents.Should().ContainKey(benign.Key);
        promptConfig.Agents.Should().ContainKey(badTemp.Key,
            "definitions must not be mutated when RefuseStartup fires — the exception is authoritative");
    }

    [Fact]
    public async Task QuarantineOffender_RemovesOffendersOnly_AndKeepsBenignRoster()
    {
        AgentDefinition benign1 = ValidatorTestHarness.MakeAgent("benign-1");
        AgentDefinition benign2 = ValidatorTestHarness.MakeAgent("benign-2");
        AgentDefinition badTemp = ValidatorTestHarness.MakeAgent("bad-temp", d => d.Temperature = 2.5);
        AgentDefinition badRole = ValidatorTestHarness.MakeAgent("bad-role", d => d.Role = "hacker");

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig(
            failurePolicy: AgentDefinitionFailurePolicy.QuarantineOffender);
        (AgentDefinitionValidator validator, _, _,
            TestLogger<AgentDefinitionValidator> logger) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(benign1, benign2, badTemp, badRole);

        AgentDefinitionValidationReport report = await validator.ValidateAsync(promptConfig);

        report.QuarantinedAgentKeys.Should().BeEquivalentTo([badTemp.Key, badRole.Key]);
        promptConfig.Agents.Should().ContainKey(benign1.Key);
        promptConfig.Agents.Should().ContainKey(benign2.Key);
        promptConfig.Agents.Should().NotContainKey(badTemp.Key);
        promptConfig.Agents.Should().NotContainKey(badRole.Key);

        int warningCount = logger.Entries.Count(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.StartsWith("Quarantined agent definition", StringComparison.Ordinal));
        warningCount.Should().Be(2);
    }

    [Fact]
    public async Task DuplicateName_IsDetected()
    {
        AgentDefinition a = ValidatorTestHarness.MakeAgent("a", d => d.Name = "Shared Name");
        AgentDefinition b = ValidatorTestHarness.MakeAgent("b", d => d.Name = "Shared Name");

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(a, b);

        AgentDefinitionValidationException ex = (await FluentActions
            .Invoking(() => validator.ValidateAsync(promptConfig))
            .Should().ThrowAsync<AgentDefinitionValidationException>()).Which;

        ex.Violations.Should().Contain(v =>
            v.Field == "Name"
            && v.RuleId == "structural.duplicate-name"
            && v.AgentKey == b.Key);
    }

    [Fact]
    public async Task AllowedTools_WhenPopulated_OverridesRegistry()
    {
        AgentDefinition def = ValidatorTestHarness.MakeAgent("bespoke", d => d.Tools = ["CustomBespokeTool"]);
        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        config.AgentDefinition.AllowedTools = ["CustomBespokeTool"];

        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        AgentDefinitionValidationReport report = await validator.ValidateAsync(promptConfig);

        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task StructuralRules_TemperatureAndPromptLengthAndKeywordLength()
    {
        AgentDefinition def = ValidatorTestHarness.MakeAgent("x", d =>
        {
            d.Temperature = 1.5;
            d.SystemPrompt = new string('a', 33_000);
            d.KeywordFastPaths = [new string('k', 200)];
        });

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        AgentDefinitionValidationException ex = (await FluentActions
            .Invoking(() => validator.ValidateAsync(promptConfig))
            .Should().ThrowAsync<AgentDefinitionValidationException>()).Which;

        ex.Violations.Should().Contain(v => v.Field == "Temperature");
        ex.Violations.Should().Contain(v => v.Field == "SystemPrompt");
        ex.Violations.Should().Contain(v => v.Field == "KeywordFastPaths[0]");
    }
}
