using Microsoft.Extensions.Options;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Periodic sweep that enforces
/// <see cref="PlanReviewOptions.DefaultReviewTimeout"/> and
/// <see cref="PlanReviewOptions.ClarificationTimeout"/> against Pending plan-
/// review and clarification rows. Replaces the pre-#94 blocked-thread wait
/// in <see cref="IApprovalGate.WaitForApprovalAsync"/> — the timeout is now a
/// terminal transition on the row, driven by an injected <see cref="TimeProvider"/>
/// (so a fake clock advances the sweep deterministically in tests).
///
/// <para>
/// On every tick, expired rows transition to
/// <see cref="ApprovalDecision.TimedOut"/> through
/// <see cref="IApprovalGate.RespondAsync"/> (race-safe conditional UPDATE), and
/// the completion service is invoked so the plan finalises with reason
/// <see cref="PlanReviewTerminalReason.ReviewTimedOut"/>.
/// </para>
/// </summary>
public sealed class PlanReviewTimeoutBackgroundService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly TimeProvider _clock;
    private readonly IOptions<PlanReviewOptions> _options;
    private readonly ILogger<PlanReviewTimeoutBackgroundService> _logger;

    public PlanReviewTimeoutBackgroundService(
        IServiceProvider sp,
        TimeProvider clock,
        IOptions<PlanReviewOptions> options,
        ILogger<PlanReviewTimeoutBackgroundService> logger)
    {
        _sp = sp;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(15);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PlanReviewTimeoutBackgroundService tick failed; retrying next cycle.");
                }
                try
                {
                    await Task.Delay(tick, _clock, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _sp.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        IApprovalGate gate = sp.GetRequiredService<IApprovalGate>();
        PlanReviewCompletionService completion = sp.GetRequiredService<PlanReviewCompletionService>();

        // We do not have a per-subject-scanning helper on IApprovalGate, so we
        // scope the sweep to rows visible via GetHistoryAsync + a subject scan
        // gathered from the recent history. This bounds memory to the number
        // of distinct recent subjects, which is small in this deployment.
        IReadOnlyList<ApprovalRequest> recent = await gate.GetHistoryAsync(500, ct);
        var subjects = recent.Select(r => r.Context.UserId).Distinct(StringComparer.Ordinal).ToList();

        DateTimeOffset now = _clock.GetUtcNow();
        foreach (string subject in subjects)
        {
            IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject, ct);
            foreach (ApprovalRequest row in pending)
            {
                bool expired = row.Context.Kind switch
                {
                    ApprovalKind.PlanReview =>
                        now - row.CreatedAt >= _options.Value.DefaultReviewTimeout,
                    ApprovalKind.Clarification =>
                        now - row.CreatedAt >= _options.Value.ClarificationTimeout,
                    _ => false,
                };
                if (!expired) continue;

                await gate.RespondAsync(
                    row.RequestId, ApprovalDecision.TimedOut,
                    comment: "Plan review sweep observed deadline elapse.",
                    responsePayload: null, ct: ct);

                if (!string.IsNullOrWhiteSpace(row.Context.PlanId))
                {
                    await completion.ResolveAsync(row.Context.PlanId, subject, ct);
                }
            }
        }
    }
}
