import type { GatewayCallEvidence, GatewayEvidence, GatewayEvidenceStatus } from '../types';

export type GatewayDisplayState =
  | 'observed'
  | 'unavailable'
  | 'bypassed'
  | 'blocked'
  | 'failed'
  | 'throttled'
  | 'truncated'
  | 'warning'
  | 'unknown';

export interface GatewayRibbonItem {
  key: 'route' | 'tokens' | 'budget' | 'cache' | 'safety' | 'gatewaySafety' | 'failover' | 'policy' | 'correlation';
  label: string;
  value: string;
  state: GatewayDisplayState | 'neutral';
}

export interface GatewayRibbonModel {
  state: GatewayDisplayState;
  statusLabel: string;
  items: GatewayRibbonItem[];
  truncatedNote?: string;
  /** Present only when a token budget throttled a call; drives the quota-state walkthrough. */
  quota?: GatewayQuotaState;
}

export interface GatewayQuotaState {
  scopeLabel: string;
  modeLabel: string;
  retryAfterSeconds: number | null;
  remaining: number | null;
  /** Ordered reset walkthrough; normal Retry-After first, demo-only generation reset second. */
  steps: string[];
}

const UNAVAILABLE = 'Unavailable';
const UNKNOWN = 'Unknown';

const STATUS_LABELS: Record<GatewayDisplayState, string> = {
  observed: 'Observed',
  unavailable: 'Metadata unavailable',
  bypassed: 'Gateway bypassed',
  blocked: 'Blocked',
  failed: 'Failed',
  throttled: 'Throttled',
  truncated: 'Truncated',
  warning: 'Safety warning',
  unknown: 'Unknown',
};

function distinct(values: (string | null | undefined)[]): string[] {
  return [...new Set(values.filter((v): v is string => !!v))];
}

function join(values: string[], missing: string): string {
  if (values.length === 0) return missing;
  return values.length === 1 ? values[0] : `${values[0]} +${values.length - 1}`;
}

function outcomeItem(
  key: 'cache' | 'safety',
  label: string,
  calls: GatewayCallEvidence[],
  observed: boolean,
  incomplete: boolean,
): GatewayRibbonItem {
  if (!observed) return { key, label, value: UNAVAILABLE, state: 'unavailable' };
  const vals = distinct(calls.map(c => c[key]));
  const known = vals.filter(v => v !== 'unknown' && v !== 'not-applicable');
  const partial = vals.includes('unknown') || incomplete;
  if (known.length === 0) {
    const na = vals.includes('not-applicable');
    return { key, label, value: na ? `Not applicable${partial ? ' (partial)' : ''}` : UNKNOWN, state: na && !partial ? 'observed' : 'unknown' };
  }
  if (key === 'safety' && known.includes('passed') && partial && !known.includes('blocked')) {
    return { key, label, value: 'Partial checks — not all calls reported', state: 'unknown' };
  }
  const state: GatewayDisplayState = known.includes('blocked') ? 'blocked' : partial ? 'unknown' : 'observed';
  const text = known.map(v => v.charAt(0).toUpperCase() + v.slice(1)).join(' / ');
  return { key, label, value: partial ? `${text} (partial)` : text, state };
}

/**
 * APIM-level content safety (#331). Only a fully verified allow (known phase, input and output
 * checked) is neutral. No-text output is explicitly not applicable, not a checked output.
 * Safety-error allows, unchecked text and missing detail are warnings/unknown, never success.
 * The coarse safety header comes from this same gateway, not application-level safety.
 */
function gatewaySafetyItem(calls: GatewayCallEvidence[], incomplete = false): GatewayRibbonItem {
  const key = 'gatewaySafety';
  const label = 'Gateway safety';
  const found = calls.map(c => c.gatewaySafety).filter((x): x is NonNullable<typeof x> => !!x);
  if (found.length === 0) {
    const disabled = calls.length > 0 && !incomplete && calls.every(c => c.safety === 'not-applicable');
    return { key, label, value: disabled ? 'Not applicable — gateway checks disabled' : 'Not reported', state: disabled ? 'neutral' : 'unknown' };
  }

  const blockedCall = calls.find(c => c.gatewaySafety?.decision === 'block' && c.statusCode !== 503)
    ?? calls.find(c => c.gatewaySafety?.decision === 'block');
  const blocked = blockedCall?.gatewaySafety;
  if (blocked) {
    if (blockedCall?.statusCode === 503) {
      return { key, label, value: `Unavailable (${blocked.phase}; request stopped)`, state: 'blocked' };
    }
    const detail = [
      blocked.phase,
      blocked.promptAttack ? 'prompt attack' : null,
      blocked.category ? `${blocked.category}${blocked.severity != null ? ` severity ${blocked.severity}` : ''}` : null,
    ].filter(Boolean).join(', ');
    return { key, label, value: `Blocked (${detail})`, state: 'blocked' };
  }
  const partial = found.length < calls.length || incomplete ? ' (partial: some calls not reported)' : '';
  if (found.some(f => f.decision === 'allowed_on_safety_error')) {
    return { key, label, value: `Allowed on safety error — not verified${partial}`, state: 'warning' };
  }
  const unchecked = found.filter(f => f.outputChecked === false && f.outputCheckReason !== 'no-text');
  if (unchecked.length > 0) {
    const reasons = distinct(unchecked.map(f => f.outputCheckReason === 'streaming'
      ? 'streaming' : f.outputCheckReason === 'backend-error' ? 'backend error'
        : f.outputCheckReason === 'unavailable' ? 'safety unavailable' : null));
    const reason = reasons.length ? ` (${reasons.join(' / ')})` : '';
    const checked = found.filter(f => f.outputChecked === true).length;
    return { key, label, value: `Allowed — output not checked${reason}${checked ? `; ${checked} other call${checked === 1 ? '' : 's'} checked` : ''}${partial}`, state: 'warning' };
  }
  if (found.some(f => f.outputChecked == null)) {
    return { key, label, value: `Allowed — output check unknown${partial}`, state: 'warning' };
  }
  if (partial) return { key, label, value: `Allowed${partial}`, state: 'warning' };
  const noText = found.filter(f => f.outputChecked === false && f.outputCheckReason === 'no-text').length;
  if (noText > 0) return {
    key, label,
    value: `Input checked; ${found.some(f => f.outputChecked === true) ? 'output text checked; ' : ''}${noText} call${noText === 1 ? '' : 's'} had no output text (tool arguments not checked)`,
    state: 'neutral',
  };
  return { key, label, value: 'Allowed — input and output checked', state: 'neutral' };
}

