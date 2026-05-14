using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Promotion Planning specialist — handles promotion/trade queries:
/// promo history analysis, lift calculations, timing evaluation, ROI estimation,
/// and campaign approval gating. Uses its own tool set and lower temperature (0.3).
/// </summary>
public class PromoPlanningAgent : ISpecialistAgent
{
    private readonly IAgentExecutionPipeline _pipeline;
    private readonly AgentDefinition _agentDef;
    public string Model => _agentDef.Model;
    private readonly IEnumerable<AITool> _tools;
    private readonly IApprovalGate? _approvalGate;

    public string Key => "promo-planning";
    public string DisplayName => "Promotion Planning Agent";
    public IReadOnlyList<string> SupportedIntents { get; } =
    [
        AgentIntent.PromotionTrade
    ];

    public PromoPlanningAgent(
        IAgentExecutionPipeline pipeline,
        AgentDefinition agentDef,
        IEnumerable<AITool> tools,
        IApprovalGate? approvalGate = null)
    {
        _pipeline = pipeline;
        _agentDef = agentDef;
        _tools = tools;
        _approvalGate = approvalGate;
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
            FallbackReply = "I wasn't able to generate a promotion analysis."
        };

        return _pipeline.ExecuteAsync(context, ct);
    }

    /// <summary>
    /// Checks if the given spend amount triggers the approval gate for high-spend campaigns.
    /// </summary>
    public async Task<ApprovalResult?> CheckApprovalAsync(
        double spend, double roi, string userId, string description, CancellationToken ct = default)
    {
        if (_approvalGate == null) return null;

        var requiresApproval = spend > 500_000 || (spend > 100_000 && roi < 10);
        if (!requiresApproval) return null;

        var urgency = spend > 500_000 ? "High" : "Medium";
        var impact = $"Campaign spend: ${spend:N0}, Expected ROI: {roi:F1}%";

        var request = await _approvalGate.RequestApprovalAsync(new ApprovalContext(
            AgentId: Key,
            UserId: userId,
            Action: description,
            Impact: impact,
            Urgency: urgency,
            Reasoning: $"High-spend promotion requires approval. Spend=${spend:N0}, ROI={roi:F1}%"
        ), ct);

        return await _approvalGate.GetResultAsync(request.RequestId, ct);
    }
}