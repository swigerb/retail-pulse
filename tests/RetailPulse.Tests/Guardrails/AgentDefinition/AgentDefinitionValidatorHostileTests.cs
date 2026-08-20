using FluentAssertions;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Tests.Guardrails.ContentSafety;
using AgentDefinition = RetailPulse.Api.Models.AgentDefinition;
using PromptConfiguration = RetailPulse.Api.Models.PromptConfiguration;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// H1 — hostile fixtures must be rejected under <c>RefuseStartup</c>, and the
/// aggregated exception + audit rows must name the offending agent, field,
/// and rule.
/// </summary>
public class AgentDefinitionValidatorHostileTests
{
    [Theory]
    [MemberData(nameof(PatternDetectableCases))]
    public async Task RefuseStartup_RejectsPatternHostileCase_AndAuditsIt(string caseName)
    {
        HostileCorpus.HostileCase hostile = HostileCorpus.Get(caseName);
        AgentDefinition def = BuildDefinitionWithHostile(hostile);

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog audit,
            _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        Func<Task> act = () => validator.ValidateAsync(promptConfig);

        AgentDefinitionValidationException ex = (await act.Should()
            .ThrowAsync<AgentDefinitionValidationException>()).Which;

        ex.Message.Should().Contain($"agent='{def.Key}'");
        ex.Message.Should().Contain($"field='{hostile.Field}'");
        ex.Violations.Should().Contain(v =>
            v.AgentKey == def.Key
            && v.Field == hostile.Field
            && v.DetectionType == hostile.ExpectedDetectionType);

        IReadOnlyList<SuspiciousRequest> rows = await audit.GetRecentAsync(50);
        rows.Should().Contain(r =>
            r.DetectionType == hostile.ExpectedDetectionType
            && r.UserContext == AgentDefinitionDetectionTypes.StartupValidatorContext
            && r.Action == AgentDefinitionDetectionTypes.ActionBlocked
            && r.RequestText.Contains(def.Key)
            && r.RequestText.Contains(hostile.Field));
    }

    [Theory]
    [MemberData(nameof(PatternDetectableCases))]
    public async Task QuarantineOffender_RemovesAgent_AndLogsWarning(string caseName)
    {
        HostileCorpus.HostileCase hostile = HostileCorpus.Get(caseName);
        AgentDefinition benign = ValidatorTestHarness.MakeAgent("benign-a");
        AgentDefinition offender = BuildDefinitionWithHostile(hostile);

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig(
            failurePolicy: AgentDefinitionFailurePolicy.QuarantineOffender);
        (AgentDefinitionValidator validator, RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog audit,
            _, TestLogger<AgentDefinitionValidator> logger) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(benign, offender);

        AgentDefinitionValidationReport report = await validator.ValidateAsync(promptConfig);

        report.QuarantinedAgentKeys.Should().ContainSingle(k => k == offender.Key);
        promptConfig.Agents.Should().NotContainKey(offender.Key);
        promptConfig.Agents.Should().ContainKey(benign.Key);

        logger.Entries.Should().Contain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("Quarantined agent definition")
            && e.Message.Contains(offender.Key));

