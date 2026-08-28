using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Caching;

namespace RetailPulse.Api.Middleware;

/// <summary>
/// Wraps IChatClient to emit tokens progressively via SignalR.
/// Uses IAsyncEnumerable from the chat client's streaming support.
/// Falls back to full response if streaming is not supported by the model.
/// </summary>
public class StreamingMiddleware
{
    private readonly IHubContext<StreamingHub> _hubContext;
    private readonly ILogger<StreamingMiddleware> _logger;

    public StreamingMiddleware(
        IHubContext<StreamingHub> hubContext,
        ILogger<StreamingMiddleware> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Streams a chat response via SignalR, emitting "streaming:token" events.
    /// Returns the fully assembled response text.
    /// </summary>
    public async Task<string> StreamResponseAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions? options,
        string sessionId,
        string agentId,
        CancellationToken ct = default)
    {
        using Activity? activity = AgentTelemetry.Source.StartActivity("streaming.response", ActivityKind.Internal);
        activity?.SetTag("streaming.session_id", sessionId);
        activity?.SetTag("streaming.agent_id", agentId);

        try
        {
            // Signal streaming start
            await StreamingEvents.SendStartAsync(_hubContext, sessionId, agentId);

            var fullResponse = new StringBuilder();
            int tokenIndex = 0;

            await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(messages, options, ct))
            {
                if (update.Text is { Length: > 0 })
                {
                    fullResponse.Append(update.Text);
                    await StreamingEvents.SendTokenAsync(_hubContext, sessionId, update.Text, tokenIndex++);
                }
            }

            string responseText = fullResponse.ToString();
            await StreamingEvents.SendCompleteAsync(_hubContext, sessionId, responseText, fromCache: false);

            activity?.SetTag("streaming.tokens_emitted", tokenIndex);
            activity?.SetTag("streaming.response_length", responseText.Length);

            return responseText;
        }
        catch (NotSupportedException)
        {
            _logger.LogInformation("Streaming not supported by model — falling back for session {SessionId}", sessionId);
            activity?.SetTag("streaming.fallback", true);
            return await FallbackToFullResponseAsync(chatClient, messages, options, sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Streaming failed for session {SessionId} — falling back", sessionId);
            activity?.SetTag("streaming.fallback", true);
            return await FallbackToFullResponseAsync(chatClient, messages, options, sessionId, ct);
        }
    }

    /// <summary>
    /// Returns an IAsyncEnumerable that yields tokens for SSE-style streaming endpoints.
    /// </summary>
    public async IAsyncEnumerable<StreamingToken> StreamTokensAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions? options,
        string sessionId,
        string agentId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await StreamingEvents.SendStartAsync(_hubContext, sessionId, agentId);
        int tokenIndex = 0;

        await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(messages, options, ct))
        {
            if (update.Text is { Length: > 0 })
            {
                await StreamingEvents.SendTokenAsync(_hubContext, sessionId, update.Text, tokenIndex++);
                yield return new StreamingToken(update.Text, false);
            }
        }

        await StreamingEvents.SendCompleteAsync(_hubContext, sessionId, "", fromCache: false);
        yield return new StreamingToken("", true);
    }

    private async Task<string> FallbackToFullResponseAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions? options,
        string sessionId,
        CancellationToken ct)
    {
        ChatResponse response = await chatClient.GetResponseAsync(messages, options, ct);
        string text = response.Text ?? "";

        await StreamingEvents.SendCompleteAsync(_hubContext, sessionId, text, fromCache: false);
        return text;
    }

    /// <summary>
    /// Pushes a pre-computed response through SignalR as simulated streaming tokens.
    /// Used when the agent pipeline already produced the full response but the
    /// client is listening on the streaming channel.
    /// </summary>
    public async Task StreamResponseFallbackAsync(
        string sessionId,
        string agentId,
        string fullResponse,
        CancellationToken ct = default)
    {
        await StreamingEvents.SendStartAsync(_hubContext, sessionId, agentId);

        // Emit the full response in word-boundary chunks for a streaming UX
        string[] words = fullResponse.Split(' ');
        int tokenIndex = 0;
        foreach (string word in words)
        {
            ct.ThrowIfCancellationRequested();
            string token = tokenIndex == 0 ? word : " " + word;
            await StreamingEvents.SendTokenAsync(_hubContext, sessionId, token, tokenIndex++);
        }

        await StreamingEvents.SendCompleteAsync(_hubContext, sessionId, fullResponse, fromCache: false);
    }
}

