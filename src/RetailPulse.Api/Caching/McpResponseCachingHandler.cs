using Microsoft.Extensions.Caching.Memory;
using RetailPulse.Api.Telemetry;

namespace RetailPulse.Api.Caching;

/// <summary>
/// DelegatingHandler that caches successful GET responses from the MCP server.
/// Uses IMemoryCache with a configurable TTL (default 60s).
/// Only caches 200 OK responses. Non-GET requests pass through uncached.
/// </summary>
public class McpResponseCachingHandler : DelegatingHandler
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<McpResponseCachingHandler> _logger;
    private readonly TimeSpan _ttl;
    private readonly RetailPulseMetrics? _metrics;

    public McpResponseCachingHandler(
        IMemoryCache cache,
        ILogger<McpResponseCachingHandler> logger,
        TimeSpan? ttl = null,
        RetailPulseMetrics? metrics = null)
    {
        _cache = cache;
        _logger = logger;
        _ttl = ttl ?? TimeSpan.FromSeconds(60);
        _metrics = metrics;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only cache GET requests
        if (request.Method != HttpMethod.Get)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        string cacheKey = $"mcp:{request.RequestUri}";

        if (_cache.TryGetValue(cacheKey, out CachedMcpResponse? cached) && cached is not null)
        {
            _logger.LogDebug("MCP cache hit for {Uri}", request.RequestUri);
            _metrics?.RecordCacheHit();
            var cachedResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(cached.Body, System.Text.Encoding.UTF8, cached.ContentType)
            };
            cachedResponse.Headers.Add("X-MCP-Cache", "hit");
            return cachedResponse;
        }

        _metrics?.RecordCacheMiss();
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

            _cache.Set(cacheKey, new CachedMcpResponse(body, contentType), _ttl);
            _logger.LogDebug("MCP cache set for {Uri}, TTL={Ttl}s", request.RequestUri, _ttl.TotalSeconds);

            // Re-create response since we consumed the content
            var freshResponse = new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType)
            };
            // Copy relevant headers
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
                freshResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
            freshResponse.Headers.Add("X-MCP-Cache", "miss");
            return freshResponse;
        }

        return response;
    }

    private sealed record CachedMcpResponse(string Body, string ContentType);
}
