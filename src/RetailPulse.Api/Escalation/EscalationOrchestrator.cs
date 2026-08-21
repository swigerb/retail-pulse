using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Escalation;

/// <summary>
/// L1→L2→L3 escalation chain for complex queries that need multiple specialist perspectives.
/// L1: Single specialist (fast path, 8s timeout).
/// L2: Multi-specialist fan-out (parallel, 15s timeout).
/// L3: Flags for human review with context.
///
/// <para>
/// <b>Relationship to hybrid execution (issue #95).</b> The L2 multi-specialist
/// fan-out predated the plan-first path and was the earlier response to "one
/// specialist is not enough". Issue #95's hybrid execution decider
/// (<see cref="RetailPulse.Api.Agents.Routing.HybridExecutionDecider"/>) is now
/// the canonical multi-specialist admission signal for <c>/api/chat</c>:
/// multi-domain / low-confidence / advisory prompts admit into
/// <see cref="RetailPulse.Api.Agents.Planning.PlanOrchestrator"/>, which owns
/// planning, execution, review, persistence, and cost attribution. The chat
/// pipeline does NOT call this orchestrator, so there is no duplicated fan-out
/// on the primary chat path.
/// </para>
/// <para>
/// This class stays wired to its own <c>POST /api/escalate</c> endpoint for
/// callers that opt in to the older L1→L2→L3 shape explicitly; removing it
/// without an integration compatibility pass would silently drop that
/// endpoint's contract. It is deprecated-in-place: no new features land here,
/// and new callers should prefer the plan path.
/// </para>
/// </summary>
public class EscalationOrchestrator
{
    private readonly IEnumerable<ISpecialistAgent> _specialists;
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _synthesisDef;
    private readonly ILogger<EscalationOrchestrator> _logger;

    private static readonly TimeSpan _l1Timeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan _l2Timeout = TimeSpan.FromSeconds(15);

    public EscalationOrchestrator(
        IEnumerable<ISpecialistAgent> specialists,
        IChatClient chatClient,
        AgentDefinition synthesisDef,
        ILogger<EscalationOrchestrator> logger)
    {
        _specialists = specialists;
        _chatClient = chatClient;
        _synthesisDef = synthesisDef;
        _logger = logger;
    }

    public record EscalationResult(
        string Reply,
        int Level,
        string[] AgentsConsulted,
        long DurationMs,
        bool NeedsHumanReview = false,
        string? EscalationReason = null);

    /// <summary>
    /// Escalates a query through L1→L2→L3 as needed.
    /// </summary>
    public async Task<EscalationResult> EscalateAsync(
        ChatRequest request,
        RoutingDecision routing,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Determine complexity from detected intents
        IReadOnlyList<string> detectedIntents = routing.DetectedIntents ?? [routing.Intent];
        bool isMultiDomain = detectedIntents.Count > 1;
        bool isLowConfidence = routing.Confidence < 0.5;

        // L1: Try primary specialist
        ISpecialistAgent? primaryAgent = _specialists.FirstOrDefault(s =>
            s.SupportedIntents.Contains(routing.Intent));

        if (primaryAgent != null && !isMultiDomain && !isLowConfidence)
        {
            try
            {
                using var l1Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                l1Cts.CancelAfter(_l1Timeout);

                ChatResponse response = await primaryAgent.HandleAsync(request, l1Cts.Token);

                if (!string.IsNullOrWhiteSpace(response.Reply) && !response.Reply.Contains("⚠️"))
                {
                    _logger.LogInformation(
                        "L1 handled by {Agent} in {Ms}ms",
                        primaryAgent.Key, sw.ElapsedMilliseconds);

                    return new EscalationResult(
                        response.Reply, 1,
                        [primaryAgent.Key],
                        sw.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("L1 timed out for {Agent}, escalating to L2", primaryAgent.Key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "L1 failed for {Agent}, escalating to L2", primaryAgent.Key);
            }
        }

        // L2: Fan out to relevant specialists
        var relevantAgents = _specialists
            .Where(s => detectedIntents.Any(i => s.SupportedIntents.Contains(i)))
            .Take(3)
            .ToList();

        if (relevantAgents.Count == 0)
            relevantAgents = [.. _specialists.Where(s => s.Key == "general").Take(1)];

        if (relevantAgents.Count > 0)
        {
            try
            {
                using var l2Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                l2Cts.CancelAfter(_l2Timeout);

                IEnumerable<Task<(string Key, string? Reply, bool Success)>> tasks = relevantAgents.Select(async agent =>
                {
                    try
                    {
                        ChatResponse response = await agent.HandleAsync(request, l2Cts.Token);
                        return (agent.Key, response.Reply, Success: true);
                    }
                    catch
                    {
                        return (agent.Key, Reply: default(string), Success: false);
                    }
                });

                (string Key, string? Reply, bool Success)[] results = await Task.WhenAll(tasks);
                (string Key, string? Reply, bool Success)[] successful = [.. results.Where(r => r.Success && !string.IsNullOrWhiteSpace(r.Reply))];

                if (successful.Length > 0)
                {
                    string synthesized = successful.Length == 1
                        ? successful[0].Reply!
                        : await SynthesizeL2Async(request.Message, [.. successful], ct);

                    _logger.LogInformation(
                        "L2 handled by {Agents} in {Ms}ms",
                        string.Join(", ", successful.Select(s => s.Key)),
                        sw.ElapsedMilliseconds);

                    return new EscalationResult(
                        synthesized, 2,
                        [.. successful.Select(s => s.Key)],
                        sw.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("L2 timed out, escalating to L3");
            }
        }

        // L3: Flag for human review
        _logger.LogWarning(
            "Escalated to L3 (human review) for: {Message}",
            request.Message[..Math.Min(100, request.Message.Length)]);

        return new EscalationResult(
            "🔄 This query requires additional expertise. It has been flagged for specialist review. " +
            "A team member will follow up shortly with a detailed analysis.",
            3,
            [],
            sw.ElapsedMilliseconds,
            NeedsHumanReview: true,
            EscalationReason: isLowConfidence
                ? "Low routing confidence"
                : isMultiDomain
                    ? "Multi-domain query spanning " + string.Join(", ", detectedIntents)
                    : "All specialist agents failed or timed out");
    }

    private async Task<string> SynthesizeL2Async(
        string originalQuestion,
        (string Key, string? Reply, bool Success)[] agentResults,
        CancellationToken ct)
    {
        string perspectives = string.Join("\n\n", agentResults.Select(r =>
            $"**{r.Key}**:\n{r.Reply}"));

        string prompt = $"""
            The user asked: "{originalQuestion}"

            Multiple specialist agents provided these perspectives:

            {perspectives}

            Synthesize these into a single coherent response that:
            1. Combines insights from all perspectives
            2. Highlights any contradictions or tensions between viewpoints
            3. Provides a clear, actionable answer
            4. Credits the specialist domains that contributed

            Be concise but comprehensive.
            """;

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _synthesisDef.SystemPrompt),
                new(ChatRole.User, prompt)
            };

            var options = new ChatOptions { Temperature = (float)_synthesisDef.Temperature };
            Microsoft.Extensions.AI.ChatResponse response = await _chatClient.GetResponseAsync(messages, options, ct);
            return response.Text ?? perspectives;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "L2 synthesis failed, returning concatenated responses");
            return string.Join("\n\n---\n\n", agentResults.Select(r =>
                $"**{r.Key}**: {r.Reply}"));
        }
    }
}
