import type {
  GatewayEvidenceStatus,
  GatewayCallEvidence,
  GatewayEvidence,
  GatewayOutcome,
  GatewayRouteState,
} from '../types';

const OUTCOMES: readonly GatewayOutcome[] = [
  'unknown', 'not-applicable', 'allowed', 'throttled', 'hit', 'miss', 'passed', 'blocked',
];
const ROUTES: readonly GatewayRouteState[] = ['unknown', 'primary', 'failover'];
const STATUSES: readonly GatewayEvidenceStatus[] = ['unavailable', 'observed', 'bypassed', 'blocked', 'failed'];
const TRANSPORT_ERRORS = ['timeout', 'canceled', 'network', 'other'];
const SAFE_TOKEN = /^[A-Za-z0-9._:-]{1,64}$/;

function outcome(v: unknown): GatewayOutcome {
  return typeof v === 'string' && (OUTCOMES as readonly string[]).includes(v)
    ? (v as GatewayOutcome)
    : 'unknown';
}

function status(v: unknown): GatewayEvidenceStatus {
  return typeof v === 'string' && (STATUSES as readonly string[]).includes(v)
    ? (v as GatewayEvidenceStatus)
    : 'unavailable';
}

function count(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) && v >= 0 ? v : null;
}

function token(v: unknown): string | null {
  return typeof v === 'string' && SAFE_TOKEN.test(v) ? v : null;
}

function normalizeCall(raw: unknown): GatewayCallEvidence | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  return {
    gatewayRequestId: token(r.gatewayRequestId),
    backendAlias: token(r.backendAlias),
    route:
      typeof r.route === 'string' && (ROUTES as readonly string[]).includes(r.route)
        ? (r.route as GatewayRouteState)
        : 'unknown',
    tokensConsumed: count(r.tokensConsumed),
    tokensRemaining: count(r.tokensRemaining),
    quota: outcome(r.quota),
    cache: outcome(r.cache),
    safety: outcome(r.safety),
    policyVersion: token(r.policyVersion),
    gatewayLatencyMs: count(r.gatewayLatencyMs),
    backendLatencyMs: count(r.backendLatencyMs),
    statusCode: count(r.statusCode),
    status: status(r.status),
    transportError:
      typeof r.transportError === 'string' && TRANSPORT_ERRORS.includes(r.transportError)
        ? r.transportError
        : null,
  };
}

/**
 * Validates untrusted gateway evidence from the API. Absent or malformed payloads
 * yield `undefined`/explicit unknown states, never fabricated success.
 */
export function normalizeGatewayEvidence(raw: unknown): GatewayEvidence | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const r = raw as Record<string, unknown>;
  const calls = Array.isArray(r.calls)
    ? r.calls.map(normalizeCall).filter((c): c is GatewayCallEvidence => c !== null)
    : [];
  const aggregate = status(r.status);
  return {
    status: aggregate,
    observed: aggregate === 'observed' && calls.length > 0,
    truncated: r.truncated === true,
    droppedCalls: count(r.droppedCalls) ?? 0,
    callCount: count(r.callCount) ?? calls.length,
    totalTokensConsumed: count(r.totalTokensConsumed),
    tokensRemaining: count(r.tokensRemaining),
    calls,
  };
}
