using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents.Specialists;

/// <summary>
/// Promotion Planning specialist — thin shim over
/// <see cref="ConfiguredSpecialistAgent"/> that retains the bespoke approval-gate
/// hook (<see cref="CheckApprovalAsync"/>) called by the Task Module endpoint.
/// The LLM path is fully data-driven.
/// </summary>
public sealed class PromoPlanningAgent : ConfiguredSpecialistAgent
{
    private readonly IApprovalGate? _approvalGate;

    public PromoPlanningAgent(
        IAgentExecutionPipeline pipeline,
        AgentDefinition agentDef,
        IEnumerable<AITool> tools,
        IApprovalGate? approvalGate = null)
        : base(pipeline, EnsureDefaults(agentDef), tools)
    {
        _approvalGate = approvalGate;
    }

    private static AgentDefinition EnsureDefaults(AgentDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        def = def.Clone();

        if (string.IsNullOrWhiteSpace(def.Key))
            def.Key = "promo-planning";
        if (def.Intents.Count == 0)
            def.Intents = [AgentIntent.PromotionTrade];
        if (string.IsNullOrWhiteSpace(def.DisplayName))
            def.DisplayName = "Promotion Planning Agent";
        if (string.IsNullOrWhiteSpace(def.FallbackReply))
            def.FallbackReply = "I wasn't able to generate a promotion analysis.";
        return def;
    }

    /// <summary>
    /// Checks if the given spend amount triggers the approval gate for high-spend campaigns.
    /// </summary>
    public async Task<ApprovalResult?> CheckApprovalAsync(
        double spend, double roi, string userId, string description, CancellationToken ct = default)
    {
        if (_approvalGate == null) return null;

        bool requiresApproval = spend > 500_000 || (spend > 100_000 && roi < 10);
        if (!requiresApproval) return null;

        string urgency = spend > 500_000 ? "High" : "Medium";
        string impact = $"Campaign spend: ${spend:N0}, Expected ROI: {roi:F1}%";

        ApprovalRequest request = await _approvalGate.RequestApprovalAsync(new ApprovalContext(
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
