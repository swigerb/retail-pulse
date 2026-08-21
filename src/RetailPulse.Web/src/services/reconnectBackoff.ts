/**
 * Reconnect backoff schedule for real-time channels (issue #92).
 *
 * Pure function so the schedule is deterministic and unit-testable without
 * standing up a SignalR connection. The caller (SignalR's `IRetryPolicy` or
 * a bespoke reconnect loop) supplies the previous retry count; we return
 * either the delay in milliseconds before the next attempt, or `null` to
 * signal the retry budget is exhausted and the channel should transition to
 * a terminal disconnected state.
 *
 * We intentionally cap both the per-attempt delay (`maxDelayMs`) and the
 * total attempt count (`maxAttempts`) so a persistently unhealthy channel
 * never spins forever and never grows an unbounded delay that hides the
 * real problem from the user.
 */

export interface ReconnectBackoffOptions {
  /** Delay in ms before the FIRST reconnect attempt (previousRetryCount === 0). */
  readonly initialDelayMs: number;
  /** Ceiling for any single delay after exponential growth. */
  readonly maxDelayMs: number;
  /** Exponential base. Typical value: 2. */
  readonly multiplier: number;
  /**
   * Hard cap on retry attempts. When `previousRetryCount >= maxAttempts`
   * the function returns `null` so SignalR (or the bespoke loop) transitions
   * to the terminal disconnected state.
   */
  readonly maxAttempts: number;
  /**
   * Optional pseudo-random jitter multiplier in `[0, 1)`. When provided we
   * return `capped * (1 - jitter/2 + rng() * jitter)` so the schedule stays
   * bounded but avoids thundering-herd reconnects. Callers omit this in
   * tests to keep the schedule deterministic.
   */
  readonly jitter?: number;
  /** Optional RNG override (0 <= rng() < 1). Defaults to Math.random. */
  readonly rng?: () => number;
}

/**
 * Default channel-resilience schedule, tuned to sit well under the shortest
 * plausible intermediary idle timeout (~60s corporate proxy) so the user sees
 * "reconnecting…" quickly, then backs off to avoid hammering the API during a
 * real outage. Terminal after 8 attempts (~2 minutes elapsed), at which point
 * the UI surfaces a "disconnected" state.
 */
export const DEFAULT_RECONNECT_BACKOFF: ReconnectBackoffOptions = {
  initialDelayMs: 1_000,
  maxDelayMs: 30_000,
  multiplier: 2,
  maxAttempts: 8,
};

/**
 * Computes the delay before the next reconnect attempt, or `null` when the
 * retry budget is exhausted. `previousRetryCount` matches SignalR's
 * `RetryContext.previousRetryCount` — 0 means "no attempts yet, decide when
 * to make the first one."
 */
export function computeReconnectDelayMs(
  previousRetryCount: number,
  options: ReconnectBackoffOptions = DEFAULT_RECONNECT_BACKOFF,
): number | null {
  if (!Number.isFinite(previousRetryCount) || previousRetryCount < 0) {
    throw new RangeError('previousRetryCount must be a non-negative finite number.');
  }
  if (previousRetryCount >= options.maxAttempts) {
    return null;
  }

  const raw = options.initialDelayMs * Math.pow(options.multiplier, previousRetryCount);
  const capped = Math.min(raw, options.maxDelayMs);

  const jitter = options.jitter;
  if (jitter === undefined || jitter <= 0) {
    return Math.max(0, Math.round(capped));
  }
  const clampedJitter = Math.min(jitter, 1);
  const rng = options.rng ?? Math.random;
  const factor = 1 - clampedJitter / 2 + rng() * clampedJitter;
  return Math.max(0, Math.round(capped * factor));
}

/**
 * Materialises the full deterministic schedule (jitter disabled) up to and
 * including the terminal `null` marker. Useful for tests and diagnostics.
 */
export function buildReconnectSchedule(
  options: ReconnectBackoffOptions = DEFAULT_RECONNECT_BACKOFF,
): readonly (number | null)[] {
  const schedule: (number | null)[] = [];
  for (let attempt = 0; attempt < options.maxAttempts; attempt++) {
    const delay = computeReconnectDelayMs(attempt, { ...options, jitter: 0 });
    schedule.push(delay);
  }
  schedule.push(null);
  return schedule;
}
