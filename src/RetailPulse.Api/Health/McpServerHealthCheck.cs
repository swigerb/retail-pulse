using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RetailPulse.Api.Health;

/// <summary>
/// Health check that pings the MCP server's /health endpoint to verify connectivity.
/// </summary>
public class McpServerHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpServerHealthCheck> _logger;

    public McpServerHealthCheck(IHttpClientFactory httpClientFactory, ILogger<McpServerHealthCheck> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = _httpClientFactory.CreateClient("McpServer");
            HttpResponseMessage response = await client.GetAsync("/health", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("MCP server is reachable.");
            }

            _logger.LogWarning("MCP server health check returned {StatusCode}", response.StatusCode);
            return HealthCheckResult.Degraded(
                $"MCP server returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP server health check failed");
            return HealthCheckResult.Unhealthy("MCP server is unreachable.", ex);
        }
    }
}
