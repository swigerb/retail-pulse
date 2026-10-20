import type { GatewayCallEvidence, GatewayEvidence, GatewayEvidenceStatus } from '../types';

export type GatewayDisplayState =
  | 'observed'
  | 'unavailable'
  | 'bypassed'
  | 'blocked'
  | 'failed'
  | 'throttled'
  | 'truncated'
  | 'unknown';

export interface GatewayRibbonItem {
  key: 'route' | 'tokens' | 'cache' | 'safety' | 'failover' | 'policy' | 'correlation';
  label: string;
  value: string;
  state: GatewayDisplayState;
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
    {
      key: 'failover',
      label: 'Failover',
      value: failedOver ? 'Failed over' : incompleteRoute ? UNKNOWN : 'None',
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
