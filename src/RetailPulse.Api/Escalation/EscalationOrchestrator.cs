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
/// </summary>
public class EscalationOrchestrator
{
    private readonly IEnumerable<ISpecialistAgent> _specialists;
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _synthesisDef;
    private readonly ILogger<EscalationOrchestrator> _logger;

    private static readonly TimeSpan L1Timeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan L2Timeout = TimeSpan.FromSeconds(15);

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
        var detectedIntents = routing.DetectedIntents ?? new[] { routing.Intent };
        var isMultiDomain = detectedIntents.Count > 1;
        var isLowConfidence = routing.Confidence < 0.5;

        // L1: Try primary specialist
        var primaryAgent = _specialists.FirstOrDefault(s =>
            s.SupportedIntents.Contains(routing.Intent));

        if (primaryAgent != null && !isMultiDomain && !isLowConfidence)
        {
            try
            {
                using var l1Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                l1Cts.CancelAfter(L1Timeout);

                var response = await primaryAgent.HandleAsync(request, l1Cts.Token);

                if (!string.IsNullOrWhiteSpace(response.Reply) && !response.Reply.Contains("⚠️"))
                {
                    _logger.LogInformation(
                        "L1 handled by {Agent} in {Ms}ms",
                        primaryAgent.Key, sw.ElapsedMilliseconds);

                    return new EscalationResult(
                        response.Reply, 1,
                        new[] { primaryAgent.Key },
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
            relevantAgents = _specialists.Where(s => s.Key == "general").Take(1).ToList();

        if (relevantAgents.Count > 0)
        {
            try
            {
                using var l2Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                l2Cts.CancelAfter(L2Timeout);

                var tasks = relevantAgents.Select(async agent =>
                {
                    try
                    {
                        var response = await agent.HandleAsync(request, l2Cts.Token);
                        return (agent.Key, response.Reply, Success: true);
                    }
                    catch
                    {
                        return (agent.Key, Reply: (string?)null, Success: false);
                    }
                });

                var results = await Task.WhenAll(tasks);
                var successful = results.Where(r => r.Success && !string.IsNullOrWhiteSpace(r.Reply)).ToArray();

                if (successful.Length > 0)
                {
                    var synthesized = successful.Length == 1
                        ? successful[0].Reply!
                        : await SynthesizeL2Async(request.Message, successful, ct);

                    _logger.LogInformation(
                        "L2 handled by {Agents} in {Ms}ms",
                        string.Join(", ", successful.Select(s => s.Key)),
                        sw.ElapsedMilliseconds);

                    return new EscalationResult(
                        synthesized, 2,
                        successful.Select(s => s.Key).ToArray(),
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
            Array.Empty<string>(),
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
        var perspectives = string.Join("\n\n", agentResults.Select(r =>
            $"**{r.Key}**:\n{r.Reply}"));

        var prompt = $"""
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
            var response = await _chatClient.GetResponseAsync(messages, options, ct);
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
