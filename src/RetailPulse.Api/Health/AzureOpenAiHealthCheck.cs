using Microsoft.Extensions.Diagnostics.HealthChecks;
using RetailPulse.Api.OpenAI;

namespace RetailPulse.Api.Health;

/// <summary>
/// Health check that validates Azure OpenAI client connectivity by listing models.
/// Uses the configured OpenAI endpoint to verify the service is reachable.
/// </summary>
public class AzureOpenAiHealthCheck : IHealthCheck
{
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

            HttpClient client = _httpClientFactory.CreateClient();
            bool useManagedIdentity = _configuration.GetValue("OpenAI:UseManagedIdentity", false);
            string? apiKey = OpenAiConnectionSettings.ResolveConfiguredApiKey(_configuration);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/models");
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
