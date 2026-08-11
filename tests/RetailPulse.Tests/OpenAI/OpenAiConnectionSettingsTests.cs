using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using RetailPulse.Api.OpenAI;

namespace RetailPulse.Tests.OpenAI;

public class OpenAiConnectionSettingsTests
{
    [Fact]
    public void Load_PrefersApimSubscriptionKey_WhenManagedIdentityDisabled()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://gateway.example.com/inference",
            useManagedIdentity: false,
            apiKey: "direct-key",
            apimSubscriptionKey: "apim-sub-key");

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        settings.Endpoint.Should().Be("https://gateway.example.com/inference");
        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ApiKey);
        settings.ApiKey.Should().Be("apim-sub-key");
        settings.ApiKeySource.Should().Be("OpenAI:ApimSubscriptionKey");
    }

    [Fact]
    public void Load_FallsBackToDirectApiKey_WhenApimSubscriptionKeyMissing()
    {
        // Direct AOAI endpoint is only valid in Development or with the explicit
        // OpenAI:AllowDirectEndpoint escape hatch (see AI Gateway invariants below).
        IConfiguration config = CreateConfig(
            endpoint: "https://contoso.openai.azure.com",
            useManagedIdentity: false,
            apiKey: "direct-key");

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: true));

        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ApiKey);
        settings.ApiKey.Should().Be("direct-key");
        settings.ApiKeySource.Should().Be("OpenAI:ApiKey");
    }

    [Fact]
    public void Load_PreservesManagedIdentityPath_WhenEnabled()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://contoso.openai.azure.com",
            useManagedIdentity: true,
            apiKey: "direct-key",
            apimSubscriptionKey: "apim-sub-key");

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: true));

        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ManagedIdentity);
        settings.ApiKey.Should().BeNull();
        settings.ApiKeySource.Should().Be("ManagedIdentity");
    }

    [Fact]
    public void Load_UsesDevelopmentFallbackKey_WhenManagedIdentityDisabledAndNoKeyConfigured()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://gateway.example.com/inference",
            useManagedIdentity: false);

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: true));

        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ApiKey);
        settings.ApiKey.Should().Be("demo-key");
        settings.ApiKeySource.Should().Be("DevelopmentFallback");
    }

    [Fact]
    public void Load_ThrowsInDevelopment_WhenEndpointMissing()
    {
        IConfiguration config = CreateConfig(
            useManagedIdentity: false);

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: true));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenAI:Endpoint*");
    }

    [Fact]
    public void Load_ThrowsOutsideDevelopment_WhenManagedIdentityDisabledAndNoKeyConfigured()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://gateway.example.com/inference",
            useManagedIdentity: false);

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenAI:ApimSubscriptionKey*OpenAI:ApiKey*");
    }

    // ── AI Gateway invariants ────────────────────────────────────────────────

    [Theory]
    [InlineData("https://gateway.example.com/inference/openai")]
    [InlineData("https://gateway.example.com/openai")]
    public void Load_RejectsEndpointEndingInOpenAi_RegardlessOfEnvironment(string endpoint)
    {
        IConfiguration config = CreateConfig(
            endpoint: endpoint,
            useManagedIdentity: false,
            apimSubscriptionKey: "apim-sub-key");

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*doubled*openai*");
    }

    [Fact]
    public void Load_RejectsEndpointEndingInOpenAi_EvenInDevelopment()
    {
        // The SDK-appends-/openai regression from #55 is not environment-specific:
        // a Development run against APIM with an /openai-suffixed endpoint also
        // produces a doubled path at request time.
        IConfiguration config = CreateConfig(
            endpoint: "https://gateway.example.com/inference/openai",
            useManagedIdentity: false);

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: true));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*openai*");
    }

    [Theory]
    [InlineData("https://contoso.openai.azure.com")]
    [InlineData("https://aiservices-abc.cognitiveservices.azure.com")]
    [InlineData("https://foundry-xyz.services.ai.azure.com")]
    public void Load_RejectsDirectAzureOpenAiEndpoint_OutsideDevelopment(string endpoint)
    {
        IConfiguration config = CreateConfig(
            endpoint: endpoint,
            useManagedIdentity: false,
            apimSubscriptionKey: "apim-sub-key");

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*APIM AI Gateway*");
    }

    [Fact]
    public void Load_AllowsDirectAzureOpenAiEndpoint_InDevelopment()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://contoso.openai.azure.com",
            useManagedIdentity: false);

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: true));

        settings.Endpoint.Should().Be("https://contoso.openai.azure.com");
    }

    [Fact]
    public void Load_AllowsDirectAzureOpenAiEndpoint_WhenExplicitEscapeHatchSet()
    {
        var data = new Dictionary<string, string?>
        {
            ["OpenAI:Endpoint"] = "https://contoso.openai.azure.com",
            ["OpenAI:UseManagedIdentity"] = "true",
            ["OpenAI:AllowDirectEndpoint"] = "true",
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        settings.Endpoint.Should().Be("https://contoso.openai.azure.com");
        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ManagedIdentity);
    }

    [Theory]
    [InlineData("/inference")]        // Linux: parses as file:///inference — must be rejected
    [InlineData("apim.example.com/inference")] // no scheme
    [InlineData("ftp://apim.example.com/inference")] // non-http scheme
    public void Load_ThrowsWhenEndpointIsNotAbsoluteHttpUrl(string endpoint)
    {
        IConfiguration config = CreateConfig(
            endpoint: endpoint,
            useManagedIdentity: false,
            apimSubscriptionKey: "apim-sub-key");

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute http(s) URL*");
    }

    // ── Ordering guarantees ─────────────────────────────────────────────────
    // The AI Gateway invariant MUST be evaluated before any auth-mode branch
    // (ManagedIdentity vs ApiKey vs DevelopmentFallback). Otherwise a bad
    // endpoint escapes into `AzureOpenAIClient` construction and blows up at
    // request time instead of startup. These tests pin the ordering directly:
    // even the configurations that would normally succeed on the auth branch
    // must still fail on the invariant when the endpoint is bad.

    [Fact]
    public void Load_ChecksAiGatewayInvariant_BeforeManagedIdentityBranch()
    {
        // ManagedIdentity path would normally succeed without any key. The
        // invariant must still fire on a non-absolute endpoint.
        IConfiguration config = CreateConfig(
            endpoint: "/inference",
            useManagedIdentity: true);

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute http(s) URL*");
    }

    [Fact]
    public void Load_ChecksAiGatewayInvariant_BeforeDevelopmentFallbackKey()
    {
        // Development fallback would normally hand out "demo-key" and succeed.
        // The invariant must still fire on an /openai-suffixed endpoint —
        // Development is not an escape hatch for the doubled-segment defect.
        IConfiguration config = CreateConfig(
            endpoint: "https://gateway.example.com/inference/openai",
            useManagedIdentity: false);

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: true));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*openai*");
    }

    [Fact]
    public void Load_ChecksAiGatewayInvariant_BeforeApiKeyResolution()
    {
        // ApiKey path would normally succeed with an APIM subscription key.
        // The invariant must still fire on a direct AOAI host outside Dev
        // even when a valid key is configured.
        IConfiguration config = CreateConfig(
            endpoint: "https://contoso.openai.azure.com",
            useManagedIdentity: false,
            apimSubscriptionKey: "apim-sub-key",
            apiKey: "direct-key");

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*APIM AI Gateway*");
    }

    [Fact]
    public void Load_AiGatewayInvariant_IgnoresAllowDirectEndpointForNonAbsoluteUrls()
    {
        // The escape hatch never rescues a non-absolute URL — that's a
        // misconfiguration regardless of environment or intent.
        var data = new Dictionary<string, string?>
        {
            ["OpenAI:Endpoint"] = "/inference",
            ["OpenAI:UseManagedIdentity"] = "true",
            ["OpenAI:AllowDirectEndpoint"] = "true",
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*absolute http(s) URL*");
    }

    [Fact]
    public void Load_AiGatewayInvariant_IgnoresAllowDirectEndpointForOpenAiSuffix()
    {
        // The escape hatch also never rescues an /openai-suffixed endpoint —
        // that's a runtime-request failure waiting to happen (regression #55)
        // regardless of environment or intent.
        var data = new Dictionary<string, string?>
        {
            ["OpenAI:Endpoint"] = "https://apim.example.com/inference/openai",
            ["OpenAI:UseManagedIdentity"] = "true",
            ["OpenAI:AllowDirectEndpoint"] = "true",
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*openai*");
    }

    [Fact]
    public void Load_AcceptsCanonicalApimInferenceEndpoint()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://apim-abc.azure-api.net/inference",
            useManagedIdentity: false,
            apimSubscriptionKey: "apim-sub-key");

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        settings.Endpoint.Should().Be("https://apim-abc.azure-api.net/inference");
        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ApiKey);
        settings.ApiKeySource.Should().Be("OpenAI:ApimSubscriptionKey");
    }

    private static IConfiguration CreateConfig(
        string? endpoint = null,
        bool? useManagedIdentity = null,
        string? apiKey = null,
        string? apimSubscriptionKey = null)
    {
        var data = new Dictionary<string, string?>();
        if (endpoint is not null) data["OpenAI:Endpoint"] = endpoint;
        if (useManagedIdentity.HasValue) data["OpenAI:UseManagedIdentity"] = useManagedIdentity.Value.ToString();
        if (apiKey is not null) data["OpenAI:ApiKey"] = apiKey;
        if (apimSubscriptionKey is not null) data["OpenAI:ApimSubscriptionKey"] = apimSubscriptionKey;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
    }

    private static IHostEnvironment CreateEnvironment(bool isDevelopment)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName)
            .Returns(isDevelopment ? Environments.Development : Environments.Production);
        return environment.Object;
    }
}
