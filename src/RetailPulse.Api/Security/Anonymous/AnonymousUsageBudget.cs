namespace RetailPulse.Api.Security.Anonymous;

/// <summary>Point-in-time view of the anonymous daily budget, for telemetry and tests.</summary>
public readonly record struct AnonymousBudgetSnapshot(
    DateOnly Day,
    int Requests,
    int MaxRequests,
    long Tokens,
    long MaxTokens,
    decimal CostUsd,
    decimal MaxCostUsd,
    bool BreakerTripped,
    string? TripReason);

/// <summary>
/// Fail-closed daily circuit breaker for anonymous billable use.
///
/// Anonymous mode has no per-user accountability, so it MUST have a hard global ceiling. This
/// tracks the current UTC day's request count, cumulative model tokens, and estimated cost, and
/// trips a breaker the moment any ceiling is exceeded — after which every new request is denied
/// until the counters reset at UTC midnight.
///
/// <para><b>Truthful accounting.</b> A request slot is consumed at request admission
/// (<see cref="TryBeginRequest"/>), BEFORE the cache is consulted, so a cache hit cannot bypass
/// the request ceiling. Token/cost accumulation (<see cref="RecordUsage"/>) is driven by the real
/// <c>ICostTracker</c> usage stream, where cache hits correctly contribute zero tokens and zero
/// cost. Usage is never fabricated.</para>
///
/// <para><b>Replica-local.</b> Counters live in process memory. Distributed exact enforcement is
/// not possible with the current replica-local storage, so hosted Anonymous is pinned to
/// <c>maxReplicas=1</c> and the ceilings are set conservatively. This is explicitly NOT equivalent
/// to authenticated production; counters also reset on restart.</para>
/// </summary>
public sealed class AnonymousUsageBudget
{
    private readonly AnonymousAuthOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private DateOnly _day;
    private int _requests;
    private long _tokens;
    private decimal _costUsd;
    private bool _tripped;
    private string? _tripReason;

    /// <summary>
    /// When false (Development without hosted guardrails), the breaker is advisory only and never
    /// denies — but still counts, so tests and telemetry are meaningful.
    /// </summary>
    public bool Enforced { get; }

    public AnonymousUsageBudget(AnonymousAuthOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Enforced = options.HostedGuardrailsEnforced;
        _day = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Attempts to admit one request. Consumes a request slot when admitted. Returns false
    /// (fail-closed) when the breaker is tripped or the daily request ceiling is reached. In
    /// non-enforced Development mode it always admits but still increments the counter.
    /// </summary>
    public bool TryBeginRequest(out string? denyReason)
    {
        lock (_gate)
        {
            RollDayIfNeeded();

            if (_tripped)
            {
                denyReason = _tripReason ?? "Anonymous daily budget exhausted.";
                _requests++;
                return !Enforced;
            }

            if (_requests >= _options.DailyMaxRequests)
            {
                Trip($"Anonymous daily request ceiling reached ({_options.DailyMaxRequests}).");
                denyReason = _tripReason;
                _requests++;
                return !Enforced;
            }

            _requests++;
            denyReason = null;
            return true;
        }
    }

    /// <summary>
    /// Records real model usage (tokens and estimated cost) against the daily budget. Cache hits
    /// arrive here as zero tokens/zero cost and therefore never advance the token/cost ceilings.
    /// Trips the breaker when the token or cost ceiling is exceeded.
    /// </summary>
    public void RecordUsage(long tokens, decimal costUsd)
    {
        if (tokens <= 0 && costUsd <= 0m)
        {
            return;
        }

        lock (_gate)
        {
            RollDayIfNeeded();
            _tokens += Math.Max(0, tokens);
            _costUsd += Math.Max(0m, costUsd);

            if (!_tripped && _tokens >= _options.DailyMaxTokens)
            {
                Trip($"Anonymous daily token ceiling reached ({_options.DailyMaxTokens}).");
            }
            else if (!_tripped && _costUsd >= _options.DailyMaxCostUsd)
            {
                Trip($"Anonymous daily cost ceiling reached (${_options.DailyMaxCostUsd}).");
            }
        }
    }

    public AnonymousBudgetSnapshot Snapshot()
    {
        lock (_gate)
        {
            RollDayIfNeeded();
            return new AnonymousBudgetSnapshot(
                _day, _requests, _options.DailyMaxRequests,
                _tokens, _options.DailyMaxTokens,
                _costUsd, _options.DailyMaxCostUsd,
                _tripped, _tripReason);
        }
    }

    private void RollDayIfNeeded()
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        if (today != _day)
        {
            _day = today;
            _requests = 0;
            _tokens = 0;
            _costUsd = 0m;
            _tripped = false;
            _tripReason = null;
        }
    }

    private void Trip(string reason)
    {
        _tripped = true;
        _tripReason = reason;
    }
}
