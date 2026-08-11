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

        // AI Gateway invariant — outside Development the API must route inference
        // through the APIM AI Gateway. A direct Azure OpenAI / Cognitive Services
        // endpoint bypasses the gateway's token limits, MI-backed backend auth,
        // token-emit metrics, and LLM diagnostics — every observability + cost
        // guarantee the deployment contract makes. The escape hatch
        // `OpenAI:AllowDirectEndpoint=true` exists only for explicit, deliberate
        // safe local-dev scenarios (e.g. running the API against a local AOAI
        // resource without APIM in front) and is refused in Development just as
        // clearly by logs — never a silent fallback.
        //
        // We also refuse any endpoint that carries a doubled `/openai/openai`
        // path — the exact defect from incident #55 — so a regression in the
        // Bicep output expression or a hand-set env-var can't sneak back in
        // and produce silent 404s at request time.
        bool allowDirect = configuration.GetValue("OpenAI:AllowDirectEndpoint", false);
        EnforceAiGatewayInvariant(endpoint, environment, allowDirect);

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

    // Internal for direct test access — the invariant is a hard startup gate and
    // needs unit coverage independent of the full Load() config pipeline.
    internal static void EnforceAiGatewayInvariant(
        string endpoint,
        IHostEnvironment environment,
        bool allowDirectEndpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"Configuration value 'OpenAI:Endpoint' ('{endpoint}') must be an absolute URL.");
        }

        // Doubled `/openai/openai` — the P0 incident #55 shape. `AzureOpenAIClient`
        // appends `/openai/deployments/...` itself, so an endpoint that already ends
        // in `/openai` produces a doubled segment at request time and 404s from APIM
        // with `OperationNotFound`. Refuse it regardless of environment or escape
        // hatches — there is no legitimate deployment shape that requires this.
        string path = uri.AbsolutePath ?? string.Empty;
        if (path.Contains("/openai/openai", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration value 'OpenAI:Endpoint' ('{endpoint}') ends in '/openai' — the Azure OpenAI SDK independently appends '/openai/deployments/...' to whatever endpoint it is given, so this produces a doubled '/openai/openai' segment at request time (regression of incident #55). Point 'OpenAI:Endpoint' at the APIM inference API base (e.g. 'https://<apim>.azure-api.net/inference') and let the SDK append the '/openai' suffix.");
        }

        bool isDirectAzureOpenAiHost = IsDirectAzureOpenAiHost(uri.Host);
        if (!isDirectAzureOpenAiHost)
        {
            return;
        }

        if (environment.IsDevelopment())
        {
            return;
        }

        if (allowDirectEndpoint)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Configuration value 'OpenAI:Endpoint' ('{endpoint}') points at a direct Azure OpenAI / Cognitive Services host outside Development. The API must route inference through the APIM AI Gateway (e.g. 'https://<apim>.azure-api.net/inference'). Set OpenAI:AllowDirectEndpoint=true only for an explicit safe local-dev scenario.");
    }

    private static bool IsDirectAzureOpenAiHost(string host)
    {
        return !string.IsNullOrEmpty(host)
            && (host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase));
    }
}
