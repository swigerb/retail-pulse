using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.ContentSafety;
using Polly.CircuitBreaker;
using Polly.Timeout;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Second-layer evaluator that calls Azure AI Content Safety through the
/// resilience pipeline. Text moderation uses the SDK's typed
/// <see cref="ContentSafetyClient"/>; Prompt Shields (jailbreak +
/// indirect-injection) use the shared <see cref="HttpClient"/> so the same
/// timeout + circuit breaker guard both paths.
/// </summary>
/// <remarks>
/// Authentication is <see cref="Azure.Identity.DefaultAzureCredential"/>-based.
/// The evaluator resolves the current
/// <see cref="ContentSafetyConfig"/> on every call from the DI-registered
/// <see cref="GuardrailsConfig"/> so runtime toggles (fail policy, thresholds,
/// stage enable flags) take effect without a restart.
/// </remarks>
internal sealed class AzureContentSafetyEvaluator : IContentSafetyEvaluator
{
    private readonly ContentSafetyClient _client;
    private readonly HttpClient _http;
    private readonly ContentSafetyTokenProvider _tokens;
    private readonly GuardrailsConfig _guardrails;
    private readonly ILogger<AzureContentSafetyEvaluator> _logger;

    // Prompt Shield surface — the 1.0.0 SDK does not expose a strongly typed
    // model for text:shieldPrompt yet, so this call is issued through the
    // resilience-instrumented HttpClient. The api-version tracks the Azure
    // Content Safety GA line and matches the version used by AnalyzeText.
    private const string _promptShieldPath =
        "/contentsafety/text:shieldPrompt?api-version=2024-09-01";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AzureContentSafetyEvaluator(
        ContentSafetyClient client,
        HttpClient http,
        ContentSafetyTokenProvider tokens,
        GuardrailsConfig guardrails,
        ILogger<AzureContentSafetyEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(guardrails);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _http = http;
        _tokens = tokens;
        _guardrails = guardrails;
        _logger = logger;
    }

    public async Task<ContentSafetyResult> EvaluateAsync(
        string text,
        ContentSafetyStage stage,
        ContentSafetyEvaluationContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ContentSafetyResult.Passed;
        }

        ContentSafetyConfig config = _guardrails.ContentSafety; using Activity? activity = AgentTelemetry.Source.StartActivity(
            SpanName(stage),
            ActivityKind.Client);
        activity?.SetTag("guardrails.contentsafety.stage", stage.ToString());