        IReadOnlyList<SuspiciousRequest> rows = await audit.GetRecentAsync(50);
        rows.Should().Contain(r =>
            r.Action == AgentDefinitionDetectionTypes.ActionQuarantined
            && r.DetectionType == hostile.ExpectedDetectionType);
    }

    [Fact]
    public async Task ContentSafety_Enabled_CatchesEncodedPayload_ThatPatternLayerMisses()
    {
        HostileCorpus.HostileCase hostile = HostileCorpus.Encoded[0];
        AgentDefinition def = BuildDefinitionWithHostile(hostile);
        var evaluator = new FakeContentSafetyEvaluator
        {
            Matcher = (text, stage) =>
            {
                if (stage != ContentSafetyStage.AgentDefinition) return null;
                return text.Contains(hostile.Payload, StringComparison.Ordinal)
                    ? new ContentSafetyResult(
                        ContentSafetyDecision.Blocked,
                        [],
                        PromptShieldJailbreakDetected: true,
                        PromptShieldIndirectInjectionDetected: false,
                        Latency: TimeSpan.FromMilliseconds(1),
                        CorrelationId: "cs-block-1")
                    : null;
            },
        };

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog audit,
            _, _) = ValidatorTestHarness.Build(config, evaluator);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        Func<Task> act = () => validator.ValidateAsync(promptConfig);

        AgentDefinitionValidationException ex = (await act.Should()
            .ThrowAsync<AgentDefinitionValidationException>()).Which;
        ex.Violations.Should().Contain(v =>
            v.DetectionType == AgentDefinitionDetectionTypes.ContentSafety
            && v.AgentKey == def.Key
            && v.Field == hostile.Field);

        IReadOnlyList<SuspiciousRequest> rows = await audit.GetRecentAsync(50);
        rows.Should().Contain(r =>
            r.DetectionType == AgentDefinitionDetectionTypes.ContentSafety);
    }

    [Fact]
    public async Task ToolEscalation_UpdateMetrics_WithoutGrant_IsRejected()
    {
        // UpdateMetrics is deliberately not in AgentToolRegistry today, so it's
        // rejected by the policy layer (tool-not-allowed).
        AgentDefinition def = ValidatorTestHarness.MakeAgent("general", d =>
        {
            d.Tools = ["CreateChart", "UpdateMetrics"];
        });

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog audit,
            _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        AgentDefinitionValidationException ex = (await FluentActions
            .Invoking(() => validator.ValidateAsync(promptConfig))
            .Should().ThrowAsync<AgentDefinitionValidationException>()).Which;

        ex.Violations.Should().Contain(v =>
            v.AgentKey == def.Key
            && v.Field == "Tools[1]"
            && v.DetectionType == AgentDefinitionDetectionTypes.Policy);

        (await audit.GetRecentAsync(50)).Should().Contain(r =>
            r.DetectionType == AgentDefinitionDetectionTypes.Policy
            && r.Action == AgentDefinitionDetectionTypes.ActionBlocked);
    }

    [Fact]
    public async Task ToolEscalation_RequestApproval_OnUngrantedAgent_IsRejected()
    {
        AgentDefinition def = ValidatorTestHarness.MakeAgent("field-sentiment", d =>
        {
            d.Tools = ["CreateChart", "RequestApproval"];
        });

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, _, _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        AgentDefinitionValidationException ex = (await FluentActions
            .Invoking(() => validator.ValidateAsync(promptConfig))
            .Should().ThrowAsync<AgentDefinitionValidationException>()).Which;

        ex.Violations.Should().Contain(v =>
            v.AgentKey == def.Key
            && v.DetectionType == AgentDefinitionDetectionTypes.PrivilegedGrant);
    }

    [Fact]
    public async Task ToolEscalation_RequestApproval_OnGrantedAgent_IsAccepted()
    {
        AgentDefinition def = ValidatorTestHarness.MakeAgent("promo-planning", d =>
        {
            d.Tools = ["CreateChart", "RequestApproval"];
        });

        GuardrailsConfig config = ValidatorTestHarness.DefaultConfig();
        (AgentDefinitionValidator validator, RetailPulse.Api.Guardrails.InMemorySuspiciousRequestLog audit,
            _, _) = ValidatorTestHarness.Build(config);
        PromptConfiguration promptConfig = ValidatorTestHarness.Configure(def);

        AgentDefinitionValidationReport report = await validator.ValidateAsync(promptConfig);

        report.Violations.Should().BeEmpty();
        (await audit.GetRecentAsync(50)).Should().BeEmpty();
    }

    public static IEnumerable<object[]> PatternDetectableCases() =>
        HostileCorpus.AsPatternDetectableTheoryData();

    private static AgentDefinition BuildDefinitionWithHostile(HostileCorpus.HostileCase hostile) =>
        ValidatorTestHarness.MakeAgent("hostile", d =>
        {
            switch (hostile.Field)
            {
                case "SystemPrompt":
                    d.SystemPrompt = hostile.Payload;
                    break;
                case "DisplayName":
                    d.DisplayName = hostile.Payload;
                    break;
                case "FallbackReply":
                    d.FallbackReply = hostile.Payload;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected field '{hostile.Field}'.");
            }
        });
}
