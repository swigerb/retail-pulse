using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for SQL injection detection in GuardrailsMiddleware.
/// Verifies that InjectionPatterns block DROP TABLE, UNION SELECT, XSS,
/// and other injection vectors while allowing normal business queries.
/// Act 10 — Enterprise Shield coverage gap #1.
/// </summary>
public class SqlInjectionTests
{
    private static GuardrailsMiddleware CreateMiddleware(GuardrailsConfig? config = null)
    {
        var cfg = config ?? new GuardrailsConfig
        {
            JailbreakDetectionEnabled = true,
            PiiDetectionEnabled = true,
            MaxInputLength = 10_000
        };
        var log = new InMemorySuspiciousRequestLog();
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());
        var logger = new Mock<ILogger<GuardrailsMiddleware>>();
        return new GuardrailsMiddleware(cfg, log, tenantProvider.Object, logger.Object);
    }

    private static ChatRequest MakeRequest(string message) =>
        new(message, SessionId: "test-session");

    #region SQL Injection Patterns — BLOCKED

    [Theory]
    [InlineData("'; DROP TABLE users--")]
    [InlineData("'; DELETE FROM orders--")]
    [InlineData("test'; drop table customers;--")]
    public async Task Injection_DropTable_Blocked(string input)
    {
        var mw = CreateMiddleware();
        var result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeTrue($"'{input}' contains a SQL injection pattern");
        result.RefusalMessage.Should().Contain("injection");
    }

    [Theory]
    [InlineData("' OR '1'='1")]
    [InlineData("' or 1=1--")]
    public async Task Injection_OrTautology_Blocked(string input)
    {
        var mw = CreateMiddleware();
        var result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeTrue($"'{input}' is a classic SQL injection tautology");
    }

    [Theory]
    [InlineData("UNION SELECT username, password FROM users")]
    [InlineData("1 union select * from credentials")]
    public async Task Injection_UnionSelect_Blocked(string input)
    {
        var mw = CreateMiddleware();
        var result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeTrue($"'{input}' contains UNION SELECT injection");
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("Hello <script>document.cookie</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src='evil.com'>")]
    [InlineData("javascript:alert(1)")]
    public async Task Injection_XssPatterns_Blocked(string input)
    {
        var mw = CreateMiddleware();
        var result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeTrue($"'{input}' contains an XSS/injection pattern");
    }

    #endregion

    #region Normal Business Queries — NOT Blocked

    [Theory]
    [InlineData("What are our union membership numbers?")]
    [InlineData("Show me the table of sales data")]
    [InlineData("Can you select the top brands by revenue?")]
    [InlineData("Drop me a line when Q4 numbers are in")]
    [InlineData("Compare Northeast and Southeast performance")]
    public async Task Injection_NormalQueries_NotBlocked(string input)
    {
        var mw = CreateMiddleware();
        var result = await mw.CheckInputAsync(MakeRequest(input));

        result.IsBlocked.Should().BeFalse($"'{input}' is a normal business query");
    }

    #endregion

    #region Injection Logging

    [Fact]
    public async Task Injection_Blocked_LoggedToSuspiciousLog()
    {
        var log = new InMemorySuspiciousRequestLog();
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());

        var mw = new GuardrailsMiddleware(
            new GuardrailsConfig { JailbreakDetectionEnabled = true },
            log,
            tenantProvider.Object,
            new Mock<ILogger<GuardrailsMiddleware>>().Object);

        await mw.CheckInputAsync(MakeRequest("'; drop table users--"));

        var entries = await log.GetRecentAsync();
        entries.Should().ContainSingle(e => e.DetectionType == "injection",
            "blocked injection attempt should be logged");
    }

    #endregion

    #region Disabled Detection — NOT Blocked

    [Fact]
    public async Task Injection_DetectionDisabled_NotBlocked()
    {
        var mw = CreateMiddleware(new GuardrailsConfig
        {
            JailbreakDetectionEnabled = false // injection piggybacks on this toggle
        });

        var result = await mw.CheckInputAsync(MakeRequest("'; drop table users--"));

        result.IsBlocked.Should().BeFalse("injection detection is disabled when JailbreakDetectionEnabled = false");
    }

    #endregion
}
