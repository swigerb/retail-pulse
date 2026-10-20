using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Endpoints;

public static class GatewayProfileEndpoints
{
    public static void MapGatewayProfileEndpoints(this WebApplication app)
    {
        app.MapGet("/api/gateway/profile", (IConfiguration configuration, GuardrailsConfig guardrails) =>
            Results.Ok(RegulatedRetailProfile.Resolve(configuration, guardrails.ContentSafety)))
            .WithTags("Gateway")
            .WithName("GetGatewayProfile")
            .WithDescription("Reports whether regulated-retail mode is active. Never claims HIPAA certification.")
            .RequireAuthorization();
    }
}
