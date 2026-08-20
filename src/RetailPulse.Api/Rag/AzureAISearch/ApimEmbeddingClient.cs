using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag.AzureAISearch;

/// <summary>
/// Embeds text via the APIM AI Gateway using the Azure OpenAI embeddings API
/// shape (<c>/openai/deployments/{deployment}/embeddings</c>). Routing through
/// APIM is a hard requirement — it keeps embedding tokens metered,
/// rate-limited, and audited under the same policy that governs completions.
///
/// Every successful call raises an <see cref="UsageEvent"/> on the shared
/// <see cref="ICostTracker"/> so embedding token spend rolls into the same
/// cost dashboard as chat completions.
/// </summary>
public sealed class ApimEmbeddingClient
{
    /// <summary>Named <see cref="HttpClient"/> registration for embeddings.</summary>
    public const string HttpClientName = "KnowledgeEmbeddings";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AzureAISearchOptions _options;
    private readonly CognitiveServicesTokenProvider? _tokenProvider;
    private readonly ICostTracker _costTracker;
    private readonly ILogger<ApimEmbeddingClient> _logger;

    public ApimEmbeddingClient(
        HttpClient http,
        AzureAISearchOptions options,
        ICostTracker costTracker,
        ILogger<ApimEmbeddingClient> logger,
        CognitiveServicesTokenProvider? tokenProvider = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Embeds a single string. Wraps transport failures in
    /// <see cref="KnowledgeProviderUnavailableException"/> so the degradation
    /// policy can react — never returns an empty / zero vector to signal
    /// outage.
    /// </summary>
    public async Task<ReadOnlyMemory<float>> EmbedAsync(string input, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        IReadOnlyList<ReadOnlyMemory<float>> vectors = await EmbedBatchAsync([input], ct).ConfigureAwait(false);
        return vectors[0];
    }

    /// <summary>
    /// Embeds a batch of strings in a single APIM call. Batch order is
    /// preserved. Empty batch returns an empty list without a network call.
    /// </summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return [];
        }

        AzureAISearchEmbeddingsOptions embeddings = _options.Embeddings;
        string path = $"openai/deployments/{Uri.EscapeDataString(embeddings.Deployment)}/embeddings?api-version={Uri.EscapeDataString(embeddings.ApiVersion)}";

        using HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Content = JsonContent.Create(
            new EmbeddingRequestBody(inputs, embeddings.Dimensions),
            options: _jsonOptions);

        if (embeddings.UseManagedIdentity && _tokenProvider is not null)
        {
            string token = await _tokenProvider.GetBearerAsync(ct).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrWhiteSpace(embeddings.ApimSubscriptionKey))
        {
            // APIM inference APIs authenticate the caller via api-key. When
            // provided we always send it — it lets local dev bypass MI while
            // preserving APIM's token metering and diagnostics.
            request.Headers.Remove("api-key");
            request.Headers.Add("api-key", embeddings.ApimSubscriptionKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new KnowledgeProviderUnavailableException(
                AzureAISearchKnowledgeBase.ProviderName,
                $"Embedding request to APIM AI Gateway failed at transport: {ex.Message}",
                ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new KnowledgeProviderUnavailableException(
                AzureAISearchKnowledgeBase.ProviderName,
                "Embedding request to APIM AI Gateway timed out.",
                ex);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await ReadShortBodyAsync(stream, ct).ConfigureAwait(false);
            if (IsTransport(response.StatusCode))
            {
                throw new KnowledgeProviderUnavailableException(
                    AzureAISearchKnowledgeBase.ProviderName,
                    $"Embedding request returned {(int)response.StatusCode} {response.StatusCode}: {body}");
            }

            throw new HttpRequestException(
                $"Embedding request returned {(int)response.StatusCode} {response.StatusCode}: {body}");
        }

        EmbeddingResponseBody? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<EmbeddingResponseBody>(
                stream, _jsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new KnowledgeProviderUnavailableException(
                AzureAISearchKnowledgeBase.ProviderName,
                "Embedding response was not valid JSON.",
                ex);
        }

        if (payload?.Data is null || payload.Data.Count != inputs.Count)
        {
            throw new KnowledgeProviderUnavailableException(
                AzureAISearchKnowledgeBase.ProviderName,
                $"Embedding response returned {payload?.Data?.Count ?? 0} vectors for {inputs.Count} inputs.");
        }

        int promptTokens = payload.Usage?.PromptTokens ?? 0;
        int totalTokens = payload.Usage?.TotalTokens ?? promptTokens;
        await RecordCostAsync(embeddings, promptTokens, totalTokens, ct).ConfigureAwait(false);

        // Order-preserve by index because Azure OpenAI does not guarantee
        // response-item order without honouring the "index" field.
        var result = new ReadOnlyMemory<float>[inputs.Count];
        foreach (EmbeddingResponseItem item in payload.Data)
        {
            if (item.Index < 0 || item.Index >= result.Length)
            {
                throw new KnowledgeProviderUnavailableException(
                    AzureAISearchKnowledgeBase.ProviderName,
                    $"Embedding response referenced index {item.Index} outside batch of {inputs.Count}.");
            }
            if (item.Embedding is null || item.Embedding.Length != embeddings.Dimensions)
            {
                throw new KnowledgeProviderUnavailableException(
                    AzureAISearchKnowledgeBase.ProviderName,
                    $"Embedding response returned vector of length {item.Embedding?.Length ?? 0}, expected {embeddings.Dimensions}. Confirm Knowledge:AzureAISearch:Embeddings:Dimensions matches the deployment.");
            }
            result[item.Index] = item.Embedding;
        }

        return result;
    }

    private Task RecordCostAsync(AzureAISearchEmbeddingsOptions embeddings, int promptTokens, int totalTokens, CancellationToken ct)
    {
        int inputTokens = promptTokens > 0 ? promptTokens : totalTokens;
        if (inputTokens <= 0)
        {
            _logger.LogDebug("Embedding response did not report token usage — skipping cost event.");
            return Task.CompletedTask;
        }

        var usage = new UsageEvent(
            AgentId: embeddings.CostTrackingAgentId,
            Model: embeddings.ResolveModelId(),
            InputTokens: inputTokens,
            OutputTokens: 0,
            ToolName: "embeddings",
            Timestamp: DateTime.UtcNow);

        return _costTracker.TrackUsageAsync(usage, ct);
    }

    private static bool IsTransport(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == HttpStatusCode.TooManyRequests ||
        (int)status >= 500;

    private static async Task<string> ReadShortBodyAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        char[] buffer = new char[512];
        int read = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
        return new string(buffer, 0, read).Trim();
    }

    private sealed record EmbeddingRequestBody(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("dimensions")] int? Dimensions);

    private sealed class EmbeddingResponseBody
    {
        [JsonPropertyName("data")]
        public List<EmbeddingResponseItem>? Data { get; set; }

        [JsonPropertyName("usage")]
        public EmbeddingUsage? Usage { get; set; }
    }

    private sealed class EmbeddingResponseItem
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }

    private sealed class EmbeddingUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
