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

  it('keeps only valid failover demo fields', () => {
    const e = normalizeGatewayEvidence({
      status: 'observed',
      calls: [{ route: 'failover', status: 'observed', failoverDemo: { primaryStatusCode: 400, attempts: 'x', fallbackUsed: 'yes' } }],
    });
    expect(e?.calls[0].failoverDemo).toEqual({ primaryStatusCode: 400, attempts: null, fallbackUsed: null });
    expect(normalizeGatewayEvidence({ status: 'observed', calls: [{ status: 'observed' }] })?.calls[0].failoverDemo).toBeNull();
  });

  it('keeps only valid gateway safety metadata', () => {
    const e = normalizeGatewayEvidence({
      status: 'blocked',
      calls: [{ status: 'blocked', gatewaySafety: { phase: 'input', decision: 'block', promptAttack: true, category: 'Violence', severity: 4, mode: 'regulated', latencyMs: 12, outputChecked: false, prompt: 'secret text' } }],
    });
    expect(e?.calls[0].gatewaySafety).toEqual({
      phase: 'input', decision: 'block', promptAttack: true, category: 'Violence', severity: 4, mode: 'regulated', latencyMs: 12, outputChecked: false,
    });
    expect(JSON.stringify(e)).not.toContain('secret text');
  });

  it('never fabricates safety evidence from unknown phase/decision or free-text category', () => {
    expect(normalizeGatewayEvidence({ status: 'observed', calls: [{ gatewaySafety: { phase: 'x', decision: 'allow' } }] })?.calls[0].gatewaySafety).toBeNull();
    const e = normalizeGatewayEvidence({ status: 'observed', calls: [{ gatewaySafety: { phase: 'output', decision: 'allowed_on_safety_error', category: 'free text', severity: 99, mode: 'x' } }] });
    expect(e?.calls[0].gatewaySafety).toMatchObject({ decision: 'allowed_on_safety_error', category: null, severity: null, mode: null, promptAttack: null });
    expect(normalizeGatewayEvidence({ status: 'observed', calls: [{}] })?.calls[0].gatewaySafety).toBeNull();
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

describe('gateway safety error propagation (#331)', () => {
  it('keeps sanitized safety evidence on a blocked 403 and drops spoofed fields', async () => {
    const { vi } = await import('vitest');
    const { sendMessage, GatewayChatError } = await import('../services/api');
    const body = {
      error: 'The request was blocked by gateway content safety.',
      code: 'gateway_safety_blocked',
      gateway: {
        status: 'blocked', callCount: 1, calls: [{
          route: 'unknown', quota: 'unknown', cache: 'unknown', safety: 'blocked', status: 'blocked', statusCode: 403,
          gatewaySafety: { phase: 'input', decision: 'block', promptAttack: true, category: 'Violence<script>', severity: 99, mode: 'evil', prompt: 'SECRET' },
        }],
      },
    };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status: 403, headers: { 'content-type': 'application/json' } })));
    const err = await sendMessage({ message: 'x', sessionId: 's' } as never).catch(e => e);
    vi.unstubAllGlobals();
    expect(err).toBeInstanceOf(GatewayChatError);
    expect(err.code).toBe('gateway_safety_blocked');
    const gs = err.gateway.calls[0].gatewaySafety;
    expect(gs).toMatchObject({ phase: 'input', decision: 'block', category: null, severity: null, mode: null });
    expect(JSON.stringify(err.gateway)).not.toContain('SECRET');
  });

  it('rejects unknown decisions instead of inventing safety evidence', () => {
    const g = normalizeGatewayEvidence({ status: 'observed', calls: [{ gatewaySafety: { phase: 'input', decision: 'mystery' } }] });
    expect(g?.calls[0]?.gatewaySafety).toBeNull();
  });
});
