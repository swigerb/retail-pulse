import { describe, it, expect } from 'vitest';
import {
  computeReconnectDelayMs,
  buildReconnectSchedule,
  DEFAULT_RECONNECT_BACKOFF,
} from '../services/reconnectBackoff';

describe('reconnectBackoff.computeReconnectDelayMs', () => {
  const options = {
    initialDelayMs: 1_000,
    maxDelayMs: 10_000,
    multiplier: 2,
    maxAttempts: 6,
  } as const;

  it('grows exponentially from initialDelay by multiplier', () => {
    expect(computeReconnectDelayMs(0, options)).toBe(1_000);
    expect(computeReconnectDelayMs(1, options)).toBe(2_000);
    expect(computeReconnectDelayMs(2, options)).toBe(4_000);
    expect(computeReconnectDelayMs(3, options)).toBe(8_000);
  });

  it('caps the delay at maxDelayMs', () => {
    expect(computeReconnectDelayMs(4, options)).toBe(10_000);
    expect(computeReconnectDelayMs(5, options)).toBe(10_000);
  });

  it('returns null once the retry budget is exhausted (terminal transition)', () => {
    expect(computeReconnectDelayMs(6, options)).toBeNull();
    expect(computeReconnectDelayMs(7, options)).toBeNull();
    expect(computeReconnectDelayMs(99, options)).toBeNull();
  });

  it('rejects negative or non-finite retry counts', () => {
    expect(() => computeReconnectDelayMs(-1, options)).toThrow(RangeError);
    expect(() => computeReconnectDelayMs(Number.NaN, options)).toThrow(RangeError);
    expect(() => computeReconnectDelayMs(Number.POSITIVE_INFINITY, options)).toThrow(RangeError);
  });

  it('applies deterministic jitter when an RNG is supplied', () => {
    // rng always returns 0.5 → factor = 1 - jitter/2 + 0.5*jitter = 1
    const delay = computeReconnectDelayMs(2, {
      ...options,
      jitter: 0.4,
      rng: () => 0.5,
    });
    expect(delay).toBe(4_000);

    // rng returns 0 → factor = 1 - jitter/2 = 0.8 for jitter=0.4
    const low = computeReconnectDelayMs(2, {
      ...options,
      jitter: 0.4,
      rng: () => 0,
    });
    expect(low).toBe(3_200);
  });
});

describe('reconnectBackoff.buildReconnectSchedule', () => {
  it('materialises the schedule ending with the terminal null marker', () => {
    const schedule = buildReconnectSchedule({
      initialDelayMs: 500,
      maxDelayMs: 4_000,
      multiplier: 2,
      maxAttempts: 4,
    });

    expect(schedule).toEqual([500, 1_000, 2_000, 4_000, null]);
  });

  it('DEFAULT_RECONNECT_BACKOFF is bounded and terminates', () => {
    const schedule = buildReconnectSchedule(DEFAULT_RECONNECT_BACKOFF);
    // Must terminate with null → prevents infinite reconnect loops.
    expect(schedule[schedule.length - 1]).toBeNull();
    // Every non-null entry is <= maxDelayMs.
    for (const entry of schedule) {
      if (entry === null) continue;
      expect(entry).toBeLessThanOrEqual(DEFAULT_RECONNECT_BACKOFF.maxDelayMs);
      expect(entry).toBeGreaterThanOrEqual(0);
    }
  });
});
