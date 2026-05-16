using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for input length validation in GuardrailsMiddleware.
/// Verifies that queries exceeding MaxInputLength are rejected,
/// at-limit queries pass, and the default is 10,000 characters.
/// Act 10 — Enterprise Shield coverage gap #2.
/// </summary>
public class InputLengthTests
{
    private static GuardrailsMiddleware CreateMiddleware(int maxLength = 10_000)
    {
        var cfg = new GuardrailsConfig
        {
            MaxInputLength = maxLength,
            JailbreakDetectionEnabled = false,
            PiiDetectionEnabled = false
        };
        var log = new InMemorySuspiciousRequestLog();
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());
        var logger = new Mock<ILogger<GuardrailsMiddleware>>();
        return new GuardrailsMiddleware(cfg, log, tenantProvider.Object, logger.Object);
    }

    private static ChatRequest MakeRequest(string message) =>
        new(message, SessionId: "test-session");

    #region Over Max Length — BLOCKED

    [Fact]
    public async Task InputLength_ExceedsDefault10000_Blocked()
    {
        GuardrailsMiddleware mw = CreateMiddleware(maxLength: 10_000);
        string longInput = new('A', 10_001);

        GuardrailResult result = await mw.CheckInputAsync(MakeRequest(longInput));

        result.IsBlocked.Should().BeTrue("input exceeds the 10,000 character limit");
        result.RefusalMessage.Should().Contain("maximum allowed length");
    }

    [Fact]
    public async Task InputLength_ExceedsCustomLimit_Blocked()
    {
        GuardrailsMiddleware mw = CreateMiddleware(maxLength: 500);
        string longInput = new('B', 501);

        GuardrailResult result = await mw.CheckInputAsync(MakeRequest(longInput));

        result.IsBlocked.Should().BeTrue("input exceeds the custom 500 character limit");
    }

    [Fact]
    public async Task InputLength_WayOverLimit_Blocked()
    {
        GuardrailsMiddleware mw = CreateMiddleware(maxLength: 100);
        string longInput = new('X', 50_000);

        GuardrailResult result = await mw.CheckInputAsync(MakeRequest(longInput));

        result.IsBlocked.Should().BeTrue("extremely long input should be blocked");
    }

    #endregion

    #region At or Under Limit — PASSED

    [Fact]
    public async Task InputLength_ExactlyAtLimit_NotBlocked()
    {
        GuardrailsMiddleware mw = CreateMiddleware(maxLength: 10_000);
        string input = new('C', 10_000);

        GuardrailResult result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeFalse("input exactly at the limit should pass");
    }

    [Fact]
    public async Task InputLength_UnderLimit_NotBlocked()
    {
        GuardrailsMiddleware mw = CreateMiddleware(maxLength: 10_000);
        string input = "What are Q3 sales for Sierra Gold Tequila?";

        GuardrailResult result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeFalse("normal-length query should pass");
    }

    [Fact]
    public async Task InputLength_OneUnderLimit_NotBlocked()
    {
        GuardrailsMiddleware mw = CreateMiddleware(maxLength: 200);
        string input = new('D', 199);

        GuardrailResult result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeFalse("input one char under limit should pass");
    }

    #endregion

    #region Default Config

    [Fact]
    public void DefaultConfig_MaxInputLength_Is10000()
    {
        var config = new GuardrailsConfig();
        config.MaxInputLength.Should().Be(10_000,
            "default max input length should be 10,000 characters");
    }

    #endregion
}