/// <summary>
/// A single streaming token emitted during progressive response generation.
/// </summary>
public record StreamingToken(string Token, bool IsComplete);

/// <summary>
/// Static helpers for cache key generation, query normalization, and
/// deterministic detection — used by the chat pipeline for cache middleware.
/// </summary>
public static partial class CacheHelpers
{
    /// <summary>
    /// Keywords indicating non-deterministic queries — never cache these.
    /// </summary>
    private static readonly string[] _nonDeterministicKeywords =
    [
        "forecast", "predict", "recommend", "suggest", "what should",
        "what would", "what if", "estimate future", "project forward",
        "next quarter", "next month", "next year", "trending",
        "will it", "should i", "should we", "opinion", "advice"
    ];

    /// <summary>
    /// Generates a deterministic cache key from agent ID and normalized query.
    /// </summary>
    public static string BuildCacheKey(string agentId, string query)
    {
        string normalized = NormalizeQuery(query);
        string raw = $"{agentId}|{normalized}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Normalizes a query: lowercase, trim, remove punctuation variations, collapse whitespace.
    /// </summary>
    public static string NormalizeQuery(string query)
    {
        string result = query.Trim().ToLowerInvariant();
        result = TrailingPunctuationRegex().Replace(result, "");
        result = WhitespaceRegex().Replace(result, " ");
        return result;
    }

    [GeneratedRegex(@"[\?\!\.\,\;\:]+$")]
    private static partial Regex TrailingPunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Returns true if the query is deterministic/factual (cacheable).
    /// Forecasts, recommendations, and opinion-based queries are never cached.
    /// </summary>
    public static bool IsCacheable(string query)
    {
        string lower = query.ToLowerInvariant();
        return !_nonDeterministicKeywords.Any(kw => lower.Contains(kw));
    }

    /// <summary>
    /// Returns true when a produced answer is good enough to serve to somebody else.
    /// </summary>
    /// <remarks>
    /// A cacheable QUESTION is not the same thing as a cacheable ANSWER. Responses were
    /// stored unconditionally, so a degraded reply was replayed for the full TTL and one
    /// transient model wobble became a prompt that failed identically for every later
    /// visitor. Observed live: a curated chart prompt returning "Chart unavailable" on a
    /// <c>cache.hit</c> span with no tool calls at all.
    ///
    /// Two things disqualify an answer:
    /// <list type="bullet">
    ///   <item>The pipeline flagged it as an error (a ⏳ or ⚠️ prefixed reply).</item>
    ///   <item>The reply narrates a chart it did not actually produce. A request that
    ///     asked for a chart and came back with none is a failure worth retrying, not a
    ///     result worth keeping.</item>
    /// </list>
    /// </remarks>
    public static bool IsCacheableOutcome(string reply, int chartCount, bool isErrorResponse, bool chartWasRequested)
    {
        if (isErrorResponse) return false;
        if (string.IsNullOrWhiteSpace(reply)) return false;

        // The pipeline's own fail-closed diagnostics. These are legitimate, honest
        // responses — they just must not be the permanent answer to that prompt.
        return !reply.Contains("Chart unavailable", StringComparison.OrdinalIgnoreCase)
            && !reply.Contains("wasn't able to generate a response", StringComparison.OrdinalIgnoreCase)
            && (!chartWasRequested || chartCount > 0);
    }
}
