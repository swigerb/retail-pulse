import type { BlockedRequest, GuardrailsStats, GuardrailsConfigData } from '../types';

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
 * endpoint only returns aggregate counters — the per-entry fields required
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
  if (res.status === 404) return { jailbreakEnabled: false, piiEnabled: false, accessControlEnabled: false, blockedPatterns: '' };
  if (!res.ok) throw new Error(`Failed to fetch guardrails config: ${res.status}`);
  return res.json();
}

export async function updateGuardrailsConfig(config: GuardrailsConfigData): Promise<void> {
  const res = await fetch('/api/guardrails/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(config),
  });
  if (!res.ok) throw new Error(`Failed to update guardrails config: ${res.status}`);
}

export async function resetGuardrailsConfig(): Promise<GuardrailsConfigData> {
  const res = await fetch('/api/guardrails/config/reset', { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to reset guardrails config: ${res.status}`);
  return res.json();
}
