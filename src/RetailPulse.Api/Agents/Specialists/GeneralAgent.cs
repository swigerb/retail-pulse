using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// The General specialist — handles unclassified and general/fallback queries.
/// This is the refactored RetailPulseAgent, preserving all existing tool access
/// and behavior. The router sends anything it can't classify here.
/// </summary>
public class GeneralAgent : ISpecialistAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;
    private readonly IEnumerable<AITool> _tools;

    public string Key => "general";
    public string DisplayName => "General Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.General,
        AgentIntent.PromotionTrade,
        AgentIntent.SupplyShipments,
        AgentIntent.CompetitiveMarket,
        AgentIntent.SentimentField
    ];

    public GeneralAgent(
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
            FallbackReply = "I wasn't able to generate a response."
        };

        return _pipeline.ExecuteAsync(context, ct);
    }
}