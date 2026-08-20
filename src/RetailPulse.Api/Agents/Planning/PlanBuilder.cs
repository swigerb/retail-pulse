using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Models;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Planning;

/// <summary>
/// Turns a router-tagged multi-domain user request into an ordered plan of
/// specialist steps by invoking the tenant-hydrated "planner" agent
/// (<c>prompts.yaml</c> section, role: orchestration). Reuses
/// <see cref="MafAgentInvoker"/> — the same MAF <see cref="ChatClientAgent"/>
/// primitive path the specialists, router, and council votes flow through —
/// so the planner benefits from the existing decorator stack (function
/// invocation cap, OpenTelemetry, resilience) without special-casing.
/// <para>
/// The builder is deliberately conservative:
/// </para>
/// <list type="bullet">
///   <item>Steps outside 1..<see cref="PlanPersistenceOptions.MaxStepCount"/> are unusable.</item>
///   <item>Any step whose specialist_key does not appear in the live roster is unusable.</item>
///   <item>An empty steps array with a "reason" is treated as unusable (planner said so).</item>
///   <item>A response that is not valid JSON is unusable.</item>
/// </list>
/// <para>
/// An unusable result is not a bug — it is one of the terminal states
/// #93 explicitly requires. The caller records the plan as unusable in the
/// store and returns without invoking any specialist.
/// </para>
/// </summary>
public sealed class PlanBuilder
{
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _plannerDef;
    private readonly PlanPersistenceOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<PlanBuilder> _logger;

    public PlanBuilder(
        IChatClient chatClient,
        AgentDefinition plannerDef,
        PlanPersistenceOptions options,
        ILogger<PlanBuilder> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _plannerDef = plannerDef ?? throw new ArgumentNullException(nameof(plannerDef));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Build a plan from the user's request. The roster is the live
    /// <see cref="ISpecialistAgent"/> collection so the planner cannot invent
    /// keys or reference agents that no longer exist after a config reload.
    /// The router-detected intents are supplied verbatim as breadcrumbs so
    /// the planner does not have to re-classify the message; that respects
    /// the "preserve multi-domain breadth" design point in #93.
    /// </summary>
    public async Task<PlanBuildResult> BuildAsync(
        string request,
        IReadOnlyList<ISpecialistAgent> roster,
        IReadOnlyList<string> detectedIntents,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(detectedIntents);

        if (roster.Count == 0)
        {
            _logger.LogWarning("Planner invoked with an empty roster; treating plan as unusable.");
            return new PlanBuildResult { Steps = [], UnusableReason = "empty roster" };
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, _plannerDef.SystemPrompt),
            new(ChatRole.User, BuildRosterMessage(roster, detectedIntents)),
            new(ChatRole.User, request),
        ];

        var chatOptions = new ChatOptions
        {
            Temperature = (float)_plannerDef.Temperature,
            ResponseFormat = ChatResponseFormat.Json,
        };

        AgentResponse mafResponse;
        try
        {
            mafResponse = await MafAgentInvoker.RunAsync(
                _chatClient,
                _plannerDef.Name is { Length: > 0 } n ? n : "Plan-First Orchestrator",
                messages,
                chatOptions,
                _loggerFactory,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Planner LLM call failed; treating plan as unusable.");
            return new PlanBuildResult { Steps = [], UnusableReason = "planner call failed: " + ex.Message };
        }

        Microsoft.Extensions.AI.ChatResponse response = mafResponse.AsChatResponse();
        int input = (int)(response.Usage?.InputTokenCount ?? 0);
        int output = (int)(response.Usage?.OutputTokenCount ?? 0);
        int total = (int)(response.Usage?.TotalTokenCount ?? (input + output));
        string body = mafResponse.Text ?? "";

        (IReadOnlyList<PlannerStep> steps, string? reason) = ParseAndValidate(body, roster);

        return new PlanBuildResult
        {
            Steps = steps,
            UnusableReason = reason,
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
        };
    }

    private static string BuildRosterMessage(
        IReadOnlyList<ISpecialistAgent> roster,
        IReadOnlyList<string> detectedIntents)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Live specialist roster");
        sb.AppendLine("Use exactly these specialist_key values. Do not invent keys.");
        sb.AppendLine();
        foreach (ISpecialistAgent agent in roster)
        {
            string intents = agent.SupportedIntents.Count > 0
                ? string.Join(", ", agent.SupportedIntents)
                : "(no declared intents)";
            sb.AppendFormat(
                System.Globalization.CultureInfo.InvariantCulture,
                "- specialist_key: {0} ({1}) — intents: {2}",
                agent.Key, agent.DisplayName, intents);
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("## Router-detected intents for this request");
        if (detectedIntents.Count == 0)
        {
            sb.AppendLine("(none provided)");
        }
        else
        {
            foreach (string intent in detectedIntents)
            {
                sb.AppendLine("- " + intent);
            }
        }

        return sb.ToString();
    }

    private (IReadOnlyList<PlannerStep> steps, string? unusableReason) ParseAndValidate(
        string body,
        IReadOnlyList<ISpecialistAgent> roster)
    {
        if (string.IsNullOrWhiteSpace(body))
            return ([], "empty planner response");

        PlannerResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PlannerResponse>(body, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Planner response was not valid JSON.");
            return ([], "planner response was not valid JSON");
        }

        if (parsed is null || parsed.Steps is null)
            return ([], "planner response missing steps");

        if (parsed.Steps.Count == 0)
        {
            string? planReason = string.IsNullOrWhiteSpace(parsed.Reason)
                ? "planner returned empty plan"
                : parsed.Reason;
            return ([], planReason);
        }

        if (parsed.Steps.Count > _options.MaxStepCount)
        {
            return ([], $"plan exceeded step cap ({parsed.Steps.Count} > {_options.MaxStepCount})");
        }

        HashSet<string> validKeys = new(
            roster.Select(a => a.Key),
            StringComparer.OrdinalIgnoreCase);

        var steps = new List<PlannerStep>(parsed.Steps.Count);
        foreach (PlannerStepDto dto in parsed.Steps)
        {
            if (string.IsNullOrWhiteSpace(dto.SpecialistKey))
                return ([], "step missing specialist_key");
            if (!validKeys.Contains(dto.SpecialistKey))
                return ([], $"unknown specialist_key '{dto.SpecialistKey}'");

            steps.Add(new PlannerStep
            {
                SpecialistKey = dto.SpecialistKey,
                Intent = string.IsNullOrWhiteSpace(dto.Intent) ? dto.SpecialistKey : dto.Intent,
                Action = string.IsNullOrWhiteSpace(dto.Action) ? "handle this request" : dto.Action,
            });
        }

        return (steps, null);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class PlannerResponse
    {
        public List<PlannerStepDto>? Steps { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class PlannerStepDto
    {
        public string? SpecialistKey { get; set; }
        public string? Intent { get; set; }
        public string? Action { get; set; }
    }
}
