using System.Reflection;
using FluentAssertions;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// E1 — the <c>/api/guardrails/config</c> agent-definition projection must
/// never leak the deployment allow-lists (models, tools) or the privileged
/// grants. Only the operator-facing failure policy, safety toggle, and
/// temperature bounds are exposed.
/// </summary>
public class AgentDefinitionPolicyEndpointContractTests
{
    [Fact]
    public void AgentDefinitionPolicyResponse_HasNoAllowListOrGrantMember()
    {
        Type? response = typeof(Api.Endpoints.GuardrailEndpoints).Assembly
            .GetType("RetailPulse.Api.Endpoints.AgentDefinitionPolicyResponse");
        response.Should().NotBeNull();

        PropertyInfo[] props = response.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        props.Select(p => p.Name).Should().NotContain(
            n => n.Contains("AllowedModels", StringComparison.OrdinalIgnoreCase)
                || n.Contains("AllowedTools", StringComparison.OrdinalIgnoreCase)
                || n.Contains("PrivilegedTools", StringComparison.OrdinalIgnoreCase),
            "the projection must never surface deployment allow-lists or privileged grants");
    }
}
