using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Centralized adapter that runs a chat request through a real Microsoft Agent
/// Framework (MAF) <see cref="ChatClientAgent"/> primitive. Every agent-style call
/// in Retail Pulse — the 10 specialists via <see cref="AgentExecutionPipeline"/>,
/// the router's intent classification, and the consensus council's votes and
/// synthesis — flows through this seam so a single, tested code path owns the
/// MAF conversion.
/// </summary>
/// <remarks>
/// <para>
/// The adapter is intentionally minimal: it constructs a per-invocation
/// <see cref="ChatClientAgent"/> around the caller-provided <see cref="IChatClient"/>
/// and invokes <see cref="ChatClientAgent.RunAsync(IEnumerable{ChatMessage}, AgentSession, ChatClientAgentRunOptions, CancellationToken)"/>.
/// The returned <see cref="AgentResponse"/> is a genuine MAF type; callers extract
/// text, response messages, and token usage from it directly.
/// </para>
/// <para>
/// <see cref="ChatClientAgentOptions.UseProvidedChatClientAsIs"/> is set to
/// <see langword="true"/> so MAF does <b>not</b> re-decorate the provided
/// <see cref="IChatClient"/>. That is critical: the production DI stack already
/// wraps the chat client with <c>UseFunctionInvocation(client =&gt;
/// client.MaximumIterationsPerRequest = 3)</c> and <c>UseOpenTelemetry(...)</c>
/// in <c>Program.cs</c>. Letting MAF add its own <c>FunctionInvokingChatClient</c>
/// on top would silently override the ADR-006 iteration cap and duplicate OTel
/// spans. Preserving the caller's decorator stack keeps the tool-context budget
/// contract, InstrumentedToolMiddleware timings, ApprovalTool blocking behaviour,
/// and MCP HttpClient retry/circuit-breaker/cache all working unchanged.
/// </para>
/// <para>
/// The adapter is stateless and static. It intentionally does not hold session
/// state: every Retail Pulse request builds its own message list (system prompt
/// + trimmed history + user message) via <see cref="AgentExecutionPipeline.BuildMessages"/>,
/// so the MAF <see cref="AgentSession"/> per run is disposable and never persisted.
/// </para>
/// </remarks>
public static class MafAgentInvoker
{
    /// <summary>
    /// Runs the supplied messages through a MAF <see cref="ChatClientAgent"/>
    /// wrapping <paramref name="chatClient"/> and returns the raw
    /// <see cref="AgentResponse"/> so callers can extract text, messages, and
    /// usage using the same MAF surface the rest of the framework exposes.
    /// </summary>
    /// <param name="chatClient">The pre-decorated chat client to invoke.</param>
    /// <param name="agentName">Human-readable agent name (used for MAF telemetry / metadata).</param>
    /// <param name="messages">Chat messages to send to the model. Ownership stays with the caller.</param>
    /// <param name="chatOptions">Per-invocation chat options (temperature, tools, response format).</param>
    /// <param name="loggerFactory">Optional logger factory forwarded to <see cref="ChatClientAgent"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<AgentResponse> RunAsync(
        IChatClient chatClient,
        string agentName,
        IEnumerable<ChatMessage> messages,
        ChatOptions chatOptions,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(chatOptions);

        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentName,
            // Keep the caller-provided decorator stack (FunctionInvokingChatClient
            // with MaximumIterationsPerRequest = 3, OpenTelemetry, resilience) intact.
            UseProvidedChatClientAsIs = true,
        };

        ChatClientAgent agent = loggerFactory is null
            ? new ChatClientAgent(chatClient, agentOptions)
            : new ChatClientAgent(chatClient, agentOptions, loggerFactory);

        var runOptions = new ChatClientAgentRunOptions(chatOptions);

        // Pass session:null so MAF creates an ephemeral in-memory session per request.
        // Retail Pulse builds the full conversation transcript into `messages` on every
        // call (system prompt + trimmed history + user message), so cross-request
        // session persistence is neither expected nor desired here.
        AgentResponse response = await agent
            .RunAsync(messages, session: null, runOptions, ct)
            .ConfigureAwait(false);

        return response;
    }
}
