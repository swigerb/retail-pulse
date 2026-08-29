import type { BlockedRequest, GuardrailsStats, GuardrailsConfigData } from '../types';

type GuardrailsConfigUpdatePayload = Pick<
  GuardrailsConfigData,
  'piiDetectionEnabled' | 'jailbreakDetectionEnabled' | 'autoRedactPii' | 'maxInputLength'
> & {
  contentSafety?: {
    failPolicy: NonNullable<GuardrailsConfigData['contentSafety']>['failPolicy'];
    hateThreshold: number;
    sexualThreshold: number;
    violenceThreshold: number;
    selfHarmThreshold: number;
  };
};

export async function fetchGuardrailsStats(): Promise<GuardrailsStats> {
  const res = await fetch('/api/guardrails/stats');
  if (!res.ok) throw new Error(`Failed to fetch guardrails stats: ${res.status}`);
  const data = await res.json();
  return {
    ...data,
    recentBlocked: data.recentBlocked ?? [],
    blocksPerHour: data.blocksPerHour ?? [],
  };
}

/**
 * Fetches the raw suspicious-request audit log so the dashboard can render
 * category / severity / decision per entry. The `/api/guardrails/stats`
 * endpoint only returns aggregate counters - the per-entry fields required
 * for the pattern-vs-model split live behind `/api/guardrails/log`.
 */
export async function fetchGuardrailsLog(count = 50): Promise<BlockedRequest[]> {
  const res = await fetch(`/api/guardrails/log?count=${encodeURIComponent(count)}`);
  if (!res.ok) throw new Error(`Failed to fetch guardrails log: ${res.status}`);
  const data: unknown = await res.json();
  if (!Array.isArray(data)) return [];
  return data
    .filter((entry): entry is Record<string, unknown> => typeof entry === 'object' && entry !== null)
    .map(entry => ({
      id: String(entry.id ?? ''),
      timestamp: String(entry.timestamp ?? ''),
      // Backend uses `requestText`; older mocks may still emit `requestPreview`.
      requestPreview: String(entry.requestText ?? entry.requestPreview ?? ''),
      detectionType: String(entry.detectionType ?? '') as BlockedRequest['detectionType'],
      reason: String(entry.reason ?? ''),
      actionTaken: String(entry.action ?? entry.actionTaken ?? ''),
      category: typeof entry.category === 'string' ? entry.category : undefined,
      severity: typeof entry.severity === 'number' ? entry.severity : undefined,
      decision: typeof entry.decision === 'string' ? entry.decision : undefined,
    }));
}

export async function fetchGuardrailsConfig(): Promise<GuardrailsConfigData> {
  const res = await fetch('/api/guardrails/config');
  if (res.status === 404) return defaultGuardrailsConfig();
  if (!res.ok) throw new Error(`Failed to fetch guardrails config: ${res.status}`);
  return res.json();
}

export async function updateGuardrailsConfig(config: GuardrailsConfigData): Promise<GuardrailsConfigData> {
  const body = toUpdatePayload(config);
  const res = await fetch('/api/guardrails/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`Failed to update guardrails config: ${res.status}`);
  const saved = await res.json() as GuardrailsConfigData;
  assertAppliedConfig(body, saved);
  return saved;
}

export async function resetGuardrailsConfig(): Promise<GuardrailsConfigData> {
  const res = await fetch('/api/guardrails/config/reset', { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to reset guardrails config: ${res.status}`);
  return res.json();
}

function defaultGuardrailsConfig(): GuardrailsConfigData {
  return {
    piiDetectionEnabled: false,
    jailbreakDetectionEnabled: false,
    autoRedactPii: false,
    maxInputLength: 0,
    piiPatterns: [],
    jailbreakPatterns: [],
  };
}

function toUpdatePayload(config: GuardrailsConfigData): GuardrailsConfigUpdatePayload {
  const payload: GuardrailsConfigUpdatePayload = {
    piiDetectionEnabled: config.piiDetectionEnabled,
    jailbreakDetectionEnabled: config.jailbreakDetectionEnabled,
    autoRedactPii: config.autoRedactPii,
    maxInputLength: config.maxInputLength,
  };
  if (config.contentSafety) {
    payload.contentSafety = {
      failPolicy: config.contentSafety.failPolicy,
      hateThreshold: config.contentSafety.hateThreshold,
      sexualThreshold: config.contentSafety.sexualThreshold,
      violenceThreshold: config.contentSafety.violenceThreshold,
      selfHarmThreshold: config.contentSafety.selfHarmThreshold,
    };
  }
  return payload;
}

function assertAppliedConfig(requested: GuardrailsConfigUpdatePayload, saved: GuardrailsConfigData): void {
  const expected: Array<[keyof GuardrailsConfigUpdatePayload, unknown]> = [
    ['piiDetectionEnabled', requested.piiDetectionEnabled],
    ['jailbreakDetectionEnabled', requested.jailbreakDetectionEnabled],
    ['autoRedactPii', requested.autoRedactPii],
    ['maxInputLength', requested.maxInputLength],
  ];
  const mismatch = expected.find(([key, value]) => saved[key] !== value);
  if (mismatch) {
    throw new Error(`Guardrails config update was not applied for ${String(mismatch[0])}`);
  }
  if (requested.contentSafety && !saved.contentSafety) {
    throw new Error('Guardrails config update was not applied for contentSafety');
  }
  if (requested.contentSafety && saved.contentSafety) {
    const contentSafetyMismatch = Object.entries(requested.contentSafety)
      .find(([key, value]) => saved.contentSafety?.[key as keyof typeof requested.contentSafety] !== value);
    if (contentSafetyMismatch) {
      throw new Error(`Guardrails config update was not applied for contentSafety.${contentSafetyMismatch[0]}`);
    }
  }
}