        long startTicks = Stopwatch.GetTimestamp();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(200, config.TimeoutMs)));

        try
        {
            List<ContentSafetyCategoryHit> hits = [];
            bool jailbreak = false;
            bool indirect = false;
            string? correlation = null;

            if (context.CheckPromptShield && config.PromptShieldsEnabled)
            {
                PromptShieldResult shield = await CallPromptShieldAsync(text, stage, cts.Token).ConfigureAwait(false);
                jailbreak = shield.UserPromptAttackDetected;
                indirect = shield.DocumentAttackDetected;
                correlation = shield.CorrelationId;
            }

            // Azure Content Safety rejects any single AnalyzeText request over 10,000
            // characters with a 400. Sending the whole text meant the largest tool
            // results — a twelve-brand portfolio payload is ~14,000 characters — threw,
            // the tool invocation failed, and the caller silently lost every brand. The
            // curated portfolio-ranking prompt could therefore never draw its chart, and
            // the failure looked like a charting bug rather than a guardrail one.
            //
            // Scanning a truncated prefix would have been a hole in the guardrail, so the
            // text is segmented and every segment is analysed. Severity is the maximum
            // across segments: any segment tripping a category trips the whole text.
            Dictionary<string, int> maxByCategory = new(StringComparer.OrdinalIgnoreCase);

            foreach (string segment in SegmentForAnalysis(text))
            {
                AnalyzeTextResult analysis = await _client.AnalyzeTextAsync(
                    new AnalyzeTextOptions(segment),
                    cts.Token).ConfigureAwait(false);

                foreach (TextCategoriesAnalysis categoryAnalysis in analysis.CategoriesAnalysis)
                {
                    int severity = categoryAnalysis.Severity ?? 0;
                    if (severity <= 0) continue;

                    string category = categoryAnalysis.Category.ToString();
                    maxByCategory[category] = maxByCategory.TryGetValue(category, out int existing)
                        ? Math.Max(existing, severity)
                        : severity;
                }
            }

            foreach ((string category, int severity) in maxByCategory)
            {
                hits.Add(new ContentSafetyCategoryHit(category, severity));
            }

            ContentSafetyResult result = Decide(config, hits, jailbreak, indirect, startTicks, correlation);
            AnnotateActivity(activity, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Content Safety call timed out at {Stage} (limit {TimeoutMs}ms).",
                stage, config.TimeoutMs);
            activity?.SetTag("guardrails.contentsafety.decision", ContentSafetyDecision.ServiceUnavailable.ToString());
            activity?.SetTag("guardrails.contentsafety.timeout", true);
            return WithLatency(ContentSafetyResult.ServiceUnavailable, startTicks);
        }
        catch (RequestFailedException ex) when (IsServiceUnavailable(ex))
        {
            _logger.LogWarning(ex, "Content Safety service returned unavailable status at {Stage}.", stage);
            activity?.SetTag("guardrails.contentsafety.decision", ContentSafetyDecision.ServiceUnavailable.ToString());
            activity?.SetTag("guardrails.contentsafety.transport_error", true);
            return WithLatency(ContentSafetyResult.ServiceUnavailable, startTicks);
        }
        catch (BrokenCircuitException ex)
        {
            // Polly opened the breaker — every call is short-circuited until
            // the sampling window recovers. We translate this to
            // ServiceUnavailable so the middleware's fail-open / fail-closed
            // policy decides the request outcome and the audit row is written.
            _logger.LogWarning(ex, "Content Safety circuit breaker open at {Stage}.", stage);
            activity?.SetTag("guardrails.contentsafety.decision", ContentSafetyDecision.ServiceUnavailable.ToString());
            activity?.SetTag("guardrails.contentsafety.breaker_open", true);
            return WithLatency(ContentSafetyResult.ServiceUnavailable, startTicks);
        }
        catch (TimeoutRejectedException ex)
        {
            _logger.LogWarning(ex, "Content Safety Polly timeout rejected at {Stage} (limit {TimeoutMs}ms).",
                stage, config.TimeoutMs);
            activity?.SetTag("guardrails.contentsafety.decision", ContentSafetyDecision.ServiceUnavailable.ToString());
            activity?.SetTag("guardrails.contentsafety.timeout", true);
            return WithLatency(ContentSafetyResult.ServiceUnavailable, startTicks);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Content Safety transport error at {Stage}.", stage);
            activity?.SetTag("guardrails.contentsafety.decision", ContentSafetyDecision.ServiceUnavailable.ToString());
            activity?.SetTag("guardrails.contentsafety.transport_error", true);
            return WithLatency(ContentSafetyResult.ServiceUnavailable, startTicks);
        }
    }

    private static ContentSafetyResult Decide(
        ContentSafetyConfig config,
        List<ContentSafetyCategoryHit> hits,
        bool jailbreak,
        bool indirect,
        long startTicks,
        string? correlation)
    {
        TimeSpan latency = Stopwatch.GetElapsedTime(startTicks);

        if (jailbreak || indirect)
        {
            return new ContentSafetyResult(
                ContentSafetyDecision.Blocked,
                hits,
                jailbreak,
                indirect,
                latency,
                correlation,
                PrimaryCategory: jailbreak
                    ? ContentSafetyDetectionTypes.PromptShield
                    : ContentSafetyDetectionTypes.IndirectInjection);
        }

        ContentSafetyCategoryHit? overThreshold = null;
        foreach (ContentSafetyCategoryHit hit in hits)
        {
            int threshold = config.Thresholds.Resolve(hit.Category);
            if (hit.Severity >= threshold)
            {
                overThreshold = hit;
                break;
            }
        }

        if (overThreshold is not null)
        {
            return new ContentSafetyResult(
                ContentSafetyDecision.Blocked,
                hits,
                jailbreak,
                indirect,
                latency,
                correlation,
                PrimaryCategory: ContentSafetyDetectionTypes.ForCategory(overThreshold.Category));
        }

        ContentSafetyDecision decision = hits.Count > 0
            ? ContentSafetyDecision.Flagged
            : ContentSafetyDecision.Passed;

        return new ContentSafetyResult(
            decision,
            hits,
            jailbreak,
            indirect,
            latency,
            correlation);
    }

    private async Task<PromptShieldResult> CallPromptShieldAsync(
        string text,
        ContentSafetyStage stage,
        CancellationToken ct)
    {
        // Input is the user speaking, so it is submitted as a user prompt and
        // jailbreak detection applies. Retrieved knowledge and tool results are
        // data the model is about to read, so they are submitted as documents
        // and indirect-injection detection applies. Submitting a tool result as
        // a user prompt would look for the wrong attack entirely.
        PromptShieldRequest body = stage is ContentSafetyStage.RetrievedKnowledge or ContentSafetyStage.ToolResult
            ? new PromptShieldRequest(UserPrompt: null, Documents: [text])
            : new PromptShieldRequest(UserPrompt: text, Documents: null);

        using HttpContent content = JsonContent.Create(body, options: _jsonOptions);
        using HttpRequestMessage request = new(HttpMethod.Post, _promptShieldPath) { Content = content };
        // Managed-identity bearer — matches Bicep's disableLocalAuth=true.
        // The token provider caches under a semaphore so this call is
        // synchronous once per rotation and never issues a per-request login.
        string bearer = await _tokens.GetBearerAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        PromptShieldResponse? parsed = await response.Content
            .ReadFromJsonAsync<PromptShieldResponse>(_jsonOptions, ct)
            .ConfigureAwait(false);

        string? correlationId = response.Headers.TryGetValues("apim-request-id", out IEnumerable<string>? apimIds)
            ? apimIds.FirstOrDefault()
            : response.Headers.TryGetValues("x-ms-request-id", out IEnumerable<string>? reqIds)
                ? reqIds.FirstOrDefault()
                : null;

        return new PromptShieldResult(
            UserPromptAttackDetected: parsed?.UserPromptAnalysis?.AttackDetected ?? false,
            DocumentAttackDetected: (parsed?.DocumentsAnalysis ?? []).Any(d => d.AttackDetected),
            CorrelationId: correlationId);
    }

    private static void AnnotateActivity(Activity? activity, ContentSafetyResult result)
    {
        if (activity is null) return;

        activity.SetTag("guardrails.contentsafety.decision", result.Decision.ToString());
        activity.SetTag("guardrails.contentsafety.latency_ms", (int)result.Latency.TotalMilliseconds);
        activity.SetTag("guardrails.contentsafety.prompt_shield.jailbreak", result.PromptShieldJailbreakDetected);
        activity.SetTag("guardrails.contentsafety.prompt_shield.indirect", result.PromptShieldIndirectInjectionDetected);
        foreach (ContentSafetyCategoryHit hit in result.Categories)
        {
            activity.SetTag($"guardrails.contentsafety.category.{hit.Category.ToLowerInvariant()}", hit.Severity);
        }
    }

    private static ContentSafetyResult WithLatency(ContentSafetyResult template, long startTicks) =>
        template with { Latency = Stopwatch.GetElapsedTime(startTicks) };

    /// <summary>
    /// Splits text into segments the Content Safety AnalyzeText API will accept.
    /// </summary>
    /// <remarks>
    /// The service rejects any single request over 10,000 characters with a 400, and the
    /// exception propagated out of the tool-invocation path — so an oversized tool result
    /// did not merely skip scanning, it destroyed the result. A twelve-brand portfolio
    /// payload (~14,000 characters) failed every time.
    ///
    /// Every segment is analysed rather than a truncated prefix, because scanning only the
    /// first 10,000 characters would leave the remainder unscanned — a hole in the
    /// guardrail rather than a fix for it. Segments overlap slightly so content straddling
    /// a boundary is still seen whole by at least one call.
    /// </remarks>
    internal static IEnumerable<string> SegmentForAnalysis(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= _maxAnalyzeChars)
        {
            yield return text;
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            int length = Math.Min(_maxAnalyzeChars, text.Length - start);
            yield return text.Substring(start, length);

            if (start + length >= text.Length) yield break;

            // Step forward by less than a full segment so a phrase spanning the cut is
            // present in its entirety in the following segment.
            start += _maxAnalyzeChars - _segmentOverlapChars;
        }
    }

    /// <summary>
    /// Segment size, held below the service's 10,000-character request limit so a
    /// multi-byte boundary or future header cannot push a request over it.
    /// </summary>
    private const int _maxAnalyzeChars = 9_000;

    /// <summary>Overlap between consecutive segments, so a boundary cannot hide content.</summary>
    private const int _segmentOverlapChars = 500;

    private static bool IsServiceUnavailable(RequestFailedException ex) =>
        ex.Status is 0 or 408 or 429 or 500 or 502 or 503 or 504;

    private static string SpanName(ContentSafetyStage stage) => stage switch
    {
        ContentSafetyStage.Input => "guardrails.contentsafety.input",
        ContentSafetyStage.Output => "guardrails.contentsafety.output",
        ContentSafetyStage.RetrievedKnowledge => "guardrails.contentsafety.retrieved_knowledge",
        ContentSafetyStage.ToolResult => "guardrails.contentsafety.tool_result",
        ContentSafetyStage.AgentDefinition => "guardrails.contentsafety.agent_definition",
        _ => "guardrails.contentsafety",
    };

    private sealed record PromptShieldRequest(
        [property: JsonPropertyName("userPrompt")] string? UserPrompt,
        [property: JsonPropertyName("documents")] IReadOnlyList<string>? Documents);

    private sealed record PromptShieldResponse(
        [property: JsonPropertyName("userPromptAnalysis")] PromptShieldAnalysis? UserPromptAnalysis,
        [property: JsonPropertyName("documentsAnalysis")] IReadOnlyList<PromptShieldAnalysis>? DocumentsAnalysis);

    private sealed record PromptShieldAnalysis(
        [property: JsonPropertyName("attackDetected")] bool AttackDetected);

    private readonly record struct PromptShieldResult(
        bool UserPromptAttackDetected,
        bool DocumentAttackDetected,
        string? CorrelationId);

    /// <summary>
    /// Exposed for the DI extension so <see cref="IOptions{TOptions}"/> callers
    /// can validate the resolved endpoint without duplicating the check.
    /// </summary>
    internal static void ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                "GuardrailsConfig:ContentSafety:Endpoint is required when ContentSafety is enabled. "
                + "Set it via configuration or leave ContentSafety:Enabled=false to disable the layer.");
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"GuardrailsConfig:ContentSafety:Endpoint '{endpoint}' is not a valid absolute URI.");
        }
    }
}
