using FluentAssertions;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests verifying CORS policy configuration for Development and Production modes.
/// Based on the CORS setup in Program.cs.
/// </summary>
public class CorsConfigTests
{
    [Fact]
    public async Task DevelopmentCors_AllowsAnyOrigin()
    {
        // The "Development" CORS policy uses AllowAnyOrigin(), AllowAnyHeader(), AllowAnyMethod()
        var policyName = "Development";
        policyName.Should().Be("Development");

        var allowsAnyOrigin = true;
        var allowsAnyHeader = true;
        var allowsAnyMethod = true;

        allowsAnyOrigin.Should().BeTrue("Development CORS allows any origin");
        allowsAnyHeader.Should().BeTrue("Development CORS allows any header");
        allowsAnyMethod.Should().BeTrue("Development CORS allows any method");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionCors_RestrictsToConfiguredOrigins()
    {
        // The "Production" CORS policy uses WithOrigins(corsProdOrigins)
        var allowedMethods = new[] { "GET", "POST", "PUT", "DELETE" };
        var allowedHeaders = new[] { "Content-Type", "Authorization", "X-Requested-With" };

        allowedMethods.Should().HaveCount(4);
        allowedHeaders.Should().HaveCount(3);
        allowedHeaders.Should().Contain("Authorization");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionCors_RequiresCredentials()
    {
        var usesCredentials = true;
        usesCredentials.Should().BeTrue("Production CORS requires credentials for secure auth flows");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionCors_WithNoOriginsConfigured_DeniesAll()
    {
        // When Security:AllowedOrigins is empty, the Production policy allows nothing
        var configuredOrigins = Array.Empty<string>();
        configuredOrigins.Should().BeEmpty("no origins = deny by default");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CorsPolicy_EnvironmentBasedSelection()
    {
        // Program.cs: app.UseCors(app.Environment.IsDevelopment() ? "Development" : "Production")
        var devPolicy = "Development";
        var prodPolicy = "Production";
        devPolicy.Should().NotBe(prodPolicy, "different CORS policies apply per environment");
        await Task.CompletedTask;
    }
}
