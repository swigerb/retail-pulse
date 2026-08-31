using Microsoft.Extensions.Hosting;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Pre-acquires the Content Safety managed-identity token at host start so the
/// first real scan is not the one paying the cold AAD/IMDS round-trip inside its
/// own timeout budget. This covers the runtime pipeline; the startup
/// agent-definition scan is warmed separately, before it runs, because it
/// executes during host configuration rather than after the host starts.
/// </summary>
/// <remarks>
/// The warm-up is time-boxed and fire-and-forget: <see cref="StartAsync"/>
/// returns immediately so host startup is never gated on a remote credential
/// call, and the underlying warm-up is bounded so an unreachable credential
/// endpoint cannot stall the service.
/// </remarks>
internal sealed class ContentSafetyWarmUpService : IHostedService
{
    private readonly ContentSafetyTokenProvider _tokens;
    private readonly GuardrailsConfig _guardrails;
    private readonly ILogger<ContentSafetyWarmUpService> _logger;

    public ContentSafetyWarmUpService(
        ContentSafetyTokenProvider tokens,
        GuardrailsConfig guardrails,
        ILogger<ContentSafetyWarmUpService> logger)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(guardrails);
        ArgumentNullException.ThrowIfNull(logger);
        _tokens = tokens;
        _guardrails = guardrails;
        _logger = logger;
    }

    /// <summary>
    /// Exposed for tests so they can await the fire-and-forget warm-up
    /// deterministically. Null until <see cref="StartAsync"/> has run.
    /// </summary>
    public Task? WarmUpCompletion { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var budget = TimeSpan.FromMilliseconds(
            Math.Max(200, _guardrails.ContentSafety.WarmUpTimeoutMs));

        // Fire-and-forget on a background task so a slow credential endpoint can
        // never delay host startup. The warm-up itself is bounded by budget.
        WarmUpCompletion = Task.Run(() => WarmAsync(budget), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmAsync(TimeSpan budget)
    {
        try
        {
            ContentSafetyWarmUpResult result = await _tokens
                .WarmUpAsync(budget, CancellationToken.None)
                .ConfigureAwait(false);

            if (result is ContentSafetyWarmUpResult.Warmed or ContentSafetyWarmUpResult.AlreadyWarm)
            {
                _logger.LogInformation("Content Safety token warm-up completed ({Result}).", result);
            }
            else
            {
                _logger.LogWarning(
                    "Content Safety token warm-up did not prime a token ({Result}); the first scan may pay the cold token cost.",
                    result);
            }
        }
        catch (Exception ex)
        {
            // Warm-up is best-effort. A failure here must never surface at startup.
            _logger.LogWarning(ex, "Content Safety token warm-up failed unexpectedly.");
        }
    }
}
