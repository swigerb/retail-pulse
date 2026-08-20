using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A1 — disabled-parity contract. With <see cref="ContentSafetyConfig.Enabled"/>
/// false the DI graph must contain only the no-op evaluator, and the middleware
/// must never call the injected evaluator. This is what makes the default
/// deployment byte-for-byte identical to the regex-only baseline.
/// </summary>
public class ContentSafetyDisabledParityTests
{
    [Fact]
    public void AddContentSafety_Disabled_RegistersOnlyNoOp()
    {
        var services = new ServiceCollection();
        var cfg = new ContentSafetyConfig { Enabled = false };
        services.AddContentSafety(cfg);

        using ServiceProvider sp = services.BuildServiceProvider();
        IContentSafetyEvaluator evaluator = sp.GetRequiredService<IContentSafetyEvaluator>();

        evaluator.Should().BeOfType<NoOpContentSafetyEvaluator>();
    }

    [Fact]
    public void AddContentSafety_Disabled_DoesNotRegisterHttpClientOrAzureClient()
    {
        var services = new ServiceCollection();
        services.AddContentSafety(new ContentSafetyConfig { Enabled = false });

        services.Should().NotContain(sd => sd.ServiceType == typeof(Azure.AI.ContentSafety.ContentSafetyClient),
            "the Azure client must not be constructed when Content Safety is disabled");
    }

    [Fact]
    public async Task Middleware_Disabled_DoesNotConsultEvaluator()
    {
        var fake = new FakeContentSafetyEvaluator();
        GuardrailsMiddleware middleware = CreateMiddleware(new GuardrailsConfig
        {
            JailbreakDetectionEnabled = true,
            PiiDetectionEnabled = true,
            MaxInputLength = 10_000,
            ContentSafety = new ContentSafetyConfig { Enabled = false }
        }, fake);

        GuardrailResult result = await middleware.CheckInputAsync(
            new ChatRequest("Show me the Northwest revenue trend", "session-1"));

        result.IsBlocked.Should().BeFalse();
        fake.Calls.Should().BeEmpty("evaluator is never called on the disabled path");
    }

    [Fact]
    public async Task Middleware_Disabled_OutputPassthroughUnchanged()
    {
        var fake = new FakeContentSafetyEvaluator();
        var config = new GuardrailsConfig
        {
            PiiDetectionEnabled = false,
            AutoRedactPii = false,
            ContentSafety = new ContentSafetyConfig { Enabled = false }
        };
        GuardrailsMiddleware middleware = CreateMiddleware(config, fake);

        string result = await middleware.FilterOutputAsync("Northwest revenue is up 12%.", "user-1");

        result.Should().Be("Northwest revenue is up 12%.");
        fake.Calls.Should().BeEmpty();
    }

    private static GuardrailsMiddleware CreateMiddleware(GuardrailsConfig config, IContentSafetyEvaluator evaluator)
    {
        var log = new InMemorySuspiciousRequestLog();
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());
        var logger = new Mock<ILogger<GuardrailsMiddleware>>();
        return new GuardrailsMiddleware(config, log, tenantProvider.Object, logger.Object, evaluator);
    }
}
