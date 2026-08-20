using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Single specialist implementation built entirely from an
/// <see cref="AgentDefinition"/>. This is the generic path used for every
/// specialist that only needs the shared execution pipeline. Bespoke
/// specialists (Memory Management, Competitive Intel) still ship their
/// own class because they carry real domain logic beyond the pipeline.
/// </summary>
/// <remarks>
/// Adding a new specialist becomes: register its tools in the
/// <see cref="Tools.AgentToolRegistry"/>, add its entry to <c>prompts.yaml</c>
/// with <c>intents</c> and any <c>keyword_fast_paths</c>, and restart.
/// No new C# class is required — that is the objective of ADR-008.
/// </remarks>
public class ConfiguredSpecialistAgent : ISpecialistAgent, IPrefetchableAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly IReadOnlyList<AITool> _tools;

    public string Key { get; }
    public string DisplayName { get; }
    public string Model => Definition.Model;
    public IReadOnlyList<string> SupportedIntents { get; }
    public IReadOnlyList<string> KeywordFastPaths { get; }

    /// <summary>The underlying <see cref="AgentDefinition"/>. Exposed for orchestrators
    /// that need council-participant/scorecard metadata without a separate registry.</summary>
    public AgentDefinition Definition { get; }

    public ConfiguredSpecialistAgent(
        IAgentExecutionPipeline pipeline,
        AgentDefinition agentDef,
        IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(agentDef);
        ArgumentNullException.ThrowIfNull(tools);

        _pipeline = pipeline;
        Definition = agentDef;
        _tools = tools as IReadOnlyList<AITool> ?? [.. tools];

        if (string.IsNullOrWhiteSpace(agentDef.Key))
        {
            throw new ArgumentException(
                $"AgentDefinition.Key is required for '{agentDef.Name}'. " +
                "Set it explicitly in prompts.yaml (or via the loader defaulting).",
                nameof(agentDef));
        }

        Key = agentDef.Key;
        DisplayName = agentDef.EffectiveDisplayName;
        SupportedIntents = agentDef.Intents.Count > 0
            ? [.. agentDef.Intents]
            : [AgentIntent.General];
        KeywordFastPaths = [.. agentDef.KeywordFastPaths];
    }

    public Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
        => ExecuteAsync(request, prefetchedData: null, ct);

    public Task<ChatResponse> HandleWithPrefetchAsync(
        ChatRequest request,
        IReadOnlyDictionary<string, string>? prefetchedData,
        CancellationToken ct = default)
        => ExecuteAsync(request, prefetchedData, ct);

    /// <summary>
    /// Hook for bespoke subclasses (e.g., <see cref="CompetitiveIntelAgent"/>) to
    /// attach a domain-specific tool-result callback without duplicating the
    /// pipeline plumbing. Default returns <c>null</c> — no callback.
    /// </summary>
    protected virtual Func<string, CancellationToken, Task>? OnToolResult => null;

    private Task<ChatResponse> ExecuteAsync(
        ChatRequest request,
        IReadOnlyDictionary<string, string>? prefetchedData,
        CancellationToken ct)
    {
        var context = new AgentExecutionContext
        {
            AgentName = Definition.Name,
            SystemPrompt = Definition.SystemPrompt,
            Temperature = (float)Definition.Temperature,
            ModelName = Definition.Model,
            Request = request,
            Tools = _tools,
            FallbackReply = Definition.EffectiveFallbackReply,
            OnToolResult = OnToolResult,
            PrefetchedData = prefetchedData
        };

        return _pipeline.ExecuteAsync(context, ct);
    }
}
