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
            endpoint: "https://gateway.example.com/inference/openai",
            useManagedIdentity: false,
            apiKey: "direct-key",
            apimSubscriptionKey: "apim-sub-key");

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        settings.Endpoint.Should().Be("https://gateway.example.com/inference/openai");
        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ApiKey);
        settings.ApiKey.Should().Be("apim-sub-key");
        settings.ApiKeySource.Should().Be("OpenAI:ApimSubscriptionKey");
    }

    [Fact]
    public void Load_FallsBackToDirectApiKey_WhenApimSubscriptionKeyMissing()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://contoso.openai.azure.com",
            useManagedIdentity: false,
            apiKey: "direct-key");

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

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

        var settings = OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        settings.AuthenticationMode.Should().Be(OpenAiAuthenticationMode.ManagedIdentity);
        settings.ApiKey.Should().BeNull();
        settings.ApiKeySource.Should().Be("ManagedIdentity");
    }

    [Fact]
    public void Load_UsesDevelopmentFallbackKey_WhenManagedIdentityDisabledAndNoKeyConfigured()
    {
        IConfiguration config = CreateConfig(
            endpoint: "https://gateway.example.com/inference/openai",
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
            endpoint: "https://gateway.example.com/inference/openai",
            useManagedIdentity: false);

        Action act = () => OpenAiConnectionSettings.Load(config, CreateEnvironment(isDevelopment: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenAI:ApimSubscriptionKey*OpenAI:ApiKey*");
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
