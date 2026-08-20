namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// Reads live Azure AI Search integration test configuration from environment
/// variables. When any required value is missing, tests using this helper
/// skip cleanly with an explicit reason recorded on the xunit output.
///
/// Required env vars:
/// <list type="bullet">
///   <item><c>RETAIL_PULSE_AI_SEARCH_ENDPOINT</c> — Search service endpoint (https://...search.windows.net)</item>
///   <item><c>RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_ENDPOINT</c> — APIM inference base URL</item>
/// </list>
/// Optional env vars:
/// <list type="bullet">
///   <item><c>RETAIL_PULSE_AI_SEARCH_INDEX</c> (default: retail-pulse-live-tests)</item>
///   <item><c>RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_DEPLOYMENT</c> (default: text-embedding-3-small)</item>
///   <item><c>RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_APIM_KEY</c> (bypasses MI when set)</item>
///   <item><c>RETAIL_PULSE_AI_SEARCH_SEMANTIC</c> (true/false, default: false)</item>
/// </list>
/// </summary>
public static class AzureAISearchLiveTestConfig
{
    public const string SkipReason =
        "Live Azure AI Search integration test skipped — set RETAIL_PULSE_AI_SEARCH_ENDPOINT and RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_ENDPOINT to run.";

    public static bool IsConfigured(out string? reason)
    {
        string? endpoint = Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_ENDPOINT");
        string? embeddings = Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(embeddings))
        {
            reason = SkipReason;
            return false;
        }
        reason = null;
        return true;
    }

    public static string ResolveIndex() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_INDEX") ?? "retail-pulse-live-tests";

    public static string ResolveEmbeddingsDeployment() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_DEPLOYMENT") ?? "text-embedding-3-small";

    public static string? ResolveEmbeddingsApimKey() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_APIM_KEY");

    public static bool ResolveSemantic() =>
        string.Equals(
            Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_SEMANTIC")?.Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static string ResolveEndpoint() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_ENDPOINT")!;

    public static string ResolveEmbeddingsEndpoint() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_AI_SEARCH_EMBEDDINGS_ENDPOINT")!;
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips at test-discovery time when the
/// live Azure AI Search integration environment is not configured. The skip
/// reason is set from <see cref="AzureAISearchLiveTestConfig.SkipReason"/> so
/// operators reading the CI output see explicitly why the test did not run.
/// </summary>
public sealed class LiveAzureAISearchFactAttribute : FactAttribute
{
    public LiveAzureAISearchFactAttribute()
    {
        if (!AzureAISearchLiveTestConfig.IsConfigured(out string? reason))
        {
            Skip = reason;
        }
    }
}
