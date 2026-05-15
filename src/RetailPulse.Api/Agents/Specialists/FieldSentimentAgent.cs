using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Field Sentiment specialist — handles distributor feedback, field sales reports,
/// and qualitative sentiment queries. Uses only GetFieldSentiment + CreateChart
/// tools for minimal token usage and focused execution.
/// </summary>
public class FieldSentimentAgent : ISpecialistAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;
    public string Model => _agentDef.Model;
    private readonly IEnumerable<AITool> _tools;

    public string Key => "field-sentiment";
    public string DisplayName => "Field Sentiment Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.SentimentField
    ];

    public FieldSentimentAgent(
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
            FallbackReply = "I wasn't able to retrieve field sentiment data."
        };

        return _pipeline.ExecuteAsync(context, ct);
    }
}
