using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Consensus;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;

namespace RetailPulse.Api.Consensus;

/// <summary>
/// Orchestrates the Portfolio Health Council — fans out health assessment
/// requests to specialist agents in parallel (Task.WhenAll), collects
/// structured votes, and synthesizes a unified verdict via LLM.
/// </summary>
public class ConsensusOrchestrator : IConsensusCouncil
{
    private readonly IEnumerable<ISpecialistAgent> _specialists;
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _synthesisDef;
    private readonly AgentDefinition _voteDef;
    private readonly ILogger<ConsensusOrchestrator> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly HashSet<string> _councilParticipants;

    /// <summary>MAF agent name for lightweight voter runs, so characterization tests can
    /// verify each vote flows through <see cref="MafAgentInvoker"/> (a real
    /// <see cref="ChatClientAgent"/>) and not directly through <see cref="IChatClient"/>.</summary>
    internal const string MafVoterAgentName = "ConsensusOrchestrator.voter";

    /// <summary>MAF agent name for the synthesis run.</summary>
    internal const string MafSynthesizerAgentName = "ConsensusOrchestrator.synthesizer";

    /// <summary>Per-agent timeout for the fan-out phase. Must be long enough for
    /// agents to complete tool calls (MCP server round-trips + LLM reasoning).
    /// 30s balances responsiveness against the 75s overall request timeout.</summary>
    private static readonly TimeSpan _agentTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for lightweight voting mode — no tool calls, just LLM reasoning.</summary>
    private static readonly TimeSpan _lightweightVoteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Default agent keys eligible for the council — used when the caller does
    /// not supply a configuration-derived list (unit tests, legacy composition roots).</summary>
    internal static readonly IReadOnlyList<string> DefaultCouncilParticipants =
    [
        "demand-forecasting",
        "competitive-intel",
        "supply-chain"
    ];

    public ConsensusOrchestrator(
        IEnumerable<ISpecialistAgent> specialists,
        IChatClient chatClient,
        AgentDefinition synthesisDef,
        AgentDefinition voteDef,
        ILogger<ConsensusOrchestrator> logger,
        IEnumerable<string>? councilParticipants = null,
        ILoggerFactory? loggerFactory = null)
    {
        _specialists = specialists;
        _chatClient = chatClient;
        _synthesisDef = synthesisDef;
        _voteDef = voteDef;
        _logger = logger;
        _loggerFactory = loggerFactory;

        // Council roster is now data-driven per issue #98. When callers don't supply
        // a roster (older tests), fall back to the historical set so existing
        // behaviour parity is preserved.
        _councilParticipants = new HashSet<string>(
            councilParticipants ?? DefaultCouncilParticipants,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CouncilVerdict> ConveneAsync(string brand, string? region, CancellationToken ct = default)
    {
        DateTime convenedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var participants = _specialists
            .Where(s => _councilParticipants.Contains(s.Key))
            .ToList();

        _logger.LogInformation(
            "Council convening for brand={Brand}, region={Region} with {Count} participants: {Keys}",
            brand, region ?? "All", participants.Count,
            string.Join(", ", participants.Select(p => p.Key)));

        // Fan-out: collect votes from all specialists in parallel
        IEnumerable<Task<AgentVote?>> voteTasks = participants.Select(agent => CollectVoteAsync(agent, brand, region, ct));
        AgentVote?[] votes = await Task.WhenAll(voteTasks);

        AgentVote[] validVotes = [.. votes.Where(v => v is not null).Cast<AgentVote>()];

        _logger.LogInformation(
            "Collected {ValidCount}/{TotalCount} votes in {ElapsedMs}ms",
            validVotes.Length, participants.Count, sw.ElapsedMilliseconds);

        // Synthesize verdict
        CouncilVerdict verdict = await SynthesizeVerdictAsync(brand, region, validVotes, convenedAt, sw, ct);
        return verdict;
    }

    /// <summary>
    /// Lightweight voting mode: sends a focused prompt directly to the LLM without
    /// tool calls. Each voter gets a stripped system prompt and returns a JSON vote
    /// based on domain knowledge. Temperature 0 for deterministic voting.
    /// Falls back to full agent execution only if lightweight mode fails.
    /// </summary>
    private async Task<AgentVote?> CollectVoteAsync(
        ISpecialistAgent agent, string brand, string? region, CancellationToken ct)
    {
        var agentSw = Stopwatch.StartNew();
        string votePrompt = BuildVotePrompt(brand, region);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_lightweightVoteTimeout);

            // Lightweight path: direct LLM call with voting system prompt, no tools
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _voteDef.SystemPrompt),
                new(ChatRole.User, $"[{agent.DisplayName}] {votePrompt}")
            };

