import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { GatewayControlRoom } from '../components/GatewayControlRoom';
import { buildGatewayRibbon } from '../utils/gatewayRibbon';
import { normalizeGatewayEvidence } from '../services/gatewayEvidence';

// A pre-backend 429: the gateway reports throttled, remaining and retry, but no consumed tokens.
const throttledPayload = (mode: string) => ({
  status: 'observed',
  callCount: 1,
  totalTokensConsumed: null,
  tokensRemaining: 0,
  calls: [{
    route: 'unknown', tokensConsumed: null, tokensRemaining: 0, quota: 'throttled', cache: 'unknown',
    safety: 'unknown', status: 'observed', statusCode: 429,
    budget: { scopeKind: 'store', mode, retryAfterSeconds: 42 },
  }],
});

function renderRoom(raw: unknown) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <GatewayControlRoom gateway={normalizeGatewayEvidence(raw)} />
    </FluentProvider>,
  );
}

describe('token budget evidence', () => {
  it('normalizes only the fixed scope/mode vocabulary and bounded numeric retry', () => {
    const g = normalizeGatewayEvidence({
      status: 'observed',
      calls: [
        { status: 'observed', budget: { scopeKind: 'b2b', mode: 'normal', retryAfterSeconds: 9999999 } },
        { status: 'observed', budget: { scopeKind: 'sub-123', mode: 'demo', retryAfterSeconds: 5 } },
        { status: 'observed', budget: { scopeKind: 'store', mode: 'demo', retryAfterSeconds: '5s' } },
      ],
    })!;
    expect(g.calls[0].budget).toEqual({ scopeKind: 'b2b', mode: 'normal', retryAfterSeconds: null });
    expect(g.calls[1].budget).toBeNull();
    expect(g.calls[2].budget).toEqual({ scopeKind: 'store', mode: 'demo', retryAfterSeconds: null });
  });

  it('never fabricates consumed tokens on a pre-backend 429', () => {
    const model = buildGatewayRibbon(normalizeGatewayEvidence(throttledPayload('demo')));
    const tokens = model.items.find(i => i.key === 'tokens')!;
    expect(tokens.value).not.toMatch(/used/);
    expect(tokens.value).toContain('0 left');
  });

  it('renders the demo quota state with retry, remaining and both reset paths', () => {
    renderRoom(throttledPayload('demo'));
    expect(screen.getByTestId('gateway-status')).toHaveTextContent('Throttled');
    const quota = screen.getByTestId('gateway-quota-state');
    expect(quota).toHaveTextContent('Token budget exhausted — Store scope · Demo limit');
    expect(screen.getByTestId('gateway-quota-numbers')).toHaveTextContent('Retry after: 42s');
    expect(screen.getByTestId('gateway-quota-numbers')).toHaveTextContent('Remaining: 0');
    expect(quota).toHaveTextContent('wait 42 seconds');
    expect(quota).toHaveTextContent('rp-budget-demo-generation');
    expect(quota).toHaveTextContent('gateway-local');
    expect(screen.getByTestId('gateway-item-budget')).toHaveTextContent('Store scope · Demo limit · retry in 42s');
  });

  it('omits the demo reset step for the normal limit and shows no quota state when allowed', () => {
    const { unmount } = renderRoom(throttledPayload('normal'));
    expect(screen.getByTestId('gateway-quota-state')).not.toHaveTextContent('rp-budget-demo-generation');
    unmount();
    renderRoom({
      status: 'observed',
      calls: [{ status: 'observed', quota: 'allowed', tokensConsumed: 10, tokensRemaining: 90,
        budget: { scopeKind: 'channel', mode: 'normal', retryAfterSeconds: null } }],
    });
    expect(screen.queryByTestId('gateway-quota-state')).toBeNull();
    expect(screen.getByTestId('gateway-item-budget')).toHaveTextContent('Channel scope · Normal limit');
  });
});

describe('429 preservation through the API client', () => {
  it('keeps the budget code and evidence instead of the generic busy message', async () => {
    const { vi } = await import('vitest');
    const { sendMessage, GatewayChatError } = await import('../services/api');
    const body = { error: 'budget', code: 'gateway_token_budget_exceeded', retryAfterSeconds: 42, gateway: throttledPayload('demo') };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status: 429, headers: { 'content-type': 'application/json', 'retry-after': '42' } })));
    const err = await sendMessage({ message: 'x', sessionId: 's' } as never).catch(e => e);
    vi.unstubAllGlobals();
    expect(err).toBeInstanceOf(GatewayChatError);
    expect(err.status).toBe(429);
    expect(err.code).toBe('gateway_token_budget_exceeded');
    expect(err.gateway.calls[0].budget).toMatchObject({ scopeKind: 'store', mode: 'demo', retryAfterSeconds: 42 });
  });

  it('keeps the generic busy message for a 429 without budget evidence', async () => {
    const { vi } = await import('vitest');
    const { sendMessage } = await import('../services/api');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'rate_limited' }), { status: 429, headers: { 'content-type': 'application/json' } })));
    const err = await sendMessage({ message: 'x', sessionId: 's' } as never).catch(e => e);
    vi.unstubAllGlobals();
    expect(err.message).toContain('busy');
  });
});
