import type {
  GatewayBudgetEvidence,
  GatewayBudgetMode,
  GatewayBudgetScopeKind,
  GatewayEvidenceStatus,
  GatewayCallEvidence,
  GatewayEvidence,
  GatewayOutcome,
  GatewayRouteState,
  GatewaySafetyDecision,
  GatewaySafetyEvidence,
  GatewaySafetyPhase,
} from '../types';

const OUTCOMES: readonly GatewayOutcome[] = [
  'unknown', 'not-applicable', 'allowed', 'throttled', 'hit', 'miss', 'passed', 'blocked',
  'bypass', 'unavailable',
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

function normalizeDemo(v: unknown): GatewayCallEvidence['failoverDemo'] {
  if (!v || typeof v !== 'object') return null;
  const d = v as Record<string, unknown>;
  return {
    primaryStatusCode: count(d.primaryStatusCode),
    attempts: count(d.attempts),
    fallbackUsed: typeof d.fallbackUsed === 'boolean' ? d.fallbackUsed : null,
  };
}

const HARM_CATEGORIES = ['Hate', 'SelfHarm', 'Sexual', 'Violence'];
const SAFETY_PHASES: readonly GatewaySafetyPhase[] = ['input', 'output'];
const SAFETY_DECISIONS: readonly GatewaySafetyDecision[] = ['allow', 'block', 'allowed_on_safety_error'];

function normalizeSafety(v: unknown): GatewaySafetyEvidence | null {
  if (!v || typeof v !== 'object') return null;
  const d = v as Record<string, unknown>;
  if (!SAFETY_PHASES.includes(d.phase as GatewaySafetyPhase)) return null;
  if (!SAFETY_DECISIONS.includes(d.decision as GatewaySafetyDecision)) return null;
  const sev = count(d.severity);
  return {
    phase: d.phase as GatewaySafetyPhase,
    decision: d.decision as GatewaySafetyDecision,
    promptAttack: typeof d.promptAttack === 'boolean' ? d.promptAttack : null,
    category: typeof d.category === 'string' && HARM_CATEGORIES.includes(d.category) ? d.category : null,
    severity: sev !== null && sev <= 7 ? sev : null,
    mode: d.mode === 'demo' || d.mode === 'regulated' ? d.mode : null,
    latencyMs: count(d.latencyMs),
    outputChecked: typeof d.outputChecked === 'boolean' ? d.outputChecked : null,
  };
}

const BUDGET_SCOPES: readonly GatewayBudgetScopeKind[] = ['store', 'channel', 'application', 'b2b'];
const BUDGET_MODES: readonly GatewayBudgetMode[] = ['normal', 'demo'];
const MAX_RETRY_AFTER_SECONDS = 86_400;

function normalizeBudget(v: unknown): GatewayBudgetEvidence | null {
  if (!v || typeof v !== 'object') return null;
  const d = v as Record<string, unknown>;
  if (!BUDGET_SCOPES.includes(d.scopeKind as GatewayBudgetScopeKind)) return null;
  if (!BUDGET_MODES.includes(d.mode as GatewayBudgetMode)) return null;
  const retry = count(d.retryAfterSeconds);
  return {
    scopeKind: d.scopeKind as GatewayBudgetScopeKind,
    mode: d.mode as GatewayBudgetMode,
    retryAfterSeconds: retry !== null && retry <= MAX_RETRY_AFTER_SECONDS ? retry : null,
  };
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
    failoverDemo: normalizeDemo(r.failoverDemo),
    gatewaySafety: normalizeSafety(r.gatewaySafety),
    budget: normalizeBudget(r.budget),
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
