using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Demand Forecasting specialist — handles all demand/forecasting queries:
/// historical trends, 90-day predictions, seasonality analysis, depletion velocity,
/// and risk identification. Uses its own tool set and lower temperature (0.3)
/// for analytical precision.
/// </summary>
public class DemandForecastAgent : ISpecialistAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;
    public string Model => _agentDef.Model;
    private readonly IEnumerable<AITool> _tools;

    public string Key => "demand-forecasting";
    public string DisplayName => "Demand Forecast Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.DemandForecasting
    ];

    public DemandForecastAgent(
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
            FallbackReply = "I wasn't able to generate a forecast response."
        };

        return _pipeline.ExecuteAsync(context, ct);
    }
}
