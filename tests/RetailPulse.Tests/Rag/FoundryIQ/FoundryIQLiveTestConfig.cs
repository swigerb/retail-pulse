namespace RetailPulse.Tests.Rag.FoundryIQ;

/// <summary>
/// Reads live Foundry IQ integration test configuration from environment
/// variables. When any required value is missing, tests using this helper
/// skip cleanly with an explicit reason recorded on the xunit output. See
/// docs/rag/foundry-iq-provider.md for the operator setup story.
///
/// Required env vars:
/// <list type="bullet">
///   <item><c>RETAIL_PULSE_FOUNDRY_IQ_ENDPOINT</c> — Foundry project endpoint.</item>
///   <item><c>RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_NAME</c> — Vector store name to bind.</item>
///   <item><c>RETAIL_PULSE_FOUNDRY_IQ_MODEL</c> — Foundry model deployment used by the retrieval agent.</item>
/// </list>
/// Optional env vars:
/// <list type="bullet">
///   <item><c>RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_ID</c> — Exact vs_... id (bypasses name lookup).</item>
///   <item><c>RETAIL_PULSE_FOUNDRY_IQ_RETRIEVAL_AGENT_NAME</c> (default: retail-pulse-foundry-iq-retrieval).</item>
///   <item><c>RETAIL_PULSE_FOUNDRY_IQ_RETRIEVAL_AGENT_ID</c> — Exact asst_... id (bypasses name lookup).</item>
/// </list>
/// </summary>
public static class FoundryIQLiveTestConfig
{
    public const string SkipReason =
        "Live Foundry IQ integration test skipped — set RETAIL_PULSE_FOUNDRY_IQ_ENDPOINT, RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_NAME, and RETAIL_PULSE_FOUNDRY_IQ_MODEL to run.";

    public static bool IsConfigured(out string? reason)
    {
        string? endpoint = Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_ENDPOINT");
        string? vectorStore = Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_NAME");
        string? model = Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_MODEL");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(vectorStore) || string.IsNullOrWhiteSpace(model))
        {
            reason = SkipReason;
            return false;
        }
        reason = null;
        return true;
    }

    public static string ResolveEndpoint() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_ENDPOINT")!;

    public static string ResolveVectorStoreName() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_NAME")!;

    public static string ResolveModel() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_MODEL")!;

    public static string? ResolveVectorStoreId() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_VECTOR_STORE_ID");

    public static string ResolveRetrievalAgentName() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_RETRIEVAL_AGENT_NAME")
            ?? "retail-pulse-foundry-iq-retrieval";

    public static string? ResolveRetrievalAgentId() =>
        Environment.GetEnvironmentVariable("RETAIL_PULSE_FOUNDRY_IQ_RETRIEVAL_AGENT_ID");
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips at test-discovery time when
/// the live Foundry IQ integration environment is not configured. The skip
/// reason is set from <see cref="FoundryIQLiveTestConfig.SkipReason"/> so
/// operators reading CI output see explicitly why the test did not run.
/// </summary>
public sealed class LiveFoundryIqFactAttribute : FactAttribute
{
    public LiveFoundryIqFactAttribute()
    {
        if (!FoundryIQLiveTestConfig.IsConfigured(out string? reason))
        {
            Skip = reason;
        }
    }
}