            var options = new ChatOptions
            {
                Temperature = 0f,
                ResponseFormat = ChatResponseFormat.Json
            };

            // Route through MAF: the lightweight voter is a real ChatClientAgent invocation
            // (no tools attached; UseProvidedChatClientAsIs preserves the DI decorator stack).
            AgentResponse response = await MafAgentInvoker.RunAsync(
                _chatClient,
                $"{MafVoterAgentName}.{agent.Key}",
                messages,
                options,
                _loggerFactory,
                timeoutCts.Token);
            agentSw.Stop();

            string responseText = response.Text ?? "";
            return ParseVote(agent.Key, agent.DisplayName, responseText, agentSw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            agentSw.Stop();
            _logger.LogWarning("Agent {AgentKey} timed out after {Ms}ms during lightweight council vote",
                agent.Key, agentSw.ElapsedMilliseconds);

            return new AgentVote(
                agent.Key, agent.DisplayName, HealthRating.Yellow,
                $"Agent timed out after {_lightweightVoteTimeout.TotalSeconds}s — unable to complete assessment.",
                0.0, ["timeout"], agentSw.Elapsed);
        }
        catch (Exception ex)
        {
            agentSw.Stop();
            _logger.LogError(ex, "Agent {AgentKey} failed during council vote after {Ms}ms",
                agent.Key, agentSw.ElapsedMilliseconds);

            return new AgentVote(
                agent.Key, agent.DisplayName, HealthRating.Yellow,
                $"Agent encountered an error: {ex.Message}",
                0.0, ["error"], agentSw.Elapsed);
        }
    }

    private static string BuildVotePrompt(string brand, string? region)
    {
        string regionClause = string.IsNullOrWhiteSpace(region) ? "across all regions" : $"in the {region} region";
        return $$"""
            Provide a health assessment for the brand "{{brand}}" {{regionClause}}.

            Use your available tools to gather current data, then respond with a JSON vote:

            {
              "rating": "Green" | "Yellow" | "Red",
              "reasoning": "2-3 sentence explanation grounded in data",
              "confidence": 0.0 to 1.0,
              "key_metrics": ["metric1: value", "metric2: value"]
            }

            Rating guidelines:
            - Green: All metrics healthy, no significant risks
            - Yellow: Some concerns or mixed signals requiring monitoring
            - Red: Critical issues requiring immediate attention

            Respond with ONLY the JSON object. No other text.
            """;
    }

    /// <summary>
    /// Parse the agent's response text into a structured AgentVote.
    /// Falls back to a heuristic parse if strict JSON fails.
    /// </summary>
    private AgentVote ParseVote(string agentId, string agentName, string responseText, TimeSpan elapsed)
    {
        try
        {
            // Try to extract JSON from the response (may have surrounding text)
            int jsonStart = responseText.IndexOf('{');
            int jsonEnd = responseText.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                string json = responseText[jsonStart..(jsonEnd + 1)];
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                string ratingStr = root.GetProperty("rating").GetString() ?? "Yellow";
                HealthRating rating = Enum.TryParse(ratingStr, true, out HealthRating parsed)
                    ? parsed : HealthRating.Yellow;

                string reasoning = root.GetProperty("reasoning").GetString() ?? "No reasoning provided.";

                double confidence = root.TryGetProperty("confidence", out JsonElement confEl)
                    ? confEl.GetDouble() : 0.7;

                var keyMetrics = new List<string>();
                if (root.TryGetProperty("key_metrics", out JsonElement metricsEl) && metricsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in metricsEl.EnumerateArray())
                        keyMetrics.Add(item.GetString() ?? "");
                }

                return new AgentVote(agentId, agentName, rating, reasoning, confidence,
                    [.. keyMetrics], elapsed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse structured vote from {AgentId}, using heuristic", agentId);
        }

        // Heuristic fallback: scan for rating keywords
        HealthRating fallbackRating = responseText.Contains("Red", StringComparison.OrdinalIgnoreCase)
            ? HealthRating.Red
            : responseText.Contains("Yellow", StringComparison.OrdinalIgnoreCase)
                ? HealthRating.Yellow
                : HealthRating.Green;

        return new AgentVote(agentId, agentName, fallbackRating,
            responseText[..Math.Min(500, responseText.Length)],
            0.5, ["parse_fallback"], elapsed);
    }

    /// <summary>
    /// Uses the council-synthesis prompt to produce the final CouncilVerdict.
    /// </summary>
    private async Task<CouncilVerdict> SynthesizeVerdictAsync(
        string brand, string? region, AgentVote[] votes, DateTime convenedAt,
        Stopwatch sw, CancellationToken ct)
    {
        string voteSummary = string.Join("\n\n", votes.Select(v =>
            $"**{v.AgentName}** ({v.AgentId}):\n" +
            $"  Rating: {v.Rating}\n" +
            $"  Confidence: {v.Confidence:F2}\n" +
            $"  Reasoning: {v.Reasoning}\n" +
            $"  Key Metrics: {string.Join(", ", v.KeyMetrics)}\n" +
            $"  Response Time: {v.ResponseTime.TotalMilliseconds:F0}ms"));

        string synthesisPrompt = $$"""
            You are synthesizing a Portfolio Health Council verdict for brand "{{brand}}"{{(region != null ? $" in {region}" : "")}}.

            ## Agent Votes
            {{voteSummary}}

            ## Your Task
            Analyze all agent votes and produce a JSON synthesis:

            {
              "overall_rating": "Green" | "Yellow" | "Red",
              "synthesis": "Executive summary (3-5 sentences) synthesizing all agent perspectives",
              "disagreements": ["list any rating disagreements between agents"],
              "action_items": ["specific recommended actions based on the assessment"]
            }

            Rules:
            - If all agents agree on rating, overall = that rating
            - If agents disagree, lean toward the most conservative (worst) rating
            - Weight higher-confidence votes more heavily
            - Highlight specific disagreements between agents
            - Action items should be concrete and prioritized

            Respond with ONLY the JSON object.
            """;

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _synthesisDef.SystemPrompt),
                new(ChatRole.User, synthesisPrompt)
            };

            var options = new ChatOptions
            {
                Temperature = (float)_synthesisDef.Temperature
            };

            // Synthesis also flows through MAF for parity with the specialist / router paths.
            AgentResponse response = await MafAgentInvoker.RunAsync(
                _chatClient,
                MafSynthesizerAgentName,
                messages,
                options,
                _loggerFactory,
                ct);
            string responseText = response.Text ?? "";

            return ParseSynthesis(brand, region, votes, responseText, convenedAt, sw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synthesis LLM call failed — building verdict from votes only");
            return BuildFallbackVerdict(brand, region, votes, convenedAt, sw);
        }
    }

    private CouncilVerdict ParseSynthesis(
        string brand, string? region, AgentVote[] votes,
        string responseText, DateTime convenedAt, Stopwatch sw)
    {
        try
        {
            int jsonStart = responseText.IndexOf('{');
            int jsonEnd = responseText.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                string json = responseText[jsonStart..(jsonEnd + 1)];
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                string ratingStr = root.GetProperty("overall_rating").GetString() ?? "Yellow";
                HealthRating overallRating = Enum.TryParse(ratingStr, true, out HealthRating parsed)
                    ? parsed : HealthRating.Yellow;

                string synthesis = root.GetProperty("synthesis").GetString() ?? "Assessment complete.";

                var disagreements = new List<string>();
                if (root.TryGetProperty("disagreements", out JsonElement disagEl) && disagEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in disagEl.EnumerateArray())
                        disagreements.Add(item.GetString() ?? "");
                }

                var actionItems = new List<string>();
                if (root.TryGetProperty("action_items", out JsonElement actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in actionsEl.EnumerateArray())
                        actionItems.Add(item.GetString() ?? "");
                }

                bool isUnanimous = votes.Select(v => v.Rating).Distinct().Count() <= 1;

                return new CouncilVerdict(
                    brand, region, overallRating, synthesis, votes,
                    isUnanimous, [.. disagreements], [.. actionItems],
                    convenedAt, sw.Elapsed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse synthesis JSON, falling back");
        }

        return BuildFallbackVerdict(brand, region, votes, convenedAt, sw);
    }

    private static CouncilVerdict BuildFallbackVerdict(
        string brand, string? region, AgentVote[] votes,
        DateTime convenedAt, Stopwatch sw)
    {
        // Conservative fallback: use the worst rating among votes
        HealthRating overallRating = votes.Length > 0
            ? votes.Max(v => v.Rating)
            : HealthRating.Yellow;

        bool isUnanimous = votes.Select(v => v.Rating).Distinct().Count() <= 1;

        string[] disagreements = isUnanimous
            ? []
            : [.. votes.GroupBy(v => v.Rating).Select(g => $"{string.Join(", ", g.Select(v => v.AgentName))} rated {g.Key}")];

        string synthesis = $"Council assessed {brand} with {votes.Length} agent votes. " +
                        $"Overall health: {overallRating}. " +
                        (isUnanimous ? "All agents agreed." : "Agents disagreed — see individual votes.");

        return new CouncilVerdict(
            brand, region, overallRating, synthesis, votes,
            isUnanimous, disagreements, [],
            convenedAt, sw.Elapsed);
    }
}
