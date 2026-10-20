import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { GatewayControlRoom } from '../components/GatewayControlRoom';
import { buildGatewayRibbon } from '../utils/gatewayRibbon';
import type { GatewayCallEvidence, GatewayEvidence } from '../types';

const call = (over: Partial<GatewayCallEvidence> = {}): GatewayCallEvidence => ({
  gatewayRequestId: 'req-123',
  backendAlias: 'gpt-primary',
  route: 'primary',
  tokensConsumed: 120,
  tokensRemaining: 880,
  quota: 'allowed',
  cache: 'miss',
  safety: 'passed',
  policyVersion: 'v7',
  status: 'observed',
  ...over,
});

const evidence = (over: Partial<GatewayEvidence> = {}, calls = [call()]): GatewayEvidence => ({
  status: 'observed',
  observed: true,
  truncated: false,
  droppedCalls: 0,
  callCount: calls.length,
  totalTokensConsumed: 120,
  tokensRemaining: 880,
  calls,
  ...over,
});

function renderRibbon(g: GatewayEvidence | null | undefined) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <GatewayControlRoom gateway={g} />
    </FluentProvider>,
  );
}
const item = (k: string) => screen.getByTestId(`gateway-item-${k}`);

describe('GatewayControlRoom', () => {
  it('renders observed evidence with all seven fields and an accessible label', () => {
    renderRibbon(evidence());
    const region = screen.getByRole('group', { name: 'Gateway Control Room: Observed' });
    expect(region).toHaveAttribute('data-gateway-state', 'observed');
    expect(item('route')).toHaveTextContent('gpt-primary (Primary)');
    expect(item('tokens')).toHaveTextContent('120 used · 880 left');
    expect(item('cache')).toHaveTextContent('Miss');
    expect(item('safety')).toHaveTextContent('Passed');
    expect(item('failover')).toHaveTextContent('None');
    expect(item('policy')).toHaveTextContent('v7');
    expect(item('correlation')).toHaveTextContent('req-123');
  });

  it('shows blocked state', () => {
    renderRibbon(evidence({ status: 'blocked' }, [call({ status: 'blocked', safety: 'blocked' })]));
    expect(screen.getByTestId('gateway-status')).toHaveTextContent('Blocked');
    expect(item('safety')).toHaveTextContent('Blocked');
    expect(item('safety')).toHaveAttribute('data-item-state', 'blocked');
  });

  it('shows throttled quota', () => {
    renderRibbon(evidence({ tokensRemaining: 0 }, [call({ quota: 'throttled', tokensRemaining: 0 })]));
    expect(screen.getByTestId('gateway-status')).toHaveTextContent('Throttled');
    expect(item('tokens')).toHaveTextContent('Throttled · 120 used · 0 left');
  });

  it('shows cache hit', () => {
    renderRibbon(evidence({}, [call({ cache: 'hit' })]));
    expect(item('cache')).toHaveTextContent('Hit');
  });

  it('shows failover', () => {
    renderRibbon(evidence({}, [call({ route: 'failover', backendAlias: 'gpt-secondary' })]));
    expect(item('route')).toHaveTextContent('gpt-secondary (Failover)');
    expect(item('failover')).toHaveTextContent('Failed over');
  });

  it('shows bypassed without fabricated values', () => {
    renderRibbon(evidence({ status: 'bypassed', observed: false, calls: [], callCount: 0, totalTokensConsumed: null, tokensRemaining: null }, []));
    expect(screen.getByTestId('gateway-status')).toHaveTextContent('Gateway bypassed');
    expect(item('route')).toHaveTextContent('Bypassed');
    expect(item('correlation')).not.toHaveTextContent('req-');
  });

  it('shows failed', () => {
    renderRibbon(evidence({ status: 'failed', observed: false, totalTokensConsumed: null, tokensRemaining: null }, [call({ status: 'failed', cache: 'unknown', safety: 'unknown', tokensConsumed: null, tokensRemaining: null, policyVersion: null })]));
    expect(screen.getByTestId('gateway-status')).toHaveTextContent('Failed');
    expect(item('cache')).toHaveTextContent('Unknown');
    expect(item('tokens')).toHaveTextContent('Unavailable');
  });

  it.each([[null], [undefined], [evidence({ status: 'unavailable', observed: false, calls: [], callCount: 0 }, [])]])(
    'shows metadata unavailable and no success values',
    g => {
      renderRibbon(g);
      expect(screen.getByTestId('gateway-status')).toHaveTextContent('Metadata unavailable');
      for (const k of ['route', 'tokens', 'cache', 'safety', 'failover', 'policy', 'correlation']) {
        expect(item(k)).toHaveTextContent('Unavailable');
      }
    },
  );

  it('flags truncated evidence', () => {
    renderRibbon(evidence({ truncated: true, droppedCalls: 2 }));
    expect(screen.getByTestId('gateway-status')).toHaveTextContent('Truncated');
    expect(screen.getByTestId('gateway-truncated')).toHaveTextContent('2 gateway calls not listed');
    expect(item('route')).toHaveTextContent('Partial');
    expect(item('route')).toHaveAttribute('data-item-state', 'unknown');
    expect(item('failover')).toHaveTextContent('Unknown');
  });

  it('marks unknown values as unknown, not success', () => {
    const m = buildGatewayRibbon(evidence({}, [call({ cache: 'unknown', safety: 'unknown', route: 'unknown', backendAlias: null, policyVersion: null, gatewayRequestId: null })]));
    const by = Object.fromEntries(m.items.map(i => [i.key, i]));
    expect(by.cache.state).toBe('unknown');
    expect(by.safety.state).toBe('unknown');
    expect(by.route.value).toBe('Unknown');
    expect(by.failover.value).toBe('Unknown');
    expect(by.correlation.value).toBe('Unavailable');
  });

  it('summarises multiple calls', () => {
    renderRibbon(evidence({}, [call(), call({ gatewayRequestId: 'req-456', backendAlias: 'gpt-fast' })]));
    expect(item('route')).toHaveTextContent('gpt-primary +1');
    expect(item('correlation')).toHaveTextContent('req-123 +1');
  });

  it('shows observed failover demo attempts without inventing missing ones', () => {
    renderRibbon(evidence({}, [call({
      route: 'failover',
      backendAlias: 'gpt-secondary',
      failoverDemo: { primaryStatusCode: 400, attempts: 2, fallbackUsed: true },
    })]));
    expect(item('failover')).toHaveTextContent('Failed over (demo: primary 400, fallback used)');
    expect(item('route')).toHaveTextContent('gpt-secondary (Failover)');
  });

  it('reports unknown demo fields as unknown, not success', () => {
    renderRibbon(evidence({}, [call({ failoverDemo: { primaryStatusCode: null, attempts: null, fallbackUsed: null } })]));
    expect(item('failover')).toHaveTextContent('None (demo: primary unknown, fallback unknown)');
  });

  it('does not claim a primary route or no failover when evidence is partial', () => {
    renderRibbon(evidence({}, [call(), call({ route: 'unknown', backendAlias: null })]));
    expect(item('route')).toHaveTextContent('gpt-primary (Partial)');
    expect(item('route')).toHaveAttribute('data-item-state', 'unknown');
    expect(item('failover')).toHaveTextContent('Unknown');
    expect(item('failover')).toHaveAttribute('data-item-state', 'unknown');
  });
});

