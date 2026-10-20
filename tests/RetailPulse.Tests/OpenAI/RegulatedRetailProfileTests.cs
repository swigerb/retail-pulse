using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.OpenAI.Gateway;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Tests.Guardrails.ContentSafety;

namespace RetailPulse.Tests.OpenAI;

public sealed class RegulatedRetailProfileTests
{
    private static IConfiguration Config(string? enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [RegulatedRetailProfile.ConfigKey] = enabled }).Build();

    [Fact]
    public void DefaultIsSyntheticDemo_AndClearlyDistinguished()
    {
        var demo = RegulatedRetailProfile.Resolve(Config(null));
        var reg = RegulatedRetailProfile.Resolve(Config("true"));
        demo.Enabled.Should().BeFalse();
        demo.Label.Should().NotBe(reg.Label);
        reg.Mode.Should().Be("regulated-retail");
    }

    [Fact]
    public void Regulated_DisablesBodies_FailsClosed_AndNeverClaimsCertification()
    {
        var reg = RegulatedRetailProfile.Resolve(Config("true"), new ContentSafetyConfig { Enabled = true, ForceFailClosed = true });
        reg.CaptureMessageBodies.Should().BeFalse();
        reg.SafetyFailClosed.Should().BeTrue();
        reg.Label.Should().Contain("not HIPAA certified");
        reg.Disclaimer.Should().Contain("patient data").And.Contain("clinical advice");
    }

    [Fact]
    public void Regulated_ContentSafetyDisabled_DoesNotClaimFailClosed()
    {
        var reg = RegulatedRetailProfile.Resolve(
            Config("true"),
            new ContentSafetyConfig { Enabled = false, ForceFailClosed = true });

        reg.SafetyFailClosed.Should().BeFalse();
    }

    [Theory]
    [InlineData("pharmacy")]
    [InlineData("Product-Recall")]
    [InlineData("identity-bearing")]
    [InlineData("patient")]
    [InlineData(null)]
    [InlineData("")]
    public void SensitiveOrUnknownClasses_BypassCache_WhenRegulated(string? cls)
    {
        SensitiveCachePolicy.ShouldBypass(true, cls).Should().BeTrue();
        SensitiveCachePolicy.ShouldBypass(false, cls).Should().BeFalse();
    }

    [Fact]
    public void NonSensitiveClass_IsCacheable_WhenRegulated() =>
        SensitiveCachePolicy.ShouldBypass(true, "assortment").Should().BeFalse();

    [Fact]
    public async Task CorrelationHeader_IsMaskedOnlyInRegulatedMode()
    {
        const string id = "11111111-2222-3333-4444-555555555555";
        foreach ((string? flag, bool masked) in new[] { ("true", true), (null, false) })
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["X-Correlation-ID"] = id;
            var mw = new CorrelationIdMiddleware(_ => Task.CompletedTask, NullLogger<CorrelationIdMiddleware>.Instance, Config(flag));
            await mw.InvokeAsync(ctx);
            string shown = ctx.Response.Headers["X-Correlation-ID"]!;
            (shown == id).Should().Be(!masked);
            if (masked) { shown.Should().NotContain("2222-3333"); }
        }
    }

    private static GuardrailsMiddleware BuildMiddleware(GuardrailsConfig config, FakeContentSafetyEvaluator fake)
    {
        var tenants = new Moq.Mock<ITenantProvider>();
        tenants.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());
        return new GuardrailsMiddleware(config, new InMemorySuspiciousRequestLog(), tenants.Object,
            NullLogger<GuardrailsMiddleware>.Instance, fake);
    }

    private static GuardrailsConfig ContentSafetyConfigFor(bool regulated, ContentSafetyFailPolicy requested)
    {
        var cfg = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = "https://example.cognitiveservices.azure.com",
                OnUnavailable = requested,
                ForceFailClosed = regulated,
            },
        };
        return cfg;
    }

    [Fact]
    public async Task Regulated_UnavailableContentSafety_IsBlocked_EvenAfterRuntimeFailOpenUpdate()
    {
        var fake = new FakeContentSafetyEvaluator();
        fake.Enqueue(ContentSafetyStage.Input, ContentSafetyResult.ServiceUnavailable);
        GuardrailsConfig cfg = ContentSafetyConfigFor(regulated: true, ContentSafetyFailPolicy.FailOpen);
        cfg.ContentSafety.OnUnavailable = ContentSafetyFailPolicy.FailOpen; // simulated PUT /api/guardrails/config

        GuardrailResult result = await BuildMiddleware(cfg, fake).CheckInputAsync(new ChatRequest("Northwest revenue?", "s"));

        result.IsBlocked.Should().BeTrue();
        RegulatedRetailProfile.Resolve(Config("true"), cfg.ContentSafety).SafetyFailClosed.Should().BeTrue();
    }

    [Fact]
    public async Task NormalMode_UnavailableContentSafety_RemainsConfigurable()
    {
        var fake = new FakeContentSafetyEvaluator();
        fake.Enqueue(ContentSafetyStage.Input, ContentSafetyResult.ServiceUnavailable);
        GuardrailsConfig cfg = ContentSafetyConfigFor(regulated: false, ContentSafetyFailPolicy.FailOpen);

        (await BuildMiddleware(cfg, fake).CheckInputAsync(new ChatRequest("Northwest revenue?", "s"))).IsBlocked.Should().BeFalse();
        RegulatedRetailProfile.Resolve(Config(null), cfg.ContentSafety).SafetyFailClosed.Should().BeFalse();

        cfg.ContentSafety.OnUnavailable = ContentSafetyFailPolicy.FailClosed;
        cfg.ContentSafety.OnUnavailable.Should().Be(ContentSafetyFailPolicy.FailClosed);
    }
}
