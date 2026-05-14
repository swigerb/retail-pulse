using RetailPulse.Contracts;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Shared execution pipeline for specialist agents. Encapsulates the common
/// pattern of message construction, LLM invocation, telemetry collection,
/// tool span extraction, chart extraction, token accounting, and error handling.
/// Each specialist agent delegates to this pipeline while retaining ownership
/// of its identity, tools, and any domain-specific post-processing.
/// </summary>
public interface IAgentExecutionPipeline
{
    /// <summary>
    /// Executes the standard agent pipeline: build messages → call LLM → collect
    /// telemetry → extract tool spans → extract charts → account tokens → return response.
    /// </summary>
    Task<ChatResponse> ExecuteAsync(AgentExecutionContext context, CancellationToken ct = default);
}

/// <summary>
/// Per-invocation context that a specialist agent passes to the shared pipeline.
/// Contains everything the pipeline needs that varies per agent and per request.
/// </summary>
public record AgentExecutionContext
{
    /// <summary>Agent name from AgentDefinition — used in telemetry spans and logging.</summary>
    public required string AgentName { get; init; }

    /// <summary>System prompt for the agent.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>LLM temperature for this agent.</summary>
    public required float Temperature { get; init; }

    /// <summary>Model name — used for token pricing lookup.</summary>
    public required string ModelName { get; init; }

    /// <summary>The chat request from the user.</summary>
    public required ChatRequest Request { get; init; }

    /// <summary>Tools available to this agent.</summary>
    public required IEnumerable<Microsoft.Extensions.AI.AITool> Tools { get; init; }

    /// <summary>
    /// Fallback reply text when the LLM returns null/empty.
    /// Each agent can customize this to its domain.
    /// </summary>
    public string FallbackReply { get; init; } = "I wasn't able to generate a response.";

    /// <summary>
    /// Optional callback invoked for each tool result during span extraction.
    /// Allows agents like CompetitiveIntelAgent to fire alerts or perform
    /// domain-specific processing on tool results without duplicating the pipeline.
    /// </summary>
    public Func<string, CancellationToken, Task>? OnToolResult { get; init; }
}
