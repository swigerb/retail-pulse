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
  key: 'route' | 'tokens' | 'cache' | 'safety' | 'gatewaySafety' | 'failover' | 'policy' | 'correlation';
  label: string;
  value: string;
  state: GatewayDisplayState | 'neutral';
}

export interface GatewayRibbonModel {
  state: GatewayDisplayState;
  statusLabel: string;
  items: GatewayRibbonItem[];
  truncatedNote?: string;
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
): GatewayRibbonItem {
  if (!observed) return { key, label, value: UNAVAILABLE, state: 'unavailable' };
  const vals = distinct(calls.map(c => c[key]));
  const known = vals.filter(v => v !== 'unknown' && v !== 'not-applicable');
  if (known.length === 0) {
    const na = vals.includes('not-applicable');
    return { key, label, value: na ? 'Not applicable' : UNKNOWN, state: na ? 'observed' : 'unknown' };
  }
  const state: GatewayDisplayState = known.includes('blocked') ? 'blocked' : 'observed';
  const text = known.map(v => v.charAt(0).toUpperCase() + v.slice(1)).join(' / ');
  return { key, label, value: vals.includes('unknown') ? `${text} (partial)` : text, state };
}

/**
 * APIM-level content safety (#331). Only a fully verified allow (known phase, input and output
 * checked) is neutral; blocks are blocked; safety-error allows, unchecked output and partial or
 * missing detail are warnings/unknown and never success. Distinct from the legacy application safety item.
 */
function gatewaySafetyItem(calls: GatewayCallEvidence[]): GatewayRibbonItem {
  const key = 'gatewaySafety';
  const label = 'Gateway safety';
  const found = calls.map(c => c.gatewaySafety).filter((x): x is NonNullable<typeof x> => !!x);
  if (found.length === 0) return { key, label, value: 'Not reported', state: 'unknown' };

  const blocked = found.find(f => f.decision === 'block');
  if (blocked) {
    const detail = [
      blocked.phase,
      blocked.promptAttack ? 'prompt attack' : null,
      blocked.category ? `${blocked.category}${blocked.severity != null ? ` severity ${blocked.severity}` : ''}` : null,
    ].filter(Boolean).join(', ');
    return { key, label, value: `Blocked (${detail})`, state: 'blocked' };
  }
  const partial = found.length < calls.length ? ' (partial: some calls not reported)' : '';
  if (found.some(f => f.decision === 'allowed_on_safety_error')) {
    return { key, label, value: `Allowed on safety error — not verified${partial}`, state: 'warning' };
  }
  if (found.some(f => f.outputChecked === false)) {
    return { key, label, value: `Allowed — output not checked${partial}`, state: 'warning' };
  }
  if (found.some(f => f.outputChecked == null)) {
    return { key, label, value: `Allowed — output check unknown${partial}`, state: 'warning' };
  }
  if (partial) return { key, label, value: `Allowed${partial}`, state: 'warning' };
  return { key, label, value: 'Allowed — input and output checked', state: 'neutral' };
}

/** Pure mapping from the gateway contract to display state; never invents values. */
export function buildGatewayRibbon(gateway: GatewayEvidence | null | undefined): GatewayRibbonModel {
  const calls = gateway?.calls ?? [];
  const status: GatewayEvidenceStatus | 'missing' = gateway ? gateway.status : 'missing';
  const hasCalls = calls.length > 0;
  const observed = status !== 'missing' && status !== 'unavailable' && status !== 'bypassed' && hasCalls;

  let state: GatewayDisplayState;
  if (status === 'missing' || status === 'unavailable') state = 'unavailable';
  else if (status === 'bypassed') state = 'bypassed';
  else if (status === 'failed') state = 'failed';
  else if (status === 'blocked' || calls.some(c => c.status === 'blocked' || c.safety === 'blocked')) state = 'blocked';
  else if (calls.some(c => c.quota === 'throttled')) state = 'throttled';
  else if (!hasCalls) state = 'unknown';
  else if (gatewaySafetyItem(calls).state === 'warning') state = 'warning';
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
  const incompleteRoute = Boolean(gateway?.truncated) || routes.length === 0 || routes.includes('unknown');
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

  const ids = distinct(calls.map(c => c.gatewayRequestId));
  const policies = distinct(calls.map(c => c.policyVersion));
  const outcomeUnavailable = state === 'failed' && !hasCalls;
  const cache = outcomeUnavailable ? placeholder('cache', 'Cache') : outcomeItem('cache', 'Cache', calls, true);
  const safety = outcomeUnavailable ? placeholder('safety', 'Safety') : outcomeItem('safety', 'Safety', calls, true);

  const items: GatewayRibbonItem[] = [
    {
      key: 'route',
      label: 'Route',
      value: failedOver
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
    cache,
    safety,
    outcomeUnavailable ? placeholder('gatewaySafety', 'Gateway safety') : gatewaySafetyItem(calls),
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

  return { state, statusLabel: STATUS_LABELS[state], items, truncatedNote: hidden };
}
