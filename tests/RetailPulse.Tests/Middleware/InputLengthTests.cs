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
        var mw = CreateMiddleware(maxLength: 10_000);
        var longInput = new string('A', 10_001);

        var result = await mw.CheckInputAsync(MakeRequest(longInput));

        result.IsBlocked.Should().BeTrue("input exceeds the 10,000 character limit");
        result.RefusalMessage.Should().Contain("maximum allowed length");
    }

    [Fact]
    public async Task InputLength_ExceedsCustomLimit_Blocked()
    {
        var mw = CreateMiddleware(maxLength: 500);
        var longInput = new string('B', 501);

        var result = await mw.CheckInputAsync(MakeRequest(longInput));

        result.IsBlocked.Should().BeTrue("input exceeds the custom 500 character limit");
    }

    [Fact]
    public async Task InputLength_WayOverLimit_Blocked()
    {
        var mw = CreateMiddleware(maxLength: 100);
        var longInput = new string('X', 50_000);

        var result = await mw.CheckInputAsync(MakeRequest(longInput));

        result.IsBlocked.Should().BeTrue("extremely long input should be blocked");
    }

    #endregion

    #region At or Under Limit — PASSED

    [Fact]
    public async Task InputLength_ExactlyAtLimit_NotBlocked()
    {
        var mw = CreateMiddleware(maxLength: 10_000);
        var input = new string('C', 10_000);

        var result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeFalse("input exactly at the limit should pass");
    }

    [Fact]
    public async Task InputLength_UnderLimit_NotBlocked()
    {
        var mw = CreateMiddleware(maxLength: 10_000);
        var input = "What are Q3 sales for Sierra Gold Tequila?";

        var result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeFalse("normal-length query should pass");
    }

    [Fact]
    public async Task InputLength_OneUnderLimit_NotBlocked()
    {
        var mw = CreateMiddleware(maxLength: 200);
        var input = new string('D', 199);

        var result = await mw.CheckInputAsync(MakeRequest(input));

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
