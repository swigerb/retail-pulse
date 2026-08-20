using System.Reflection;
using FluentAssertions;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A13 — the <c>/api/guardrails/config</c> projection must never leak the
/// Content Safety endpoint URL, and the runtime update DTO must never accept
/// an <c>Endpoint</c> field. Callers can toggle the runtime flags and
/// per-category thresholds; the endpoint URL is server-side only.
/// </summary>
public class ContentSafetyConfigEndpointContractTests
{
    [Fact]
    public void ContentSafetyConfigResponse_HasNoEndpointOrKeyMember()
    {
        Type? response = typeof(Api.Endpoints.GuardrailEndpoints).Assembly
            .GetType("RetailPulse.Api.Endpoints.ContentSafetyConfigResponse");
        response.Should().NotBeNull();

        PropertyInfo[] props = response.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        props.Select(p => p.Name).Should().NotContain(
            n => n.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Key", StringComparison.OrdinalIgnoreCase),
            "the config projection must never surface the account endpoint or any key");
    }

    [Fact]
    public void ContentSafetyConfigUpdateDto_HasNoEndpointOrKeyMember()
    {
        Type? update = typeof(Api.Endpoints.GuardrailEndpoints).Assembly
            .GetType("RetailPulse.Api.Endpoints.ContentSafetyConfigUpdateDto");
        update.Should().NotBeNull();

        PropertyInfo[] props = update.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        props.Select(p => p.Name).Should().NotContain(
            n => n.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Key", StringComparison.OrdinalIgnoreCase),
            "runtime updates must not accept an endpoint URL — that is a provisioning concern");
    }
}
