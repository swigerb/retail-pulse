using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Hosting;

namespace RetailPulse.Api.OpenAI;

internal enum OpenAiAuthenticationMode
{
    ManagedIdentity,
    ApiKey
}

internal sealed class OpenAiConnectionSettings
{
    internal const string ApimSubscriptionKeyHeaderName = "api-key";

    private OpenAiConnectionSettings(
        string endpoint,
        OpenAiAuthenticationMode authenticationMode,
        string? apiKey,
        string apiKeySource)
    {
        Endpoint = endpoint;
        AuthenticationMode = authenticationMode;
        ApiKey = apiKey;
        ApiKeySource = apiKeySource;
    }

    public string Endpoint { get; }

    public OpenAiAuthenticationMode AuthenticationMode { get; }

    public string? ApiKey { get; }

    public string ApiKeySource { get; }

    public static OpenAiConnectionSettings Load(IConfiguration configuration, IHostEnvironment environment)
    {
        string endpoint = TrimToNull(configuration["OpenAI:Endpoint"])
            ?? throw new InvalidOperationException(
                "Configuration value 'OpenAI:Endpoint' is required.");

        bool useManagedIdentity = configuration.GetValue("OpenAI:UseManagedIdentity", false);
        if (useManagedIdentity)
        {
            return new OpenAiConnectionSettings(
                endpoint,
                OpenAiAuthenticationMode.ManagedIdentity,
                apiKey: null,
                apiKeySource: "ManagedIdentity");
        }

        string? configuredApiKey = ResolveConfiguredApiKey(configuration, out string apiKeySource);
        if (string.IsNullOrWhiteSpace(configuredApiKey))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Configuration value 'OpenAI:ApimSubscriptionKey' or 'OpenAI:ApiKey' is required outside of Development when managed identity is disabled.");
            }

            configuredApiKey = "demo-key";
            apiKeySource = "DevelopmentFallback";
        }

        return new OpenAiConnectionSettings(
            endpoint,
            OpenAiAuthenticationMode.ApiKey,
            configuredApiKey,
            apiKeySource);
    }

    public static string? ResolveConfiguredApiKey(IConfiguration configuration) =>
        ResolveConfiguredApiKey(configuration, out _);

    public static string? ResolveConfiguredApiKey(IConfiguration configuration, out string apiKeySource)
    {
        string? apimSubscriptionKey = TrimToNull(configuration["OpenAI:ApimSubscriptionKey"]);
        if (apimSubscriptionKey is not null)
        {
            apiKeySource = "OpenAI:ApimSubscriptionKey";
            return apimSubscriptionKey;
        }

        string? apiKey = TrimToNull(configuration["OpenAI:ApiKey"]);
        apiKeySource = apiKey is null ? "None" : "OpenAI:ApiKey";
        return apiKey;
    }

    public AzureOpenAIClient CreateClient(AzureOpenAIClientOptions options) =>
        AuthenticationMode switch
        {
            OpenAiAuthenticationMode.ManagedIdentity => new AzureOpenAIClient(
                new Uri(Endpoint),
                new DefaultAzureCredential(),
                options),
            OpenAiAuthenticationMode.ApiKey => new AzureOpenAIClient(
                new Uri(Endpoint),
                new ApiKeyCredential(ApiKey!),
                options),
            _ => throw new InvalidOperationException($"Unsupported OpenAI authentication mode '{AuthenticationMode}'.")
        };

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