const SCOPE_LABELS = { store: 'Store', channel: 'Channel', application: 'Application', b2b: 'B2B account' } as const;

function budgetItem(calls: GatewayCallEvidence[]): GatewayRibbonItem | null {
  const call = [...calls].reverse().find(c => c.budget);
  const b = call?.budget;
  if (!b) return null;
  const parts = [`${SCOPE_LABELS[b.scopeKind]} scope`, b.mode === 'demo' ? 'Demo limit' : 'Normal limit'];
  const throttled = calls.some(c => c.quota === 'throttled');
  if (throttled && b.retryAfterSeconds != null) parts.push(`retry in ${b.retryAfterSeconds}s`);
  return { key: 'budget', label: 'Budget', value: parts.join(' · '), state: throttled ? 'throttled' : 'observed' };
}

function quotaState(calls: GatewayCallEvidence[]): GatewayQuotaState | undefined {
  const call = [...calls].reverse().find(c => c.quota === 'throttled' && c.budget);
  const b = call?.budget;
  if (!call || !b) return undefined;
  const wait = b.retryAfterSeconds != null ? `${b.retryAfterSeconds} seconds` : 'the Retry-After interval';
  const steps = [`Normal reset: wait ${wait}; the per-minute token window then admits calls again.`];
  if (b.mode === 'demo') {
    steps.push('Demo reset: an operator bumps the rp-budget-demo-generation named value for an immediate fresh counter (APIM has no clear-counter API), or disarms rp-budget-demo-armed.');
  }
  steps.push('Counters are gateway-local: another region or gateway resource keeps its own counter.');
  return {
    scopeLabel: SCOPE_LABELS[b.scopeKind],
    modeLabel: b.mode === 'demo' ? 'Demo limit' : 'Normal limit',
    retryAfterSeconds: b.retryAfterSeconds ?? null,
    remaining: call.tokensRemaining ?? null,
    steps,
  };
}

