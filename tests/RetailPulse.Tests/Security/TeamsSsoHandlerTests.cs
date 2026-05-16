using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.TeamsBot.Auth;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests for SSO tenant validation logic in TeamsSsoHandler.
/// Verifies tenant ID matching, strict mode rejection, and issuer construction.
/// </summary>
public class TeamsSsoHandlerTests
{
    private const string TestTenantId = "aaaabbbb-cccc-dddd-eeee-ffffffffffff";
    private const string WrongTenantId = "11111111-2222-3333-4444-555555555555";

    private static TeamsSsoHandler CreateHandler(
        string? tenantId,
        bool isDevelopment,
        string? strictValidation = null)
    {
        var configData = new Dictionary<string, string?>();
        if (tenantId is not null)
            configData["MicrosoftEntra:TenantId"] = tenantId;
        if (strictValidation is not null)
            configData["MicrosoftEntra:StrictTenantValidation"] = strictValidation;
        configData["MicrosoftEntra:ClientId"] = "test-client-id";

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(isDevelopment ? "Development" : "Production");

        return new TeamsSsoHandler(
            Mock.Of<ILogger<TeamsSsoHandler>>(),
            config,
            env.Object);
    }

    // ── ValidateTenantClaim ─────────────────────────────────────────────

    [Fact]
    public async Task ValidateTenantClaim_MatchingTenant_ReturnsTrue()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId, isDevelopment: false);

        bool result = handler.ValidateTenantClaim(TestTenantId);

        result.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateTenantClaim_MismatchWithStrictMode_ReturnsFalse()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId, isDevelopment: false, strictValidation: "true");

        bool result = handler.ValidateTenantClaim(WrongTenantId);

        result.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateTenantClaim_MismatchWithStrictDisabled_ReturnsTrue()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId, isDevelopment: true, strictValidation: "false");

        bool result = handler.ValidateTenantClaim(WrongTenantId);

        result.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateTenantClaim_NullTokenTid_WithConfiguredTenant_ReturnsFalse()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId, isDevelopment: false);

        bool result = handler.ValidateTenantClaim(null);

        result.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateTenantClaim_EmptyTokenTid_WithConfiguredTenant_ReturnsFalse()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId, isDevelopment: false);

        bool result = handler.ValidateTenantClaim(string.Empty);

        result.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateTenantClaim_NoConfiguredTenant_SkipsValidation()
    {
        TeamsSsoHandler handler = CreateHandler(tenantId: null, isDevelopment: true);

        bool result = handler.ValidateTenantClaim(WrongTenantId);

        result.Should().BeTrue("no tenant configured = dev-only path, skips tid check");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateTenantClaim_CaseInsensitiveMatch_ReturnsTrue()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId.ToLowerInvariant(), isDevelopment: false);

        bool result = handler.ValidateTenantClaim(TestTenantId.ToUpperInvariant());

        result.Should().BeTrue();
        await Task.CompletedTask;
    }

    // ── BuildValidIssuers ───────────────────────────────────────────────

    [Fact]
    public async Task BuildValidIssuers_WithTenant_ReturnsTenantSpecificIssuers()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId, isDevelopment: false);

        string[] issuers = handler.BuildValidIssuers();

        issuers.Should().HaveCount(2);
        issuers.Should().Contain($"https://login.microsoftonline.com/{TestTenantId}/v2.0");
        issuers.Should().Contain($"https://sts.windows.net/{TestTenantId}/");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildValidIssuers_WithTenant_DoesNotIncludeCommonEndpoint()
    {
        TeamsSsoHandler handler = CreateHandler(TestTenantId, isDevelopment: false);

        string[] issuers = handler.BuildValidIssuers();

        issuers.Should().NotContain(i => i.Contains("common"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildValidIssuers_WithoutTenant_IncludesCommonEndpoint()
    {
        TeamsSsoHandler handler = CreateHandler(tenantId: null, isDevelopment: true);

        string[] issuers = handler.BuildValidIssuers();

        issuers.Should().Contain(i => i.Contains("common"));
        await Task.CompletedTask;
    }

    // ── Constructor validation ──────────────────────────────────────────

    [Fact]
    public async Task Constructor_NoTenantInProduction_ThrowsInvalidOperationException()
    {
        Func<TeamsSsoHandler> act = () => CreateHandler(tenantId: null, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TenantId is required*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Constructor_NoTenantInDevelopment_DoesNotThrow()
    {
        Func<TeamsSsoHandler> act = () => CreateHandler(tenantId: null, isDevelopment: true);

        act.Should().NotThrow();
        await Task.CompletedTask;
    }
}
