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
      observed: true,
      callCount: 1,
      totalTokensConsumed: 15,
      tokensRemaining: 85,
      calls: [{ gatewayRequestId: 'abc-1', route: 'failover', quota: 'allowed', cache: 'not-applicable', safety: 'passed', tokensConsumed: 15 }],
    });
    expect(e?.observed).toBe(true);
    expect(e?.calls[0]).toMatchObject({ route: 'failover', quota: 'allowed', cache: 'not-applicable', tokensConsumed: 15 });
  });

  it('turns malformed or unsafe values into explicit unknown/null', () => {
    const e = normalizeGatewayEvidence({
      observed: true,
      calls: [{ gatewayRequestId: 'sk secret!', route: 'bogus', quota: 'ok', cache: 7, safety: null, tokensConsumed: -3, gatewayLatencyMs: 'x' }, 'junk'],
    });
    expect(e?.calls).toHaveLength(1);
    expect(e?.calls[0]).toMatchObject({
      gatewayRequestId: null, route: 'unknown', quota: 'unknown', cache: 'unknown', safety: 'unknown', tokensConsumed: null, gatewayLatencyMs: null,
    });
  });

  it('is not observed when no calls carry evidence', () => {
    expect(normalizeGatewayEvidence({ observed: true, calls: [] })?.observed).toBe(false);
  });
});