/** Pure mapping from the gateway contract to display state; never invents values. */
export function buildGatewayRibbon(gateway: GatewayEvidence | null | undefined): GatewayRibbonModel {
  const calls = gateway?.calls ?? [];
  const status: GatewayEvidenceStatus | 'missing' = gateway ? gateway.status : 'missing';
  const hasCalls = calls.length > 0;
  const observed = hasCalls && calls.some(c => c.status === 'observed' || c.status === 'blocked');
  const incomplete = Boolean(gateway?.truncated) || (gateway?.callCount ?? 0) > calls.length;

  let state: GatewayDisplayState;
  if (status === 'missing' || status === 'unavailable') state = 'unavailable';
  else if (status === 'bypassed') state = 'bypassed';
  else if (status === 'failed') state = 'failed';
  else if (status === 'blocked' || calls.some(c => c.status === 'blocked' || c.safety === 'blocked' || c.gatewaySafety?.decision === 'block')) state = 'blocked';
  else if (calls.some(c => c.quota === 'throttled')) state = 'throttled';
  else if (!hasCalls) state = 'unknown';
  else if (gatewaySafetyItem(calls, incomplete).state === 'warning') state = 'warning';
  else if (gateway?.truncated) state = 'truncated';
  else state = 'observed';

  const unavailableValue = state === 'bypassed' ? 'Bypassed' : UNAVAILABLE;
  const unavailableState: GatewayDisplayState = state === 'bypassed' ? 'bypassed' : 'unavailable';
  const placeholder = (key: GatewayRibbonItem['key'], label: string): GatewayRibbonItem => ({
    key, label, value: unavailableValue, state: unavailableState,
  });

  if (!observed && state !== 'failed' && state !== 'blocked') {
    return {
      state,
      statusLabel: STATUS_LABELS[state],
      items: [
        placeholder('route', 'Route'),
        placeholder('tokens', 'Tokens'),
        placeholder('cache', 'Cache'),
        placeholder('safety', 'Safety'),
        placeholder('gatewaySafety', 'Gateway safety'),
        placeholder('failover', 'Failover'),
        placeholder('policy', 'Policy'),
        placeholder('correlation', 'Request ID'),
      ],
    };
  }

  const backends = distinct(calls.map(c => c.backendAlias));
  const routes = distinct(calls.map(c => c.route));
  const failedOver = routes.includes('failover');
  const incompleteRoute = incomplete || routes.length === 0 || routes.includes('unknown');
  const poolTarget = hasCalls && calls.every(c => c.backendKind === 'pool');
  const routeLabel = (r: string) => (r === 'failover' ? 'Failover' : r === 'primary' ? 'Primary' : UNKNOWN);
  const demo = calls.map(c => c.failoverDemo).find(d => d);
  const demoText = demo
    ? ` (demo: primary ${demo.primaryStatusCode ?? 'unknown'}, fallback ${
        demo.fallbackUsed == null ? 'unknown' : demo.fallbackUsed ? 'used' : 'not used'})`
    : '';
  const throttled = calls.some(c => c.quota === 'throttled');

  const consumed = gateway?.totalTokensConsumed;
  const remaining = gateway?.tokensRemaining;
  const tokenParts: string[] = [];
  if (consumed != null) tokenParts.push(`${consumed.toLocaleString()} used`);
  if (remaining != null) tokenParts.push(`${remaining.toLocaleString()} left`);
  const tokenValue = tokenParts.length ? tokenParts.join(' · ') : UNAVAILABLE;

  const budget = budgetItem(calls);
  const ids = distinct(calls.map(c => c.gatewayRequestId));
  const policies = distinct(calls.map(c => c.policyVersion));
  const outcomeUnavailable = state === 'failed' && !hasCalls;
  const cache = outcomeUnavailable ? placeholder('cache', 'Cache') : outcomeItem('cache', 'Cache', calls, true, incomplete);
  const gatewaySafety = outcomeUnavailable ? placeholder('gatewaySafety', 'Gateway safety') : gatewaySafetyItem(calls, incomplete);
  let safety = outcomeUnavailable ? placeholder('safety', 'Safety') : outcomeItem('safety', 'Safety', calls, true, incomplete);
  if (gatewaySafety.state === 'blocked') {
    safety = { key: 'safety', label: 'Safety', value: gatewaySafety.value, state: 'blocked' };
  } else if (safety.state !== 'blocked' && gatewaySafety.state === 'warning') {
    safety = { key: 'safety', label: 'Safety', value: 'Not fully verified', state: 'warning' };
  } else if (safety.state !== 'blocked' && gatewaySafety.state === 'neutral'
    && calls.some(c => c.gatewaySafety?.outputCheckReason === 'no-text')) {
    safety = { key: 'safety', label: 'Safety', value: 'Text checks only', state: 'neutral' };
  }

  const items: GatewayRibbonItem[] = [
    {
      key: 'route',
      label: 'Route',
      value: poolTarget
        ? `${join(backends, UNKNOWN)} (Pool; selected backend not reported)`
        : failedOver
          ? backends.length ? `${join(backends, '')} (Failover)` : 'Failover'
          : incompleteRoute
            ? backends.length ? `${join(backends, '')} (Partial)` : UNKNOWN
            : backends.length ? `${join(backends, '')} (${routeLabel(routes[0])})` : routeLabel(routes[0]),
      state: failedOver ? 'observed' : incompleteRoute ? 'unknown' : 'observed',
    },
    {
      key: 'tokens',
      label: 'Tokens',
      value: throttled ? `Throttled · ${tokenValue}` : tokenValue,
      state: throttled ? 'throttled' : tokenParts.length ? 'observed' : 'unavailable',
    },
    ...(budget ? [budget] : []),
    cache,
    safety,
    gatewaySafety,
    {
      key: 'failover',
      label: 'Failover',
      value: (failedOver ? 'Failed over' : incompleteRoute ? UNKNOWN : 'None') + demoText,
      state: failedOver ? 'observed' : incompleteRoute ? 'unknown' : 'observed',
    },
    {
      key: 'policy',
      label: 'Policy',
      value: join(policies, UNAVAILABLE),
      state: policies.length ? 'observed' : 'unavailable',
    },
    {
      key: 'correlation',
      label: 'Request ID',
      value: join(ids, UNAVAILABLE),
      state: ids.length ? 'observed' : 'unavailable',
    },
  ];

  const hidden = gateway?.truncated
    ? `${gateway.droppedCalls > 0 ? `${gateway.droppedCalls} ` : 'Some '}gateway call${gateway.droppedCalls === 1 ? '' : 's'} not listed`
    : undefined;

  return { state, statusLabel: STATUS_LABELS[state], items, truncatedNote: hidden, quota: quotaState(calls) };
}
