import { describe, expect, it } from 'vitest';
import { normalizeGatewayEvidence } from '../services/gatewayEvidence';

describe('normalizeGatewayEvidence', () => {
  it('returns undefined for absent or non-object input', () => {
    expect(normalizeGatewayEvidence(undefined)).toBeUndefined();
    expect(normalizeGatewayEvidence(null)).toBeUndefined();
    expect(normalizeGatewayEvidence('x')).toBeUndefined();
  });

  it('maps a valid payload', () => {
    const e = normalizeGatewayEvidence({
      status: 'observed',
      callCount: 1,
      totalTokensConsumed: 15,
      tokensRemaining: 85,
      calls: [{ gatewayRequestId: 'abc-1', route: 'failover', quota: 'allowed', cache: 'not-applicable', safety: 'passed', tokensConsumed: 15, status: 'observed' }],
    });
    expect(e?.observed).toBe(true);
    expect(e?.status).toBe('observed');
    expect(e?.calls[0]).toMatchObject({ route: 'failover', quota: 'allowed', cache: 'not-applicable', tokensConsumed: 15 });
  });

  it('turns malformed or unsafe values into explicit unknown/null', () => {
    const e = normalizeGatewayEvidence({
      status: 'observed',
      calls: [{ gatewayRequestId: 'sk secret!', route: 'bogus', quota: 'ok', cache: 7, safety: null, tokensConsumed: -3, gatewayLatencyMs: 'x' }, 'junk'],
    });
    expect(e?.calls).toHaveLength(1);
    expect(e?.calls[0]).toMatchObject({
      gatewayRequestId: null, route: 'unknown', quota: 'unknown', cache: 'unknown', safety: 'unknown', tokensConsumed: null, gatewayLatencyMs: null,
    });
  });

  it('is not observed when no calls carry evidence', () => {
    expect(normalizeGatewayEvidence({ status: 'observed', calls: [] })?.observed).toBe(false);
  });

  it.each(['unavailable', 'bypassed', 'blocked', 'failed'])('never treats %s as observed', (status) => {
    const e = normalizeGatewayEvidence({ status, observed: true, calls: [{ status }] });
    expect(e?.status).toBe(status);
    expect(e?.observed).toBe(false);
    expect(e?.calls[0].status).toBe(status);
  });

  it('defaults a missing or unknown status to unavailable, not observed', () => {
    const e = normalizeGatewayEvidence({ observed: true, calls: [{ status: 'great' }] });
    expect(e?.status).toBe('unavailable');
    expect(e?.observed).toBe(false);
    expect(e?.calls[0].status).toBe('unavailable');
  });

  it('keeps only fixed transport error classes and surfaces truncation', () => {
    const e = normalizeGatewayEvidence({
      status: 'failed',
      truncated: true,
      droppedCalls: 4,
      callCount: 36,
      calls: [
        { status: 'failed', transportError: 'timeout' },
        { status: 'failed', transportError: 'Connection refused to 10.0.0.1' },
      ],
    });
    expect(e?.truncated).toBe(true);
    expect(e?.droppedCalls).toBe(4);
    expect(e?.callCount).toBe(36);
    expect(e?.calls.map((c) => c.transportError)).toEqual(['timeout', null]);
  });
});
