using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Store Operations specialist — handles store performance analysis,
/// underperformer detection, stockout prediction, and store recommendations.
/// Uses temperature 0.3 for analytical precision.
/// </summary>
public class StoreOpsAgent : ISpecialistAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;
    public string Model => _agentDef.Model;
    private readonly IEnumerable<AITool> _tools;

    public string Key => "store-ops";
    public string DisplayName => "Store Operations Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.StoreOps
    ];

    public StoreOpsAgent(
        IAgentExecutionPipeline pipeline,
        AgentDefinition agentDef,
        IEnumerable<AITool> tools)
    {
        _pipeline = pipeline;
        _agentDef = agentDef;
        _tools = tools;
    }

    public Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken ct = default)
    {
        var context = new AgentExecutionContext
        {
            AgentName = _agentDef.Name,
            SystemPrompt = _agentDef.SystemPrompt,
            Temperature = (float)_agentDef.Temperature,
            ModelName = _agentDef.Model,
            Request = request,
            Tools = _tools,
            FallbackReply = "I wasn't able to generate a store operations response."
        };

        return _pipeline.ExecuteAsync(context, ct);
    }
}
