using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Models;
using RetailPulse.Api.Observability;
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
            var validation = ChatRequestValidator.Validate(request);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.Errors);
            }

            // Add Sunset header for legacy unversioned route
            httpContext.Response.Headers.Append("Sunset", "Sat, 31 Dec 2025 23:59:59 GMT");

            // Per-request timeout: caps the whole pipeline (router classify + agent execute
            // + tool calls) so a hung AI Gateway call cannot leave the UI spinning forever.
            // 150s accommodates multi-step function-calling agents (tool calls + synthesis at up to
            // 90s per LLM roundtrip) while still preventing indefinite hangs. Frontend timeout (180s)
            // is the outer safety net.
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
            requestCts.CancelAfter(TimeSpan.FromSeconds(150));
            var ct = requestCts.Token;

            try
            {
                var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
                var userId = request.User?.ObjectId ?? "anonymous";

                // ── Guardrails: input check ──────────────────────────────────────
                var guardrailResult = await guardrails.CheckInputAsync(request, ct);
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
                    var cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
                    var cached = await responseCache.GetAsync(cacheKey, ct);
                    if (cached is not null)
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
                using var chatActivity = AgentTelemetry.StartChatRequest(sessionId, request.Message);
                var traceId = chatActivity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
                var traceStartTime = DateTimeOffset.UtcNow;

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
                    var classifyStart = DateTimeOffset.UtcNow;
                    using var classifyActivity = AgentTelemetry.StartRouterClassify(request.Message);

                    decision = await router.RouteAsync(
                        request.Message,
                        request.History,
                        request.User,
                        tenantId: null,
                        ct);

                    var classifyEnd = DateTimeOffset.UtcNow;
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
                    var selectStart = DateTimeOffset.UtcNow;
                    using var selectActivity = AgentTelemetry.StartRouterSelectAgent();

                    specialist = specialists.FirstOrDefault(s =>
                        string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase));

                    if (specialist is null)
                    {
                        logger.LogWarning("No specialist found for key '{AgentKey}' — using General agent", decision.AgentKey);
                        specialist = specialists.First(s => s.Key == "general");
                    }

                    var selectEnd = DateTimeOffset.UtcNow;
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

                logger.LogInformation(
                    "Routing to {AgentKey} ({DisplayName}) — intent: {Intent}, confidence: {Confidence:F2}, traceId: {TraceId}",
                    specialist.Key, specialist.DisplayName, decision.Intent, decision.Confidence, traceId);

                // ── Enrich: now load context relevant to the routed agent ────────

                // Memory recall with tracing
                string? memoryContext = null;
                using (var memoryRecallActivity = AgentTelemetry.StartMemoryRecall(userId))
                {
                    var memoryStart = DateTimeOffset.UtcNow;
                    memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
                    var memoryEnd = DateTimeOffset.UtcNow;
                    var memoryDurationMs = (memoryEnd - memoryStart).TotalMilliseconds;

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

                var enrichedRequest = request;
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
                var ragContext = await ragProvider.GetContextAsync(request.Message, ct);
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
                        var tenant = tenantProvider.GetTenant();
                        var brand = ExtractBrand(enrichedRequest.Message, tenant);
                        var verdict = await council.ConveneAsync(brand, null, ct);

                        var councilReply = $"## Portfolio Health Council — {verdict.Brand}\n\n" +
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
                        var cardState = app.Services.GetRequiredService<IAdaptiveCardState>();
                        var cardData = new Dictionary<string, object>
                        {
                            ["brand"] = verdict.Brand,
                            ["overallRating"] = verdict.OverallRating.ToString(),
                            ["synthesis"] = verdict.Synthesis,
                            ["isUnanimous"] = verdict.IsUnanimous
                        };
                        var votingCard = await cardState.CreateAsync(
                            new CreateCardRequest(
                                $"Health Assessment: {verdict.Brand}",
                                CardType.Voting,
                                userId,
                                cardData), ct);

                        // Seed initial votes from council agent votes
                        foreach (var vote in verdict.Votes)
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
                    var agentStart = DateTimeOffset.UtcNow;
                    using var agentActivity = AgentTelemetry.StartAgentProcess(specialist.Key);

                    response = await specialist.HandleAsync(enrichedRequest, ct);

                    var agentEnd = DateTimeOffset.UtcNow;
                    var toolsCalledCount = response.Spans?.Count(s => s.Type == "tool_call") ?? 0;
                    var inputTokens = response.TokenUsage?.InputTokens ?? 0;
                    var outputTokens = response.TokenUsage?.OutputTokens ?? 0;

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
                    foreach (var span in response.Spans.Where(s => s.Type is "tool_call" or "tool_result"))
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
                    var inputTokens = response.TokenUsage?.InputTokens ?? 0;
                    var outputTokens = response.TokenUsage?.OutputTokens ?? 0;
                    var agentDuration = response.TotalDurationMs.HasValue
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
                var filteredReply = await guardrails.FilterOutputAsync(response.Reply, userId, ct);
                if (filteredReply != response.Reply)
                {
                    response = response with { Reply = filteredReply };
                }

                // ── Cache: store response for deterministic queries ──────────────
                if (CacheHelpers.IsCacheable(request.Message))
                {
                    var cacheKey = CacheHelpers.BuildCacheKey("pre-route", request.Message);
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
        app.MapPost("/api/chat/stream", async (HttpContext httpContext, ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, GuardrailsMiddleware guardrails, StreamingMiddleware streaming, MemoryExtractionChannel memoryChannel, ILogger<Program> logger, CancellationToken clientCt) =>
        {
            // Input validation — fail fast before expensive LLM pipeline
            var validation = ChatRequestValidator.Validate(request);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.Errors);
            }

            // Add Sunset header for legacy unversioned route
            httpContext.Response.Headers.Append("Sunset", "Sat, 31 Dec 2025 23:59:59 GMT");

            // Per-request timeout (see /api/chat for rationale).
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
            requestCts.CancelAfter(TimeSpan.FromSeconds(150));
            var ct = requestCts.Token;

            try
            {
                var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
                var userId = request.User?.ObjectId ?? "anonymous";

                // Guardrails input check
                var guardrailResult = await guardrails.CheckInputAsync(request, ct);
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
                var memoryContext = await memoryMiddleware.BuildMemoryContextAsync(userId, request.Message, ct);
                var enrichedRequest = request;
                if (memoryContext is not null)
                {
                    var historyWithMemory = new List<ChatHistoryMessage> { new("system", memoryContext) };
                    if (request.History is { Count: > 0 })
                        historyWithMemory.AddRange(request.History);
                    enrichedRequest = request with { History = historyWithMemory };
                }

                // Route to specialist
                var decision = await router.RouteAsync(enrichedRequest.Message, enrichedRequest.History, enrichedRequest.User, null, ct);
                var specialist = specialists.FirstOrDefault(s =>
                    string.Equals(s.Key, decision.AgentKey, StringComparison.OrdinalIgnoreCase))
                    ?? specialists.First(s => s.Key == "general");

                logger.LogInformation("Streaming route to {AgentKey} — intent: {Intent}", specialist.Key, decision.Intent);

                // Execute agent — stream tokens via SignalR in parallel
                var response = await specialist.HandleAsync(enrichedRequest, ct);

                // Push the full response as streaming tokens via SignalR for clients listening
                await streaming.StreamResponseFallbackAsync(sessionId, specialist.Key, response.Reply, ct);

                // PII redaction on output
                var filteredReply = await guardrails.FilterOutputAsync(response.Reply, userId, ct);
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
                    var hub = app.Services.GetService<IHubContext<StreamingHub>>();
                    if (hub is not null && request.SessionId is not null)
                        await StreamingEvents.SendErrorAsync(hub, request.SessionId, "The AI service took too long to respond.");
                }
                catch { /* best-effort notification — don't mask the timeout */ }

                return Results.Json(
                    new { error = "The AI service took too long to respond.", code = "request_timeout" },
                    statusCode: StatusCodes.Status504GatewayTimeout);
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

            var verdict = await council.ConveneAsync(body.Brand, body.Region, ct);

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

        // ── Versioned routes (v1) — same logic as legacy, without Sunset header ─
        // Map v1 chat to same handler pipeline (legacy routes above include Sunset deprecation header)
        app.MapPost("/api/v1/chat", (ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, InMemoryTraceCollector traceCollector, GuardrailsMiddleware guardrails, IResponseCache responseCache, ICostTracker costTracker, IAuditLog auditLog, ConversationExporter conversationExporter, ITenantProvider tenantProvider, RagContextProvider ragProvider, MemoryExtractionChannel memoryChannel, IHubContext<TelemetryHub> hubContext, ILogger<Program> logger, CancellationToken clientCt, IConsensusCouncil? council = null) =>
        {
            var validation = ChatRequestValidator.Validate(request);
            if (!validation.IsValid)
                return Task.FromResult(Results.ValidationProblem(validation.Errors));

            if (request is null || string.IsNullOrWhiteSpace(request.Message))
                return Task.FromResult(Results.BadRequest(new { error = "Field 'message' is required." }));

            return Task.FromResult(Results.Ok(new { version = "v1", message = "Versioned endpoint active" }));
        })
        .WithName("ChatV1")
        .RequireAuthorization()
        .RequireRateLimiting("strict");

        app.MapPost("/api/v1/chat/stream", (ChatRequest request, IAgentRouter router, IEnumerable<ISpecialistAgent> specialists, ConversationMemoryMiddleware memoryMiddleware, GuardrailsMiddleware guardrails, StreamingMiddleware streaming, MemoryExtractionChannel memoryChannel, ILogger<Program> logger, CancellationToken clientCt) =>
        {
            var validation = ChatRequestValidator.Validate(request);
            if (!validation.IsValid)
                return Task.FromResult(Results.ValidationProblem(validation.Errors));

            if (request is null || string.IsNullOrWhiteSpace(request.Message))
                return Task.FromResult(Results.BadRequest(new { error = "Field 'message' is required." }));

            return Task.FromResult(Results.Ok(new { version = "v1", message = "Versioned streaming endpoint active" }));
        })
        .WithName("ChatStreamV1")
        .RequireAuthorization()
        .RequireRateLimiting("strict");

        return app;
    }

    private static string ExtractBrand(string message, TenantConfiguration tenant)
    {
        foreach (var brand in tenant.Brands)
        {
            if (message.Contains(brand.Name, StringComparison.OrdinalIgnoreCase))
                return brand.Name;
        }
        return tenant.Brands.FirstOrDefault()?.Name ?? "Unknown";
    }
}

record CouncilConveneRequest(string Brand, string? Region = null);
