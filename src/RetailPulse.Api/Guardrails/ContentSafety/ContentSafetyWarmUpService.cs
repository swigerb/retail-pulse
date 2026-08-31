using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Primes both halves of a Content Safety cold start before the first real scan:
/// the managed-identity token, and the pooled HTTPS connection to the endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Warming the token alone was measurably not enough. ADR-016 primed the credential
/// and the cold-start fail-open burst fell from four audit rows to one, but the
/// survivor was a transport failure, "the connection failed before a response"
/// (issue #273). With a warm token the first scan was still the call paying DNS,
/// TCP and the TLS handshake inside its own scan budget, so the handshake is moved
/// here too, off the scan's critical path.
/// </para>
/// <para>
/// One warm-up request covers both evaluator paths. The Prompt Shields raw path and
/// the SDK moderation path are deliberately registered against the same named
/// <see cref="HttpClient"/>, so they share a single connection pool.
/// </para>
/// <para>
/// The whole warm-up runs against one deadline and never throws, so host startup is
/// never gated on a remote call. <see cref="StartAsync"/> covers the runtime
/// pipeline; the startup agent-definition scan calls <see cref="WarmAsync"/>
/// directly because it runs during host configuration, before hosted services start.
/// </para>
/// </remarks>
internal sealed class ContentSafetyWarmUpService : IHostedService
{
    // The shared client's resilience handler caps every attempt at
    // ContentSafety.TimeoutMs, so a larger warm-up budget buys nothing on a single
    // attempt. A second attempt is what actually turns spare budget into a second
    // chance at the handshake. There is no backoff between them because a refused
    // or timed-out connection is not rate limited: there is nothing to wait for.
    private const int _maxTransportAttempts = 2;

    private readonly ContentSafetyTokenProvider _tokens;
    private readonly IHttpClientFactory _httpFactory;
    private readonly GuardrailsConfig _guardrails;
    private readonly ILogger<ContentSafetyWarmUpService> _logger;

    public ContentSafetyWarmUpService(
        ContentSafetyTokenProvider tokens,
        IHttpClientFactory httpFactory,
        GuardrailsConfig guardrails,
        ILogger<ContentSafetyWarmUpService> logger)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(guardrails);
        ArgumentNullException.ThrowIfNull(logger);
        _tokens = tokens;
        _httpFactory = httpFactory;
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
        // Fire-and-forget on a background task so a slow credential endpoint or a
        // slow handshake can never delay host startup. WarmAsync is itself bounded.
        WarmUpCompletion = Task.Run(() => WarmAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs the full warm-up: token first, then transport with whatever remains of
    /// the budget. Public so the startup agent-definition scan primes exactly the
    /// way the runtime pipeline does, rather than the two priming paths drifting.
    /// </summary>
    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        var budget = TimeSpan.FromMilliseconds(
            Math.Max(200, _guardrails.ContentSafety.WarmUpTimeoutMs));
        long deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);

        await WarmTokenAsync(budget, cancellationToken).ConfigureAwait(false);
        await WarmTransportAsync(deadline, cancellationToken).ConfigureAwait(false);
    }

    private async Task WarmTokenAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            ContentSafetyWarmUpResult result = await _tokens
                .WarmUpAsync(budget, cancellationToken)
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

    private async Task WarmTransportAsync(long deadline, CancellationToken cancellationToken)
    {
        HttpClient http;
        try
        {
            http = _httpFactory.CreateClient(ContentSafetyServiceCollectionExtensions.HttpClientName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content Safety transport warm-up could not resolve the shared client.");
            return;
        }

        for (int attempt = 1; attempt <= _maxTransportAttempts; attempt++)
        {
            TimeSpan remaining = RemainingBudget(deadline);
            if (remaining <= TimeSpan.Zero)
            {
                _logger.LogWarning(
                    "Content Safety transport warm-up skipped: the warm-up budget was spent acquiring the token.");
                return;
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(remaining);

            try
            {
                // Any response at all proves DNS, TCP and TLS completed and leaves a
                // pooled connection behind for the first real scan. The status is
                // deliberately not inspected: this is a handshake, not a health check,
                // so a 404 on the endpoint root serves the purpose as well as a 200.
                using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                using HttpResponseMessage response = await http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Content Safety transport warm-up established a connection on attempt {Attempt} (status {Status}).",
                    attempt,
                    (int)response.StatusCode);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt == _maxTransportAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "Content Safety transport warm-up did not establish a connection in {Attempts} attempt(s); the first scan may pay the TLS handshake.",
                        attempt);
                }
            }
        }
    }

    private static TimeSpan RemainingBudget(long deadline)
    {
        long ticks = deadline - Stopwatch.GetTimestamp();
        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }
}
