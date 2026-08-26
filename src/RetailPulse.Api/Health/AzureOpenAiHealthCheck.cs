using Microsoft.Extensions.Diagnostics.HealthChecks;
using RetailPulse.Api.OpenAI;

namespace RetailPulse.Api.Health;

/// <summary>
/// Health check that validates Azure OpenAI inference connectivity by listing model
/// deployments. Probes the same route shape the Azure OpenAI SDK uses, so a pass proves
/// the whole configured inference path — DNS, TLS, the APIM subscription key, APIM's
/// managed-identity authentication to Azure AI Foundry, and the upstream account —
/// rather than merely that some host answered.
/// </summary>
public class AzureOpenAiHealthCheck : IHealthCheck
{
    /// <summary>Fallback api-version when <c>OpenAI:ApiVersion</c> is not configured.</summary>
    internal const string FallbackApiVersion = "2025-03-01-preview";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureOpenAiHealthCheck> _logger;

    public AzureOpenAiHealthCheck(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AzureOpenAiHealthCheck> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Builds the model-listing probe URL for a configured OpenAI endpoint.
    ///
    /// <para>
    /// <c>OpenAI:Endpoint</c> is the inference base — for a gateway deployment
    /// <c>https://{apim}.azure-api.net/inference</c>, for a direct account
    /// <c>https://{account}.cognitiveservices.azure.com</c>. In both cases the Azure
    /// OpenAI SDK appends its own <c>/openai/...</c> segment (see
    /// <see cref="OpenAiConnectionSettings"/>, which rejects an endpoint that already
    /// ends in <c>/openai</c> precisely because the SDK would double it). This probe
    /// composes the URL the same way so it exercises the real registered route.
    /// </para>
    ///
    /// <para>
    /// Probing <c>{endpoint}/models</c> — without the <c>/openai</c> segment — is what
    /// this check used to do, and it matched no APIM API at all: the inference API is
    /// registered at path <c>{inference}/openai</c>, so every gateway deployment
    /// reported a permanent <c>404</c> Degraded regardless of actual health.
    /// </para>
    /// </summary>
    internal static string BuildModelsProbeUrl(string endpoint, string apiVersion)
    {
        string trimmed = endpoint.TrimEnd('/');

        // Defensive: OpenAiConnectionSettings rejects an endpoint ending in "/openai"
        // outside Development, but a Development override must not produce a doubled
        // "/openai/openai" segment here either.
        if (!trimmed.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/openai";
        }

        return $"{trimmed}/models?api-version={Uri.EscapeDataString(apiVersion)}";
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            string? endpoint = _configuration["OpenAI:Endpoint"];
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return HealthCheckResult.Degraded("OpenAI endpoint is not configured.");
            }

            string apiVersion = _configuration["OpenAI:ApiVersion"] is { Length: > 0 } configured
                ? configured
                : FallbackApiVersion;

            HttpClient client = _httpClientFactory.CreateClient();
            bool useManagedIdentity = _configuration.GetValue("OpenAI:UseManagedIdentity", false);
            string? apiKey = OpenAiConnectionSettings.ResolveConfiguredApiKey(_configuration);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, BuildModelsProbeUrl(endpoint, apiVersion));
            if (!useManagedIdentity && !string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add(OpenAiConnectionSettings.ApimSubscriptionKeyHeaderName, apiKey);
            }

            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Azure OpenAI endpoint is reachable.");
            }

            // 401/403 means connectivity is fine but credentials may be wrong — still degraded, not unhealthy
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return HealthCheckResult.Degraded(
                    $"Azure OpenAI returned {(int)response.StatusCode} — credentials may be invalid.");
            }

            // 404 means the host answered but no route matched. That is a configuration
            // fault worth naming explicitly, because it is indistinguishable from a
            // healthy gateway unless the operator knows which URL was probed.
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning(
                    "Azure OpenAI health check returned 404 — no route matched the model-listing probe. Check that OpenAI:Endpoint is the inference base (no trailing '/openai') and that the gateway exposes the model-listing operation.");
                return HealthCheckResult.Degraded(
                    "Azure OpenAI returned HTTP 404 — the model-listing route was not found. Check OpenAI:Endpoint and the gateway's exposed operations.");
            }

            _logger.LogWarning("Azure OpenAI health check returned {StatusCode}", response.StatusCode);
            return HealthCheckResult.Degraded(
                $"Azure OpenAI returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure OpenAI health check failed");
            return HealthCheckResult.Unhealthy("Azure OpenAI endpoint is unreachable.", ex);
        }
    }
}
