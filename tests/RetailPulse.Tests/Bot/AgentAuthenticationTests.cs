using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetailPulse.TeamsBot.Auth;
using TokenValidationOptions = RetailPulse.TeamsBot.Auth.AgentAuthenticationExtensions.TokenValidationOptions;

namespace RetailPulse.Tests.Bot;

/// <summary>
/// Covers inbound channel authentication for the Teams bot.
/// </summary>
/// <remarks>
/// The bot mapped its messaging endpoint with <c>requireAuth: true</c> while registering
/// no authentication scheme at all, so every inbound Activity from Teams failed with
/// "No authenticationScheme was specified" — an HTTP 500 where the Bot Framework channel
/// expects a 401. The bot could never have worked in Production.
/// </remarks>
public class AgentAuthenticationTests
{
    private const string BotAppId = "0d376865-49b0-41a1-a37d-44eb0522c428";
    private const string TenantId = "48351615-345c-4547-bb6f-8fcc8d6e2568";

    private static TokenValidationOptions ValidOptions() => new()
    {
        Audiences = [BotAppId],
        TenantId = TenantId,
    };

    [Fact]
    public void Registers_JwtBearer_AsBothDefaultSchemes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentAspNetAuthentication(ValidOptions());

        AuthenticationOptions options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        // A missing DefaultChallengeScheme is exactly what produced the 500.
        options.DefaultAuthenticateScheme.Should().Be(JwtBearerDefaults.AuthenticationScheme);
        options.DefaultChallengeScheme.Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Accepts_TheBotFrameworkChannelIssuer()
    {
        TokenValidationOptions options = ValidOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentAspNetAuthentication(options);

        // Channel tokens for a Teams conversation are issued by the Bot Framework, not Entra.
        options.ValidIssuers.Should().Contain("https://api.botframework.com");
    }

    [Fact]
    public void Accepts_ItsOwnTenant_InBothIssuerForms()
    {
        TokenValidationOptions options = ValidOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentAspNetAuthentication(options);

        // A single-tenant bot also receives Entra-issued tokens from its own tenant.
        options.ValidIssuers.Should().Contain($"https://sts.windows.net/{TenantId}/");
        options.ValidIssuers.Should().Contain($"https://login.microsoftonline.com/{TenantId}/v2.0");
    }

    [Fact]
    public void Rejects_AnEmptyAudienceList()
    {
        var services = new ServiceCollection();
        Action act = () => services.AddAgentAspNetAuthentication(new TokenValidationOptions { Audiences = [] });

        // Without an audience any validly signed token from any tenant would be accepted.
        act.Should().Throw<ArgumentException>().WithMessage("*at least one ClientId*");
    }

    [Fact]
    public void Rejects_ANonGuidAudience()
    {
        var services = new ServiceCollection();
        Action act = () => services.AddAgentAspNetAuthentication(
            new TokenValidationOptions { Audiences = ["not-a-guid"] });

        act.Should().Throw<ArgumentException>().WithMessage("*must be GUIDs*");
    }

    [Fact]
    public void AzureBotServiceOnly_ExcludesEntraIssuers()
    {
        var options = new TokenValidationOptions
        {
            Audiences = [BotAppId],
            TenantId = TenantId,
            AzureBotServiceOnly = true,
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentAspNetAuthentication(options);

        options.ValidIssuers.Should().ContainSingle().Which.Should().Be("https://api.botframework.com");
    }

    [Fact]
    public void DefaultsTo_RoutingBotFrameworkTokensToTheBotFrameworkKeys()
    {
        TokenValidationOptions options = ValidOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentAspNetAuthentication(options);

        // Bot Framework tokens are signed with keys published at login.botframework.com;
        // validating them against the Entra key set would reject every channel message.
        options.AzureBotServiceTokenHandling.Should().BeTrue();
        options.AzureBotServiceOpenIdMetadataUrl.Should().Contain("login.botframework.com");
        options.OpenIdMetadataUrl.Should().Contain("login.microsoftonline.com");
    }

    // ---- Tenant / issuer cross-check ---------------------------------------------------
    //
    // Exercised through the same private helper the JwtBearer OnTokenValidated event uses.

    private static bool CrossCheck(string? tid, string? issuer)
    {
        var identity = new ClaimsIdentity(tid is null ? [] : new[] { new Claim("tid", tid) });

        System.Reflection.MethodInfo method = typeof(AgentAuthenticationExtensions)
            .GetMethod("IsTenantIdIssuerValid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (bool)method.Invoke(null, [identity, issuer])!;
    }

    [Theory]
    [InlineData("https://sts.windows.net/48351615-345c-4547-bb6f-8fcc8d6e2568/")]
    [InlineData("https://login.microsoftonline.com/48351615-345c-4547-bb6f-8fcc8d6e2568/v2.0")]
    public void TenantCrossCheck_AcceptsAMatchingIssuer(string issuer) => CrossCheck(TenantId, issuer).Should().BeTrue();

    [Fact]
    public void TenantCrossCheck_RejectsATokenReplayedFromAnotherTenant()
    {
        // Signed correctly and carrying an allowed issuer, but the tenant claim belongs to
        // somebody else. Checking issuer and tenant independently would let this through.
        CrossCheck("11111111-2222-3333-4444-555555555555",
            $"https://sts.windows.net/{TenantId}/").Should().BeFalse();
    }

    [Fact]
    public void TenantCrossCheck_SkipsTokensThatCarryNoTenantClaim() =>
        // Bot Framework channel tokens have no tid; the issuer allow-list governs them.
        CrossCheck(null, "https://api.botframework.com").Should().BeTrue();

    [Fact]
    public void TenantCrossCheck_RejectsAMalformedIssuer()
    {
        CrossCheck(TenantId, "not-a-url").Should().BeFalse();
        CrossCheck(TenantId, null).Should().BeFalse();
    }
}
