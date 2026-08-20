using Azure.AI.ContentSafety;
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

        services.TryAddSingleton(sp => new ContentSafetyClient(endpoint, new DefaultAzureCredential()));

        services.AddHttpClient<IContentSafetyEvaluator, AzureContentSafetyEvaluator>(client =>
        {
            client.BaseAddress = endpoint;
            // Bounded per-attempt timeout guards against the SocketsHttpHandler default of
            // 100 s — the resilience pipeline's Timeout strategy still owns request-level
            // cancellation, but a mismatched HttpClient default is a common footgun.
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs * 2);
        }).AddContentSafetyResilienceHandler(timeoutMs);

        return services;
    }
}
