using Microsoft.Extensions.Options;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Startup reconciliation for durable approval requests. Runs before Kestrel begins
/// accepting traffic (hosted services complete <see cref="IHostedService.StartAsync"/>
/// before the generic web host service ends its own StartAsync in ASP.NET Core), so
/// any Pending row left behind by a previous process is closed — or resumed — before
/// the first HTTP request or agent invocation can create a new one.
///
/// <para>
/// The delegated <see cref="IApprovalResumeStrategy"/> is the single seam Wave 2
/// (issues #93/#94) replaces to swap deterministic orphaning for a checkpoint-driven
/// resume. Reconciliation is idempotent: a repeated call is a no-op because every
/// previously eligible row has already been terminated (or adopted by this instance).
/// </para>
/// </summary>
public sealed class ApprovalReconciliationBackgroundService : IHostedService
{
    private readonly SqliteApprovalGate _gate;
    private readonly IApprovalResumeStrategy _strategy;
    private readonly IOptions<ApprovalOptions> _options;
    private readonly ILogger<ApprovalReconciliationBackgroundService> _logger;

    public ApprovalReconciliationBackgroundService(
        IApprovalGate gate,
        IApprovalResumeStrategy strategy,
        IOptions<ApprovalOptions> options,
        ILogger<ApprovalReconciliationBackgroundService> logger)
    {
        // The reconciliation surface (ReconcilePendingAsync) lives on the concrete
        // implementation, not IApprovalGate, so we intentionally require the SQLite
        // gate concrete type here. This keeps IApprovalGate lean for callers that
        // never need to reconcile.
        if (gate is not SqliteApprovalGate sqlite)
            throw new InvalidOperationException(
                $"ApprovalReconciliationBackgroundService requires {nameof(SqliteApprovalGate)}; got {gate.GetType().Name}.");

        _gate = sqlite;
        _strategy = strategy;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.ReconcileOnStartup)
        {
            _logger.LogInformation("Approval reconciliation disabled by configuration; skipping startup sweep.");
            return;
        }

        try
        {
            int terminated = await _gate.ReconcilePendingAsync(_strategy, cancellationToken);
            _logger.LogInformation(
                "Approval reconciliation complete on instance {InstanceId}: {Terminated} orphaned request(s) closed.",
                _gate.InstanceId, terminated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Reconciliation failure must NOT wedge startup — an operator would rather
            // serve traffic with a small pool of stuck-Pending rows (which the next
            // successful startup can reconcile) than fail to boot at all. The error is
            // logged so it surfaces in observability.
            _logger.LogError(ex, "Approval reconciliation failed on instance {InstanceId}.", _gate.InstanceId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
