using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Rag.AzureAISearch;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Tests.Rag.AzureAISearch;

/// <summary>
/// Embeddings client contract:
/// <list type="bullet">
///   <item>Sends the request to the APIM AI Gateway using the AOAI-style path.</item>
///   <item>Wraps transport failures in <see cref="KnowledgeProviderUnavailableException"/>.</item>
///   <item>Never returns an empty / zero vector on failure — degradation is the wrapper's job.</item>
///   <item>Rejects dimension mismatch loud.</item>
///   <item>Emits a <see cref="UsageEvent"/> per successful call so embedding tokens are metered.</item>
/// </list>
/// </summary>
public class ApimEmbeddingClientTests
{
    [Fact]
    public async Task Embed_SendsCorrectPath_AndRecordsCost()
    {
        (ApimEmbeddingClient client, CapturingHandler handler, InMemoryCostTracker tracker) = CreateClient(
            deploymentReplyMs: 0,
            respondJson: BuildSuccessJson(dim: 8, inputs: 1, promptTokens: 42));

        ReadOnlyMemory<float> vec = await client.EmbedAsync("hello world", CancellationToken.None);

        vec.Length.Should().Be(8);
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.PathAndQuery.Should()
            .Contain("openai/deployments/text-embedding-3-small/embeddings")
            .And.Contain("api-version=2024-06-01");

        CostSummary summary = await tracker.GetSummaryAsync(CostPeriod.All);
        summary.RequestCount.Should().Be(1);
        summary.TotalTokens.Should().Be(42,
            "embedding prompt-token usage must roll into the cost dashboard so tokens are metered like completions");
    }

    [Fact]
    public async Task EmbedBatch_PreservesInputOrder_ByIndex()
    {
        // Return the response with items in reversed order — the client must
        // reorder by the index field so caller-order is preserved.
        string body = /*lang=json,strict*/ """
        {
          "data": [
            { "index": 1, "embedding": [0.1, 0.2, 0.3, 0.4] },
            { "index": 0, "embedding": [0.9, 0.8, 0.7, 0.6] }
          ],
          "usage": { "prompt_tokens": 5, "total_tokens": 5 }
        }
        """;
        (ApimEmbeddingClient client, _, _) = CreateClient(respondJson: body, dimensions: 4);

        IReadOnlyList<ReadOnlyMemory<float>> vectors = await client.EmbedBatchAsync(["first", "second"], CancellationToken.None);

        vectors[0].ToArray().Should().Equal(0.9f, 0.8f, 0.7f, 0.6f);
        vectors[1].ToArray().Should().Equal(0.1f, 0.2f, 0.3f, 0.4f);
    }

    [Fact]
    public async Task Embed_TransportFailure_WrapsAsProviderUnavailable()
    {
        (ApimEmbeddingClient client, CapturingHandler handler, _) = CreateClient(
            respondStatus: HttpStatusCode.ServiceUnavailable,
            respondJson: /*lang=json,strict*/ "{\"error\":\"backend unreachable\"}");

        Func<Task> act = () => client.EmbedAsync("hi", CancellationToken.None);

        await act.Should().ThrowAsync<KnowledgeProviderUnavailableException>()
            .Where(ex => ex.ProviderName == AzureAISearchKnowledgeBase.ProviderName);
    }

    [Fact]
    public async Task Embed_DimensionMismatch_FailsLoud()
    {
        (ApimEmbeddingClient client, _, _) = CreateClient(
            respondJson: BuildSuccessJson(dim: 3, inputs: 1, promptTokens: 1),
            dimensions: 8);

        Func<Task> act = () => client.EmbedAsync("hi", CancellationToken.None);

        await act.Should().ThrowAsync<KnowledgeProviderUnavailableException>()
            .WithMessage("*vector of length 3*expected 8*");
    }

    [Fact]
    public async Task EmbedBatch_Empty_ShortCircuits_NoHttpCall()
    {
        (ApimEmbeddingClient client, CapturingHandler handler, _) = CreateClient(
            respondJson: BuildSuccessJson(dim: 8, inputs: 1, promptTokens: 42));

        IReadOnlyList<ReadOnlyMemory<float>> result = await client.EmbedBatchAsync([], CancellationToken.None);

        result.Should().BeEmpty();
        handler.LastRequest.Should().BeNull(
            "an empty batch must not consume APIM tokens or hit the wire");
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

    private static (ApimEmbeddingClient client, CapturingHandler handler, InMemoryCostTracker tracker) CreateClient(
        string respondJson,
        HttpStatusCode respondStatus = HttpStatusCode.OK,
        int deploymentReplyMs = 0,
        int dimensions = 8)
    {
        var options = new AzureAISearchOptions
        {
            Endpoint = "https://mysearch.search.windows.net",
        };
        options.Embeddings.Endpoint = "https://apim.example.com/inference";
        options.Embeddings.Deployment = "text-embedding-3-small";
        options.Embeddings.Dimensions = dimensions;
        options.Embeddings.UseManagedIdentity = false;
        options.Embeddings.ApimSubscriptionKey = "test-key";

        var handler = new CapturingHandler(respondStatus, respondJson, deploymentReplyMs);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://apim.example.com/inference/", UriKind.Absolute),
        };

        var tracker = new InMemoryCostTracker(
            Options.Create(new ObservabilityOptions
            {
                MaxCostEvents = 100,
                CostEventTtlHours = 24,
            }),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var client = new ApimEmbeddingClient(
            http,
            options,
            tracker,
            NullLoggerFactory.Instance.CreateLogger<ApimEmbeddingClient>());

        return (client, handler, tracker);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body, int delayMs) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
