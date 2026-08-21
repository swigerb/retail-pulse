using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Restart-time recovery: scan every subject's approval history for plan-
/// review / clarification rows that reached a terminal decision while the
/// API was down (or during process startup) and drive them through
/// <see cref="PlanReviewCompletionService.ResolveAsync"/> so the final
/// response is delivered / persisted. Idempotent: the completion service
/// short-circuits when the plan is already terminal.
///
/// <para>
/// Runs AFTER <see cref="ApprovalReconciliationBackgroundService"/> so any
/// row still Pending under a previous instance has already been re-adopted
/// by the current process. That means every terminal row we see here is
/// legitimately owned by the reviewer's response, not by an orphan sweep.
/// </para>
/// </summary>
public sealed class PlanReviewRestartRecoveryService : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<PlanReviewRestartRecoveryService> _logger;

    public PlanReviewRestartRecoveryService(
        IServiceProvider sp,
        ILogger<PlanReviewRestartRecoveryService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _sp.CreateAsyncScope();
            IServiceProvider sp = scope.ServiceProvider;
            IApprovalGate gate = sp.GetRequiredService<IApprovalGate>();
            IPlanStore planStore = sp.GetRequiredService<IPlanStore>();
            PlanReviewCompletionService completion =
                sp.GetRequiredService<PlanReviewCompletionService>();

            // Walk history — decisions may arrive at any point; we look for
            // rows whose plan is still stuck in an awaiting-* state.
            IReadOnlyList<ApprovalRequest> history = await gate.GetHistoryAsync(1000, cancellationToken);
            var candidates = new Dictionary<(string PlanId, string Subject), ApprovalRequest>();
            foreach (ApprovalRequest r in history)
            {
                if (r.Context.Kind is not (ApprovalKind.PlanReview or ApprovalKind.Clarification))
                    continue;
                if (r.Decision == ApprovalDecision.Pending)
                    continue;
                if (string.IsNullOrWhiteSpace(r.Context.PlanId))
                    continue;

                (string PlanId, string Subject) key = (r.Context.PlanId!, r.Context.UserId);
                if (candidates.TryGetValue(key, out ApprovalRequest? existing))
                {
                    if ((r.RespondedAt ?? r.CreatedAt) > (existing.RespondedAt ?? existing.CreatedAt))
                        candidates[key] = r;
                }
                else
                {
                    candidates[key] = r;
                }
            }

            int resolved = 0;
            foreach ((string planId, string subject) in candidates.Keys)
            {
                PlanDetailDto? plan = await planStore.GetPlanAsync(subject, planId, cancellationToken);
                if (plan is null) continue;
                if (plan.Status is not (PlanStatus.AwaitingReview or PlanStatus.AwaitingClarification))
                    continue;

                PlanReviewCompletionResult result = await completion.ResolveAsync(planId, subject, cancellationToken);
                _logger.LogInformation(
                    "Plan {PlanId} restart-recovered via {Kind}.",
                    planId, result.Kind);
                resolved++;
            }

            _logger.LogInformation(
                "PlanReviewRestartRecoveryService completed: {Count} plan(s) resumed.",
                resolved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PlanReviewRestartRecoveryService failed. Traffic may proceed; the next boot will retry.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
