using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Prefetch;
using RetailPulse.Api.Rag;
using RetailPulse.Api.Tracing;
using RetailPulse.Api.Validation;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Caching;
using RetailPulse.Contracts.Cards;
using RetailPulse.Contracts.Consensus;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Endpoints;

public static class ChatEndpoints
{
    public static WebApplication MapChatEndpoints(this WebApplication app, AgentDefinition agentDef)
    {
        // Chat endpoint — routes through guardrails → cache → multi-agent router with memory and tracing
        app.MapPost("/api/chat", async (HttpContext httpContext, ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, InMemoryTraceCollector traceCollector, GuardrailsMiddleware guardrails, IResponseCache responseCache, ICostTracker costTracker, IAuditLog auditLog, ConversationExporter conversationExporter, ITenantProvider tenantProvider, RagContextProvider ragProvider, MemoryExtractionChannel memoryChannel, IHubContext<TelemetryHub> hubContext, ILogger<Program> logger, CancellationToken clientCt, IConsensusCouncil? council = null) =>
        {
            // Input validation — fail fast before expensive LLM pipeline
            ValidationResult validation = ChatRequestValidator.Validate(request);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.Errors);
            }

            // Add Sunset header for legacy unversioned route
            httpContext.Response.Headers.Append("Sunset", "Sat, 31 Dec 2025 23:59:59 GMT");

            // Per-request timeout: caps the whole pipeline (router classify + agent execute
            // + tool calls) so a hung AI Gateway call cannot leave the UI spinning forever.
            // 60s is firm: with MaxIterations=1 and 30s NetworkTimeout, the pipeline
            // has headroom for routing + one LLM call + tool calls. If it can't finish
            // in 60s, fail fast with a clear error instead of making the user wait.
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
            requestCts.CancelAfter(TimeSpan.FromSeconds(60));
            CancellationToken ct = requestCts.Token;

            try
            {
                string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
                string userId = request.User?.ObjectId ?? "anonymous";

                // ── Guardrails: input check ──────────────────────────────────────
                GuardrailResult guardrailResult = await guardrails.CheckInputAsync(request, ct);
                if (guardrailResult.IsBlocked)
                {
                    return Results.Ok(new ChatResponse(
                        guardrailResult.RefusalMessage!,
                        sessionId,
                        [],
                        null,
                        0));
                }

                // ── Cache: check for cached response ─────────────────────────────
                if (CacheHelpers.IsCacheable(request.Message))
                {
                    string cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
                    CachedResponse? cached = await responseCache.GetAsync(cacheKey, ct);
                    if (cached is not null && cached.AgentId != "cache-warming")
                    {
                        logger.LogInformation("Cache hit for session {SessionId}, key {CacheKey}", sessionId, cacheKey[..8]);
                        return Results.Ok(new ChatResponse(
                            cached.Response,
                            sessionId,
                            [new AgentSpan("cache.hit", "cache", $"Served from cache (agent: {cached.AgentId})", 0, DateTimeOffset.UtcNow, sessionId)],
                            null,
                            0));
                    }
                }

                // Start root trace span: chat_request
                using Activity? chatActivity = AgentTelemetry.StartChatRequest(sessionId, request.Message);
                string traceId = chatActivity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                DateTimeOffset traceStartTime = DateTimeOffset.UtcNow;

                // ── Route FIRST: classify intent before loading context ──────────
                // This saves ~200ms by not loading memory/RAG for the routing decision.
                // Routing only needs the raw message + conversation history.

                // Emit progress: routing phase
                _ = hubContext.Clients.Group(sessionId).SendAsync("progress", new
                {
                    sessionId,
                    phase = "routing",
                    detail = "Classifying your question...",
                    timestamp = DateTimeOffset.UtcNow
                }, ct);

                // Router classification with tracing
                RoutingDecision decision;
                {
                    DateTimeOffset classifyStart = DateTimeOffset.UtcNow;
                    using Activity? classifyActivity = AgentTelemetry.StartRouterClassify(request.Message);

                    decision = await router.RouteAsync(
                        request.Message,
                        request.History,
                        request.User,
                        tenantId: null,
                        ct);

                    DateTimeOffset classifyEnd = DateTimeOffset.UtcNow;
                    classifyActivity?.SetTag("router.intent", decision.Intent);
                    classifyActivity?.SetTag("router.confidence", decision.Confidence);

                    traceCollector.CaptureSpan(new TraceSpan(
                        SpanId: Guid.NewGuid().ToString("N")[..16],
                        TraceId: traceId,
                        ParentSpanId: chatActivity?.SpanId.ToString(),
                        OperationName: "router.classify",
                        StartTime: classifyStart,
                        EndTime: classifyEnd,
                        DurationMs: (classifyEnd - classifyStart).TotalMilliseconds,
                        Tags: new Dictionary<string, string>
                        {
                            ["router.intent"] = decision.Intent,
                            ["router.confidence"] = decision.Confidence.ToString("F2", CultureInfo.InvariantCulture)
                        }));
                }

                // Agent selection with tracing
                ISpecialistAgent? specialist;
                {
                    DateTimeOffset selectStart = DateTimeOffset.UtcNow;
                    using Activity? selectActivity = AgentTelemetry.StartRouterSelectAgent();

                    specialist = specialists.FirstOrDefault(s =>
                        string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase));

                    if (specialist is null)
                    {
                        logger.LogWarning("No specialist found for key '{AgentKey}' — using General agent", decision.AgentKey);
                        specialist = specialists.First(s => s.Key == "general");
                    }

                    DateTimeOffset selectEnd = DateTimeOffset.UtcNow;
                    selectActivity?.SetTag("router.selected_agent", specialist.Key);
                    selectActivity?.SetTag("router.selected_agent_name", specialist.DisplayName);

                    traceCollector.CaptureSpan(new TraceSpan(
                        SpanId: Guid.NewGuid().ToString("N")[..16],
                        TraceId: traceId,
                        ParentSpanId: chatActivity?.SpanId.ToString(),
                        OperationName: "router.select_agent",
                        StartTime: selectStart,
                        EndTime: selectEnd,
                        DurationMs: (selectEnd - selectStart).TotalMilliseconds,
                        Tags: new Dictionary<string, string>
                        {
                            ["router.selected_agent"] = specialist.Key,
                            ["router.selected_agent_name"] = specialist.DisplayName
                        }));
                }

                long routingDurationMs = (long)(DateTimeOffset.UtcNow - traceStartTime).TotalMilliseconds;
                var routingInfo = new RoutingInfo(
                    specialist.Key,
                    specialist.DisplayName,
                    decision.Intent,
                    decision.Confidence,
                    routingDurationMs);

                // Emit trace_started with routing metadata for frontend telemetry panel
                traceCollector.EmitTraceStarted(traceId, traceStartTime, decision.Intent, specialist.DisplayName);

                logger.LogInformation(
                    "Routing to {AgentKey} ({DisplayName}) — intent: {Intent}, confidence: {Confidence:F2}, traceId: {TraceId}",
                    specialist.Key, specialist.DisplayName, decision.Intent, decision.Confidence, traceId);

                // ── Enrich: now load context relevant to the routed agent ────────

                // Memory recall with tracing
                string? memoryContext = null;
                using (Activity? memoryRecallActivity = AgentTelemetry.StartMemoryRecall(userId))
                {
                    DateTimeOffset memoryStart = DateTimeOffset.UtcNow;
                    memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
                    DateTimeOffset memoryEnd = DateTimeOffset.UtcNow;
                    double memoryDurationMs = (memoryEnd - memoryStart).TotalMilliseconds;

                    memoryRecallActivity?.SetTag("memory.entries_recalled", memoryContext is not null ? "context_found" : "none");

                    traceCollector.CaptureSpan(new TraceSpan(
                        SpanId: Guid.NewGuid().ToString("N")[..16],
                        TraceId: traceId,
                        ParentSpanId: chatActivity?.SpanId.ToString(),
                        OperationName: "memory.recall",
                        StartTime: memoryStart,
                        EndTime: memoryEnd,
                        DurationMs: memoryDurationMs,
                        Tags: new Dictionary<string, string>
                        {
                            ["memory.user_id"] = userId,
                            ["memory.entries_recalled"] = memoryContext is not null ? "context_found" : "none"
                        }));
                }

                ChatRequest enrichedRequest = request;
                if (memoryContext is not null)
                {
                    var historyWithMemory = new List<ChatHistoryMessage>
                    {
                        new("system", memoryContext)
                    };
                    if (request.History is { Count: > 0 })
                        historyWithMemory.AddRange(request.History);

                    enrichedRequest = request with { History = historyWithMemory };
                }

                // RAG context injection — search knowledge base for relevant grounding
                string? ragContext = await ragProvider.GetContextAsync(request.Message, ct);
                if (ragContext is not null)
                {
                    var historyWithRag = new List<ChatHistoryMessage>(enrichedRequest.History ?? [])
                    {
                        new("system", ragContext)
                    };
                    enrichedRequest = enrichedRequest with { History = historyWithRag };
                }

                // Emit progress: agent_start phase
                _ = hubContext.Clients.Group(sessionId).SendAsync("progress", new
                {
                    sessionId,
                    phase = "agent_start",
                    detail = $"{specialist.DisplayName} is analyzing...",
                    timestamp = DateTimeOffset.UtcNow
                }, ct);

                // Council interception: if the router classified as council/health, convene the council
                if (decision.DetectedIntents?.Any(i => string.Equals(i, "council/health", StringComparison.OrdinalIgnoreCase)) == true
                    || string.Equals(decision.Intent, "council/health", StringComparison.OrdinalIgnoreCase))
                {
                    if (council is not null)
                    {
                        TenantConfiguration tenant = tenantProvider.GetTenant();
                        string brand = ExtractBrand(enrichedRequest.Message, tenant);
                        CouncilVerdict verdict = await council.ConveneAsync(brand, null, ct);

                        string councilReply = $"## Portfolio Health Council — {verdict.Brand}\n\n" +
                            $"**Overall Rating: {verdict.OverallRating}** {(verdict.IsUnanimous ? "(unanimous)" : "(split decision)")}\n\n" +
                            $"{verdict.Synthesis}\n\n" +
                            (verdict.ActionItems.Length > 0
                                ? "### Action Items\n" + string.Join("\n", verdict.ActionItems.Select(a => $"- {a}")) + "\n\n"
                                : "") +
                            (verdict.Disagreements.Length > 0
                                ? "### Disagreements\n" + string.Join("\n", verdict.Disagreements.Select(d => $"- {d}")) + "\n\n"
                                : "") +
                            "### Agent Votes\n" +
                            string.Join("\n", verdict.Votes.Select(v =>
                                $"- **{v.AgentName}**: {v.Rating} (confidence: {v.Confidence:F2}) — {v.Reasoning}"));

                        var councilResponse = new ChatResponse(
                            councilReply,
                            enrichedRequest.SessionId ?? sessionId,
                            [],
                            null,
                            (long)verdict.TotalDuration.TotalMilliseconds);

                        await memoryMiddleware.ExtractAndStoreAsync(userId, enrichedRequest.Message, councilReply, ct);

                        // Auto-create a Voting card from the council verdict
                        IAdaptiveCardState cardState = app.Services.GetRequiredService<IAdaptiveCardState>();
                        var cardData = new Dictionary<string, object>
                        {
                            ["brand"] = verdict.Brand,
                            ["overallRating"] = verdict.OverallRating.ToString(),
                            ["synthesis"] = verdict.Synthesis,
                            ["isUnanimous"] = verdict.IsUnanimous
                        };
                        AdaptiveCard votingCard = await cardState.CreateAsync(
                            new CreateCardRequest(
                                $"Health Assessment: {verdict.Brand}",
                                CardType.Voting,
                                userId,
                                cardData), ct);

                        // Seed initial votes from council agent votes
                        foreach (AgentVote vote in verdict.Votes)
                        {
                            await cardState.ActionAsync(votingCard.Id, new CardAction(
                                vote.AgentId, vote.AgentName, CardActionType.Vote,
                                new Dictionary<string, string> { ["vote"] = vote.Rating.ToString() }), ct);
                        }

                        return Results.Ok(councilResponse);
                    }
                }

                // Agent execution with tracing
                ChatResponse response;
                {
                    DateTimeOffset agentStart = DateTimeOffset.UtcNow;
                    using Activity? agentActivity = AgentTelemetry.StartAgentProcess(specialist.Key);

                    // Predictive prefetch: if the specialist supports it, extract entities
                    // and pre-fetch tool data in parallel to eliminate one LLM roundtrip.
                    if (specialist is IPrefetchableAgent prefetchable)
                    {
                        ToolPrefetchService? prefetchService = httpContext.RequestServices.GetService<ToolPrefetchService>();
                        if (prefetchService is not null)
                        {
                            PrefetchEntities entities = prefetchService.ExtractEntities(enrichedRequest.Message);
                            IReadOnlyDictionary<string, string> prefetchedData = await prefetchService.PrefetchAsync(decision.Intent, entities, ct);
                            response = await prefetchable.HandleWithPrefetchAsync(enrichedRequest, prefetchedData, ct);
                        }
                        else
                        {
                            response = await specialist.HandleAsync(enrichedRequest, ct);
                        }
                    }
                    else
                    {
                        response = await specialist.HandleAsync(enrichedRequest, ct);
                    }

                    DateTimeOffset agentEnd = DateTimeOffset.UtcNow;
                    int toolsCalledCount = response.Spans?.Count(s => s.Type == "tool_call") ?? 0;
                    int inputTokens = response.TokenUsage?.InputTokens ?? 0;
                    int outputTokens = response.TokenUsage?.OutputTokens ?? 0;

                    agentActivity?.SetTag("agent.name", specialist.Key);
                    agentActivity?.SetTag("agent.tools_called_count", toolsCalledCount);
                    agentActivity?.SetTag("agent.token_input", inputTokens);
                    agentActivity?.SetTag("agent.token_output", outputTokens);

                    traceCollector.CaptureSpan(new TraceSpan(
                        SpanId: Guid.NewGuid().ToString("N")[..16],
                        TraceId: traceId,
                        ParentSpanId: chatActivity?.SpanId.ToString(),
                        OperationName: $"agent.{specialist.Key}.process",
                        StartTime: agentStart,
                        EndTime: agentEnd,
                        DurationMs: (agentEnd - agentStart).TotalMilliseconds,
                        InputTokens: inputTokens,
                        OutputTokens: outputTokens,
                        Tags: new Dictionary<string, string>
                        {
                            ["agent.name"] = specialist.Key,
                            ["agent.tools_called_count"] = toolsCalledCount.ToString(CultureInfo.InvariantCulture),
                            ["agent.token_input"] = inputTokens.ToString(CultureInfo.InvariantCulture),
                            ["agent.token_output"] = outputTokens.ToString(CultureInfo.InvariantCulture)
                        }));
                }

                // Record individual tool spans from agent response
                if (response.Spans is { Count: > 0 })
                {
                    foreach (AgentSpan? span in response.Spans.Where(s => s.Type is "tool_call" or "tool_result"))
                    {
                        traceCollector.CaptureSpan(new TraceSpan(
                            SpanId: Guid.NewGuid().ToString("N")[..16],
                            TraceId: traceId,
                            ParentSpanId: chatActivity?.SpanId.ToString(),
                            OperationName: $"tool.{span.Name}",
                            StartTime: span.Timestamp,
                            EndTime: span.Timestamp.AddMilliseconds(span.DurationMs),
                            DurationMs: span.DurationMs,
                            Tags: new Dictionary<string, string>
                            {
                                ["tool.name"] = span.Name,
                                ["tool.duration_ms"] = span.DurationMs.ToString("F0", CultureInfo.InvariantCulture),
                                ["tool.result_size"] = span.Detail?.Length > 0 ? $"{span.Detail.Length} chars" : ""
                            }));
                    }
                }

                // Memory store via bounded channel (background service processes)
                if (decision.Intent != AgentIntent.MemoryManagement)
                {
                    memoryChannel.TryWrite(new MemoryWorkItem(
                        userId,
                        request.Message,
                        response.Reply,
                        TraceId: traceId,
                        ParentSpanId: chatActivity?.SpanId.ToString()));
                }

                // Notify trace completion via SignalR
                _ = traceCollector.NotifyTraceCompletedAsync(traceId);

                // ── Observability: cost tracking + audit log ─────────────────────
                {
                    int inputTokens = response.TokenUsage?.InputTokens ?? 0;
                    int outputTokens = response.TokenUsage?.OutputTokens ?? 0;
                    TimeSpan agentDuration = response.TotalDurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(response.TotalDurationMs.Value)
                        : TimeSpan.Zero;

                    await costTracker.TrackUsageAsync(new UsageEvent(
                        specialist.Key, "gpt-4o", inputTokens, outputTokens,
                        response.Spans?.FirstOrDefault(s => s.Type == "tool_call")?.Name,
                        DateTime.UtcNow), ct);

                    await auditLog.LogAsync(new AuditEntry(
                        $"{sessionId}-{Guid.NewGuid():N}"[..32],
                        DateTime.UtcNow, userId, specialist.Key,
                        $"chat.{decision.Intent}",
                        request.Message[..Math.Min(200, request.Message.Length)],
                        response.Reply[..Math.Min(200, response.Reply.Length)],
                        inputTokens + outputTokens,
                        agentDuration), ct);

                    // Track messages in conversation exporter for session export
                    var toolCalls = response.Spans?
                        .Where(s => s.Type == "tool_call")
                        .Select(s => s.Name)
                        .ToList();

                    await conversationExporter.TrackMessageAsync(sessionId, new TrackedMessage
                    {
                        Role = "user",
                        Content = request.Message
                    }, ct);

                    await conversationExporter.TrackMessageAsync(sessionId, new TrackedMessage
                    {
                        Role = "assistant",
                        Content = response.Reply,
                        AgentId = specialist.Key,
                        ToolCalls = toolCalls,
                        DurationMs = response.TotalDurationMs.HasValue ? response.TotalDurationMs.Value : null
                    }, ct);
                }

                // ── Guardrails: output PII redaction ─────────────────────────────
                string filteredReply = await guardrails.FilterOutputAsync(response.Reply, userId, ct);
                if (filteredReply != response.Reply)
                {
                    response = response with { Reply = filteredReply };
                }

                // Attach routing info — but strip it if the pipeline returned an error
                // (e.g., rate-limit or timeout handled inside AgentExecutionPipeline).
                // Error replies start with emoji indicators (⏳/⚠️) and shouldn't
                // show "78% confidence" routing metadata that implies a real answer.
                bool isErrorResponse = response.Reply.StartsWith('⏳')
                    || response.Reply.StartsWith("⚠️", StringComparison.Ordinal);
                response = response with { Routing = isErrorResponse ? null : routingInfo };

                // ── Cache: store response for deterministic queries ──────────────
                if (CacheHelpers.IsCacheable(request.Message))
                {
                    string cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
                    await responseCache.SetAsync(cacheKey,
                        new CachedResponse(response.Reply, specialist.Key, DateTime.UtcNow, cacheKey),
                        TimeSpan.FromMinutes(5), ct);
                }

                chatActivity?.SetTag("response.length", response.Reply.Length);
                chatActivity?.SetTag("trace.id", traceId);

                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (clientCt.IsCancellationRequested)
            {
                // Client navigated away or aborted — no response needed.
                logger.LogInformation("Chat request cancelled by client for session {SessionId}", request.SessionId);
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (OperationCanceledException)
            {
                // Our request-level timeout fired (linked CTS). Surface a friendly 504 so the UI
                // can stop spinning and show a clear error instead of waiting forever.
                logger.LogWarning("Chat request timed out for session {SessionId}", request.SessionId);
                return Results.Json(
                    new
                    {
                        error = "The AI service took too long to respond. Please try again — if it persists, try a simpler question first.",
                        code = "request_timeout"
                    },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                // APIM or Azure OpenAI rate-limited the request (could happen during
                // routing classification OR agent execution). Return a proper 429 so
                // the frontend can show a retry prompt instead of crashing to debugger.
                logger.LogWarning(ex, "Rate-limited (429) during chat for session {SessionId}", request.SessionId);
                return Results.Json(
                    new
                    {
                        error = "The AI service is experiencing high demand. Please wait 30 seconds and try again.",
                        code = "rate_limited"
                    },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (ClientResultException ex)
            {
                // Other APIM / Azure OpenAI errors (500, 503, etc.) — surface the
                // status code so it doesn't fall through to a generic 503.
                logger.LogError(ex, "ClientResultException (HTTP {Status}) during chat for session {SessionId}", ex.Status, request.SessionId);
                int statusCode = ex.Status is >= 400 and < 600
                    ? ex.Status
                    : StatusCodes.Status503ServiceUnavailable;
                return Results.Json(
                    new
                    {
                        error = "The AI service encountered an error. Please try again shortly.",
                        code = "ai_service_error"
                    },
                    statusCode: statusCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error from chat agent for session {SessionId}", request.SessionId);

                return Results.Json(
                    new
                    {
                        error = "The AI service is temporarily unavailable. Please try again shortly.",
                        code = "service_unavailable"
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("Chat")
        .WithSummary("Send a chat message to the AI agent pipeline")
        .WithDescription("Routes the message through guardrails, caching, multi-agent routing, memory recall, RAG enrichment, and specialist agent execution. Returns a structured response with the agent's reply, trace spans, and token usage.")
        .WithTags("Chat")
        .Produces<ChatResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status429TooManyRequests)
        .Produces(StatusCodes.Status503ServiceUnavailable)
        .RequireAuthorization()
        .RequireRateLimiting("strict");

        // Streaming chat endpoint — SSE/SignalR progressive token delivery
        app.MapPost("/api/chat/stream", async (HttpContext httpContext, ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, GuardrailsMiddleware guardrails, StreamingMiddleware streaming, StreamingProgressFeature streamingProgressFeature, MemoryExtractionChannel memoryChannel, ILogger<Program> logger, CancellationToken clientCt) =>
        {
            // Input validation — fail fast before expensive LLM pipeline
            ValidationResult validation = ChatRequestValidator.Validate(request);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.Errors);
            }

            // Add Sunset header for legacy unversioned route
            httpContext.Response.Headers.Append("Sunset", "Sat, 31 Dec 2025 23:59:59 GMT");

            // Per-request timeout (see /api/chat for rationale).
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
            requestCts.CancelAfter(TimeSpan.FromSeconds(60));
            CancellationToken ct = requestCts.Token;

            try
            {
                string sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
                string userId = request.User?.ObjectId ?? "anonymous";

                // Guardrails input check
                GuardrailResult guardrailResult = await guardrails.CheckInputAsync(request, ct);
                if (guardrailResult.IsBlocked)
                {
                    return Results.Ok(new ChatResponse(
                        guardrailResult.RefusalMessage!,
                        sessionId,
                        [],
                        null,
                        0));
                }

                // Memory recall
                string? memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
                ChatRequest enrichedRequest = request;
                if (memoryContext is not null)
                {
                    var historyWithMemory = new List<ChatHistoryMessage> { new("system", memoryContext) };
                    if (request.History is { Count: > 0 })
                        historyWithMemory.AddRange(request.History);
                    enrichedRequest = request with { History = historyWithMemory };
                }

                // Route to specialist
                RoutingDecision decision = await router.RouteAsync(enrichedRequest.Message, enrichedRequest.History, enrichedRequest.User, null, ct);
                ISpecialistAgent specialist = specialists.FirstOrDefault(s =>
                    string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase))
                    ?? specialists.First(s => s.Key == "general");

                logger.LogInformation("Streaming route to {AgentKey} — intent: {Intent}", specialist.Key, decision.Intent);

                // Execute agent with streaming progress — the pipeline emits real-time
                // tool progress via SignalR and streams the final reply token-by-token.
                // We signal streaming mode by setting the StreamingProgress feature.
                streamingProgressFeature.Enable(sessionId);

                // Predictive prefetch: if the specialist supports it, extract entities
                // and pre-fetch tool data in parallel to eliminate one LLM roundtrip.
                ChatResponse response;
                if (specialist is IPrefetchableAgent prefetchable)
                {
                    ToolPrefetchService? prefetchService = httpContext.RequestServices.GetService<ToolPrefetchService>();
                    if (prefetchService is not null)
                    {
                        PrefetchEntities entities = prefetchService.ExtractEntities(enrichedRequest.Message);
                        IReadOnlyDictionary<string, string> prefetchedData = await prefetchService.PrefetchAsync(decision.Intent, entities, ct);
                        response = await prefetchable.HandleWithPrefetchAsync(enrichedRequest, prefetchedData, ct);
                    }
                    else
                    {
                        response = await specialist.HandleAsync(enrichedRequest, ct);
                    }
                }
                else
                {
                    response = await specialist.HandleAsync(enrichedRequest, ct);
                }

                // Stream the response via SignalR only if the pipeline didn't already do it
                // (ExecuteWithProgressAsync streams inline; fallback covers non-progress paths)
                if (!streamingProgressFeature.IsEnabled)
                    await streaming.StreamResponseFallbackAsync(sessionId, specialist.Key, response.Reply, ct);

                // PII redaction on output
                string filteredReply = await guardrails.FilterOutputAsync(response.Reply, userId, ct);
                if (filteredReply != response.Reply)
                    response = response with { Reply = filteredReply };

                // Memory extraction via bounded channel (linked to request CancellationToken)
                memoryChannel.TryWrite(new MemoryWorkItem(userId, request.Message, response.Reply));

                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (clientCt.IsCancellationRequested)
            {
                logger.LogInformation("Streaming chat cancelled by client for session {SessionId}", request.SessionId);
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Streaming chat timed out for session {SessionId}", request.SessionId);
                // Emit a streaming:error event so any connected SignalR client can stop its spinner.
                try
                {
                    IHubContext<StreamingHub>? hub = app.Services.GetService<IHubContext<StreamingHub>>();
                    if (hub is not null && request.SessionId is not null)
                        await StreamingEvents.SendErrorAsync(hub, request.SessionId, "The AI service took too long to respond.");
                }
                catch { /* best-effort notification — don't mask the timeout */ }

                return Results.Json(
                    new { error = "The AI service took too long to respond.", code = "request_timeout" },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                logger.LogWarning(ex, "Rate-limited (429) during streaming chat for session {SessionId}", request.SessionId);
                try
                {
                    IHubContext<StreamingHub>? hub = app.Services.GetService<IHubContext<StreamingHub>>();
                    if (hub is not null && request.SessionId is not null)
                        await StreamingEvents.SendErrorAsync(hub, request.SessionId, "The AI service is experiencing high demand. Please wait 30 seconds and try again.");
                }
                catch { /* best-effort notification */ }

                return Results.Json(
                    new { error = "The AI service is experiencing high demand. Please wait 30 seconds and try again.", code = "rate_limited" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (ClientResultException ex)
            {
                logger.LogError(ex, "ClientResultException (HTTP {Status}) during streaming chat for session {SessionId}", ex.Status, request.SessionId);
                int statusCode = ex.Status is >= 400 and < 600
                    ? ex.Status
                    : StatusCodes.Status503ServiceUnavailable;
                return Results.Json(
                    new { error = "The AI service encountered an error. Please try again shortly.", code = "ai_service_error" },
                    statusCode: statusCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Streaming chat error for session {SessionId}", request.SessionId);
                return Results.Json(
                    new { error = "The AI service is temporarily unavailable.", code = "service_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("ChatStream")
        .WithSummary("Stream a chat response via Server-Sent Events")
        .WithDescription("Same pipeline as /api/chat but delivers tokens progressively via SSE for real-time UI rendering.")
        .WithTags("Chat")
        .Produces(StatusCodes.Status200OK)
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status429TooManyRequests)
        .Produces(StatusCodes.Status503ServiceUnavailable)
        .RequireAuthorization()
        .RequireRateLimiting("strict");

        // Health/info endpoint
        app.MapGet("/api/info", (IEnumerable<ISpecialistAgent> specialists) => Results.Ok(new
        {
            Name = "Retail Pulse API",
            Version = "1.0.0",
            Agent = agentDef.Name,
            agentDef.Tools,
            Router = "RetailOpsRouter",
            Specialists = specialists.Select(s => new { s.Key, s.DisplayName }).ToList()
        }))
        .WithName("Info")
        .WithSummary("Get API metadata and available agents")
        .WithDescription("Returns system info including agent name, registered tools, router type, and all specialist agents with their keys and display names.")
        .WithTags("System")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        // ── Council endpoints ────────────────────────────────────────────────
        app.MapPost("/api/council/convene", async (CouncilConveneRequest body, ILogger<Program> logger, CancellationToken ct, IConsensusCouncil? council = null) =>
        {
            if (string.IsNullOrWhiteSpace(body.Brand))
                return Results.BadRequest(new { error = "Field 'brand' is required." });

            if (council is null)
            {
                logger.LogWarning("Council convene requested but IConsensusCouncil is not registered");
                return Results.StatusCode(503);
            }

            logger.LogInformation("Council convening for brand={Brand}, region={Region}",
                body.Brand, body.Region ?? "All");

            CouncilVerdict verdict = await council.ConveneAsync(body.Brand, body.Region, ct);

            return Results.Ok(new
            {
                brand = verdict.Brand,
                region = verdict.Region ?? "All Regions",
                overall_rating = verdict.OverallRating.ToString(),
                synthesis = verdict.Synthesis,
                is_unanimous = verdict.IsUnanimous,
                disagreements = verdict.Disagreements,
                action_items = verdict.ActionItems,
                convened_at = verdict.ConvenedAt,
                total_duration_ms = verdict.TotalDuration.TotalMilliseconds,
                votes = verdict.Votes.Select(v => new
                {
                    agent_id = v.AgentId,
                    agent_name = v.AgentName,
                    rating = v.Rating.ToString(),
                    reasoning = v.Reasoning,
                    confidence = v.Confidence,
                    key_metrics = v.KeyMetrics,
                    response_time_ms = v.ResponseTime.TotalMilliseconds
                })
            });
        })
        .WithName("ConveneCouncil").RequireAuthorization().RequireRateLimiting("strict");

        app.MapGet("/api/council/agents", (IEnumerable<ISpecialistAgent> specialists) =>
        {
            var agents = specialists.Select(s => new
            {
                key = s.Key,
                display_name = s.DisplayName,
                supported_intents = s.SupportedIntents,
                domain = s.Key switch
                {
                    "demand-forecasting" => "Demand & forecasting analysis",
                    "promo-planning" => "Promotion planning & ROI estimation",
                    "competitive-intel" => "Competitive intelligence & market share",
                    "supply-chain" => "Supply chain health & disruption tracking",
                    "memory" => "Conversation memory management",
                    "general" => "General retail operations (fallback)",
                    _ => "Unknown domain"
                }
            }).ToList();

            return Results.Ok(new { agents, total = agents.Count });
        })
        .WithName("ListCouncilAgents").RequireAuthorization().RequireRateLimiting("relaxed");

        return app;
    }

    private static string ExtractBrand(string message, TenantConfiguration tenant)
    {
        foreach (BrandConfig brand in tenant.Brands)
        {
            if (message.Contains(brand.Name, StringComparison.OrdinalIgnoreCase))
                return brand.Name;
        }
        return tenant.Brands.FirstOrDefault()?.Name ?? "Unknown";
    }
}

record CouncilConveneRequest(string Brand, string? Region = null);
