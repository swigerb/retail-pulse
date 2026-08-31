using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailPulse.Api.Resilience;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Registers the Content Safety second layer. When
/// <see cref="ContentSafetyConfig.Enabled"/> is <c>false</c> (the default) the
/// tree is a single <see cref="NoOpContentSafetyEvaluator"/> registration with
/// no Azure client or HTTP handler wired — startup cannot fail from a missing
/// endpoint or credential in that state, which is what keeps the disabled path
/// byte-for-byte equal to today's behaviour.
/// </summary>
public static class ContentSafetyServiceCollectionExtensions
{
    /// <summary>
    /// Named <see cref="HttpClient"/> registration shared by the Prompt Shields
    /// raw HTTP path and, via <see cref="HttpClientTransport"/>, the SDK
    /// <see cref="ContentSafetyClient"/>. Sharing the client puts both failure
    /// classes behind one resilience pipeline so the timeout, circuit breaker,
    /// and <see cref="CircuitBreakerHealthCheck"/> report unified
    /// state.
    /// </summary>
    public const string HttpClientName = "ContentSafety";

    /// <summary>
    /// Registers <see cref="IContentSafetyEvaluator"/> and the tool-result seam
    /// so <see cref="Budget.BudgetedAIFunction"/> can inspect tool payloads
    /// without any change under <c>src/RetailPulse.Api/Agents/**</c>.
    /// </summary>
    public static IServiceCollection AddContentSafety(
        this IServiceCollection services,
        ContentSafetyConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // The tool-result inspector is always registered so tests and future
        // callers get a resolvable service; when disabled it short-circuits
        // internally to a no-op.
        services.TryAddSingleton<ContentSafetyToolResultInspector>();

        if (!config.Enabled)
        {
            services.TryAddSingleton<IContentSafetyEvaluator>(NoOpContentSafetyEvaluator.Instance);
            return services;
        }

        AzureContentSafetyEvaluator.ValidateEndpoint(config.Endpoint);
        var endpoint = new Uri(config.Endpoint!, UriKind.Absolute);
        int timeoutMs = Math.Max(200, config.TimeoutMs);

        // One TokenCredential for the whole process. Kept as TokenCredential
        // rather than DefaultAzureCredential so tests can substitute a
        // deterministic credential and the Prompt Shields authentication tests
        // can assert the exact bearer token is on the wire.
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.TryAddSingleton<ContentSafetyTokenProvider>();

        // Shared resilient client — Polly timeout + circuit breaker.
        // Both the Prompt Shields raw path and the SDK path route through this
        // client, so a Content Safety outage trips one breaker.
        services.AddHttpClient(HttpClientName, client =>
        {
            client.BaseAddress = endpoint;
            // Bounded per-attempt timeout guards against the SocketsHttpHandler default of
            // 100 s — the resilience pipeline's Timeout strategy still owns request-level
            // cancellation, but a mismatched HttpClient default is a common footgun.
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs * 4);
        }).AddContentSafetyResilienceHandler(timeoutMs);

        services.TryAddSingleton(sp =>
        {
            IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient http = factory.CreateClient(HttpClientName);
            TokenCredential credential = sp.GetRequiredService<TokenCredential>();
            var options = new ContentSafetyClientOptions
            {
                // HttpClientTransport wraps the shared HttpClient WITHOUT taking
                // ownership of its lifetime — the factory owns disposal. This
                // routes AnalyzeTextAsync through the exact same handler chain
                // as the raw Prompt Shields call so both breaker paths agree.
                Transport = new HttpClientTransport(http),
            };
            return new ContentSafetyClient(endpoint, credential, options);
        });

        services.TryAddSingleton<IContentSafetyEvaluator>(sp =>
        {
            IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient http = factory.CreateClient(HttpClientName);
            return new AzureContentSafetyEvaluator(
                sp.GetRequiredService<ContentSafetyClient>(),
                http,
                sp.GetRequiredService<ContentSafetyTokenProvider>(),
                sp.GetRequiredService<GuardrailsConfig>(),
                sp.GetRequiredService<ILogger<AzureContentSafetyEvaluator>>());
        });

        // Prime the managed-identity token and the HTTPS connection at host start so
        // the first runtime scan pays neither the cold AAD/IMDS round-trip nor the
        // TLS handshake inside its own timeout. Time-boxed and fire-and-forget, so it
        // cannot delay startup.
        services.TryAddSingleton<ContentSafetyWarmUpService>();
        services.AddHostedService(sp => sp.GetRequiredService<ContentSafetyWarmUpService>());

        return services;
    }
}
