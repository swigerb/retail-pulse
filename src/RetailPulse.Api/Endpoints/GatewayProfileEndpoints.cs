using RetailPulse.Api.OpenAI.Gateway;

namespace RetailPulse.Api.Endpoints;

public static class GatewayProfileEndpoints
{
    public static void MapGatewayProfileEndpoints(this WebApplication app)
    {
        app.MapGet("/api/gateway/profile", (IConfiguration configuration) =>
            Results.Ok(RegulatedRetailProfile.Resolve(configuration)))
            .WithTags("Gateway")
            .WithName("GetGatewayProfile")
            .WithDescription("Reports whether regulated-retail mode is active. Never claims HIPAA certification.")
            .RequireAuthorization();
    }
}
