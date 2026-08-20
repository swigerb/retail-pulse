using System.Reflection;
using FluentAssertions;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// C1 — configuration contract. There must be no key-material property on
/// <see cref="AgentDefinitionPolicy"/> so a credential cannot be smuggled
/// through configuration. Mirrors <c>NoKeyOnContentSafetyConfig</c>.
/// </summary>
public class AgentDefinitionPolicyContractTests
{
    [Fact]
    public void AgentDefinitionPolicy_HasNoKeyLikeMember()
    {
        PropertyInfo[] props = typeof(AgentDefinitionPolicy)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        props.Select(p => p.Name).Should().NotContain(
            n =>
                n.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                || n.Contains("SecretKey", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Key", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Endpoint", StringComparison.OrdinalIgnoreCase),
            "AgentDefinitionPolicy must never carry credentials or key material.");
    }

    [Fact]
    public void AgentDefinitionPolicy_DefaultsToRefuseStartup()
    {
        var policy = new AgentDefinitionPolicy();

        policy.OnValidationFailure.Should().Be(AgentDefinitionFailurePolicy.RefuseStartup,
            "issue #99 forbids silently accepting a failed validation.");
    }

    [Fact]
    public void GuardrailsConfig_ExposesAgentDefinitionPolicy_ByDefault()
    {
        var config = new GuardrailsConfig();

        config.AgentDefinition.Should().NotBeNull();
        config.AgentDefinition.SafetyChecksEnabled.Should().BeTrue();
        config.AgentDefinition.MaxSystemPromptLength.Should().Be(32_000);
    }
}
