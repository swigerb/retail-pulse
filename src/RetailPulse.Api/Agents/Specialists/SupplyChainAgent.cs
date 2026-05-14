using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Supply Chain specialist — handles inventory levels, supply disruptions,
/// fulfillment rates, and overall supply health assessments.
/// Uses its own tool set and lower temperature (0.3) for analytical precision.
/// </summary>
public class SupplyChainAgent : ISpecialistAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;
    private readonly IEnumerable<AITool> _tools;

    public string Key => "supply-chain";
    public string DisplayName => "Supply Chain Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.SupplyShipments
    ];

    public SupplyChainAgent(
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
            FallbackReply = "I wasn't able to generate a supply chain analysis."
        };

        return _pipeline.ExecuteAsync(context, ct);
    }
}