describe('GatewayControlRoom gateway safety (#331)', () => {
  const safety = (over: Record<string, unknown> = {}) => ({
    phase: 'input', decision: 'allow', promptAttack: false, category: null, severity: null,
    mode: 'demo', latencyMs: 12, outputChecked: true, ...over,
  }) as GatewayCallEvidence['gatewaySafety'];
  const withSafety = (s: GatewayCallEvidence['gatewaySafety'], over: Partial<GatewayCallEvidence> = {}) =>
    evidence({}, [call({ gatewaySafety: s, ...over })]);

  it('renders a clear blocked state with sanitized detail and an accessible name', () => {
    renderRibbon(withSafety(
      safety({ decision: 'block', promptAttack: true, category: 'Violence', severity: 6, outputChecked: false }),
      { status: 'blocked', safety: 'blocked', statusCode: 403 },
    ));
    expect(screen.getByRole('group', { name: 'Gateway Control Room: Blocked' })).toBeInTheDocument();
    expect(item('gatewaySafety')).toHaveAttribute('data-item-state', 'blocked');
    expect(item('gatewaySafety')).toHaveTextContent('Blocked (input, prompt attack, Violence severity 6)');
    expect(screen.getByLabelText(/^Gateway safety: Blocked/)).toBeInTheDocument();
  });

  it('shows allowed_on_safety_error as a warning, never success', () => {
    renderRibbon(withSafety(safety({ decision: 'allowed_on_safety_error', outputChecked: false })));
    expect(item('gatewaySafety')).toHaveAttribute('data-item-state', 'warning');
    expect(item('gatewaySafety')).toHaveTextContent('Allowed on safety error — not verified');
    expect(screen.getByTestId('gateway-control-room')).toHaveAttribute('data-gateway-state', 'warning');
    expect(screen.getByTestId('gateway-status')).not.toHaveTextContent('Observed');
  });

  it('warns when streaming or input-only evidence left output unchecked', () => {
    renderRibbon(withSafety(safety({ outputChecked: false })));
    expect(item('gatewaySafety')).toHaveAttribute('data-item-state', 'warning');
    expect(item('gatewaySafety')).toHaveTextContent('Allowed — output not checked');
  });

  it('treats a valid allow with both phases checked as neutral', () => {
    renderRibbon(withSafety(safety()));
    expect(item('gatewaySafety')).toHaveAttribute('data-item-state', 'neutral');
    expect(item('gatewaySafety')).toHaveTextContent('Allowed — input and output checked');
    expect(screen.getByTestId('gateway-control-room')).toHaveAttribute('data-gateway-state', 'observed');
  });

  it('never presents unknown or partial evidence as success', () => {
    renderRibbon(withSafety(safety({ outputChecked: null })));
    expect(item('gatewaySafety')).toHaveAttribute('data-item-state', 'warning');
    expect(item('gatewaySafety')).toHaveTextContent('output check unknown');

    const model = buildGatewayRibbon(evidence({}, [call({ gatewaySafety: safety() }), call()]));
    const gs = model.items.find(i => i.key === 'gatewaySafety')!;
    expect(gs.state).toBe('warning');
    expect(gs.value).toContain('partial');

    const none = buildGatewayRibbon(evidence()).items.find(i => i.key === 'gatewaySafety')!;
    expect(none.state).toBe('unknown');
    expect(none.value).toBe('Not reported');
  });

  it('keeps the legacy safety item distinct from gateway safety', () => {
    renderRibbon(withSafety(safety()));
    expect(item('safety')).toHaveTextContent('Passed');
    expect(item('gatewaySafety')).not.toBe(item('safety'));
    expect(screen.getByText('Gateway safety:')).toBeInTheDocument();
  });
});
