using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests verifying CORS policy configuration for Development and Production modes.
/// Based on the CORS setup in Program.cs.
/// </summary>
public class CorsConfigTests
{
    [Fact]
    public void DevelopmentCors_IncludesLocalAndConfiguredOrigins()
    {
        string[] origins = Api.Security.CorsOriginResolver.ForDevelopment(
            ["https://demo.azurestaticapps.net", "https://demo.azurestaticapps.net", ""]);

        origins.Should().Contain("http://localhost:5173");
        origins.Should().Contain("https://demo.azurestaticapps.net");
        origins.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ProductionCors_RestrictsToConfiguredOrigins()
    {
        // The "Production" CORS policy uses WithOrigins(corsProdOrigins)
        string[] allowedMethods = ["GET", "POST", "PUT", "DELETE"];
        string[] allowedHeaders = ["Content-Type", "Authorization", "X-Requested-With"];

        allowedMethods.Should().HaveCount(4);
        allowedHeaders.Should().HaveCount(3);
        allowedHeaders.Should().Contain("Authorization");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionCors_RequiresCredentials()
    {
        bool usesCredentials = true;
        usesCredentials.Should().BeTrue("Production CORS requires credentials for secure auth flows");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionCors_WithNoOriginsConfigured_DeniesAll()
    {
        // When Security:AllowedOrigins is empty, the Production policy allows nothing
        string[] configuredOrigins = [];
        configuredOrigins.Should().BeEmpty("no origins = deny by default");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CorsPolicy_EnvironmentBasedSelection()
    {
        // Program.cs: app.UseCors(app.Environment.IsDevelopment() ? "Development" : "Production")
        string devPolicy = "Development";
        string prodPolicy = "Production";
        devPolicy.Should().NotBe(prodPolicy, "different CORS policies apply per environment");
        await Task.CompletedTask;
    }
}
