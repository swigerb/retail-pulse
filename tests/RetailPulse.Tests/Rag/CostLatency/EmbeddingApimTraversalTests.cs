using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Rag.CostLatency;

/// <summary>
/// Issue #107 cost / APIM traversal parity gate. Complements the existing
/// <c>ApimEmbeddingClientTests</c> by pinning the invariants that make
/// Azure AI Search compliant with the platform's tenant-wide APIM policy:
///
/// <list type="number">
///   <item>Every embedding call carries the APIM subscription key on the
///     wire (<c>api-key</c> header), proving traffic really traverses APIM
///     rather than bypassing it.</item>
///   <item>The URL path targets the AOAI-shape endpoint on the APIM host
///     (never <c>*.openai.azure.com</c> directly).</item>
///   <item>Reported prompt tokens raise a <see cref="UsageEvent"/> on the
///     shared <see cref="ICostTracker"/>, so embeddings roll into the same
///     cost budget as chat completions (ADR-006 budget).</item>
/// </list>
/// </summary>
public sealed class EmbeddingApimTraversalTests
{
    [Fact]
    public async Task Embed_TraversesApim_SendsApiKeyHeader_AndRecordsTokens()
    {
        (ApimEmbeddingClient client, ApimCapturingHandler handler, InMemoryCostTracker tracker) = CreateClient();

        await client.EmbedAsync("category management retail merchandising", CancellationToken.None);

        // APIM traversal proof: api-key header present on the wire.
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.TryGetValues("api-key", out IEnumerable<string>? keys).Should().BeTrue(
            "APIM subscription-key traversal is a hard requirement — the AzureAISearch provider must NEVER bypass APIM");
        keys!.Single().Should().Be("apim-test-key");

        // Path proof: AOAI shape hosted at the APIM base URL.
        Uri uri = handler.LastRequest.RequestUri!;
        uri.Host.Should().Be("apim.example.com", "the request MUST traverse the tenant APIM host, not a direct AOAI endpoint");
        uri.AbsolutePath.Should().Contain("openai/deployments/text-embedding-3-small/embeddings");

        // Cost telemetry proof: the reported prompt tokens produced a
        // UsageEvent on the shared cost tracker (ADR-006 budget).
        CostSummary summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().Be(1);
        summary.TotalTokens.Should().Be(64);
    }

    private static (ApimEmbeddingClient, ApimCapturingHandler, InMemoryCostTracker) CreateClient()
    {
        var options = new AzureAISearchOptions
        {
            Endpoint = "https://mysearch.search.windows.net",
        };
        options.Embeddings.Endpoint = "https://apim.example.com/inference";
        options.Embeddings.Deployment = "text-embedding-3-small";
        options.Embeddings.Dimensions = 8;
        options.Embeddings.UseManagedIdentity = false;
        options.Embeddings.ApimSubscriptionKey = "apim-test-key";

        string body = BuildSuccessJson(dim: 8, inputs: 1, promptTokens: 64);
        var handler = new ApimCapturingHandler(body);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://apim.example.com/inference/", UriKind.Absolute),
        };
        var tracker = new InMemoryCostTracker(
            Options.Create(new ObservabilityOptions { MaxCostEvents = 100, CostEventTtlHours = 24 }),
            new ConfigurationBuilder().Build());
        var client = new ApimEmbeddingClient(
            http, options, tracker,
            NullLoggerFactory.Instance.CreateLogger<ApimEmbeddingClient>());
        return (client, handler, tracker);
    }

    private static string BuildSuccessJson(int dim, int inputs, int promptTokens)
    {
        var items = Enumerable.Range(0, inputs).Select(i => new
        {
            index = i,
            embedding = Enumerable.Range(0, dim).Select(v => (float)(((i + 1) * 0.01f) + (v * 0.001f))).ToArray(),
        });
        var payload = new
        {
            data = items,
            usage = new { prompt_tokens = promptTokens, total_tokens = promptTokens },
        };
        return JsonSerializer.Serialize(payload);
    }

    private sealed class ApimCapturingHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
