import type { GuardrailsStats, GuardrailsConfigData } from '../types';

export async function fetchGuardrailsStats(): Promise<GuardrailsStats> {
  const res = await fetch('/api/guardrails/stats');
  if (!res.ok) throw new Error(`Failed to fetch guardrails stats: ${res.status}`);
  return res.json();
}

export async function fetchGuardrailsConfig(): Promise<GuardrailsConfigData> {
  const res = await fetch('/api/guardrails/config');
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
