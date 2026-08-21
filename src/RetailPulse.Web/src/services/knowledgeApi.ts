import type {
  KBDocument,
  KBSearchResult,
  KBStats,
  KnowledgeProviderSnapshot,
  KnowledgeQuotas,
  KnowledgeUploadResponse,
  KnowledgeUsage,
  SafetyBlockDisplayModel,
} from '../types';
import { buildSafetyBlockDisplay } from '../utils/safetyDisplay';

/**
 * Thrown by `uploadDocument` when the backend rejects a document for
 * content-safety-related reasons. Carries a fully whitelisted display model
 * so the caller can render the plain-language reason without ever seeing
 * the raw provider payload.
 */
export class KnowledgeUploadError extends Error {
  readonly display: SafetyBlockDisplayModel;
  readonly status: number;
  constructor(display: SafetyBlockDisplayModel, status: number) {
    super(display.reason);
    this.name = 'KnowledgeUploadError';
    this.display = display;
    this.status = status;
  }
}

/**
 * Thrown when the active knowledge provider rejects an ingestion for
 * provider-enforced quota reasons (document count, chunk count, or document
 * size). Carries the current quota / usage snapshot so the UI can render an
 * honest "quota reached" state without an extra round-trip.
 */
export class KnowledgeQuotaError extends Error {
  readonly quotas: KnowledgeQuotas | null;
  readonly usage: KnowledgeUsage | null;
  readonly status: number;
  constructor(reason: string, quotas: KnowledgeQuotas | null, usage: KnowledgeUsage | null, status: number) {
    super(reason);
    this.name = 'KnowledgeQuotaError';
    this.quotas = quotas;
    this.usage = usage;
    this.status = status;
  }
}

/**
 * Thrown when the active knowledge provider is read-only (its corpus is
 * managed outside Retail Pulse) and the user attempts an ingest. Carries the
 * verbatim provider-supplied reason so the panel can hide the upload
 * affordance and explain why.
 */
export class KnowledgeMutationUnsupportedError extends Error {
  readonly status: number;
  constructor(reason: string, status: number) {
    super(reason);
    this.name = 'KnowledgeMutationUnsupportedError';
    this.status = status;
  }
}

function isSafetyPayload(payload: unknown): payload is Record<string, unknown> {
  if (!payload || typeof payload !== 'object') return false;
  const p = payload as Record<string, unknown>;
  return (
    typeof p.contentSafetyRejected === 'boolean' ||
    typeof p.safetyBlocked === 'boolean' ||
    typeof p.category === 'string' ||
    typeof p.decision === 'string'
  );
}

function isQuotaPayload(payload: unknown): payload is Record<string, unknown> {
  return !!payload && typeof payload === 'object' && (payload as Record<string, unknown>).quotaRejected === true;
}

function isMutationUnsupportedPayload(payload: unknown): payload is Record<string, unknown> {
  return !!payload && typeof payload === 'object' && (payload as Record<string, unknown>).mutationUnsupported === true;
}

export async function fetchDocuments(): Promise<KBDocument[]> {
  const res = await fetch('/api/knowledge/documents');
  if (!res.ok) throw new Error(`Failed to fetch documents: ${res.status}`);
  return res.json();
}

export async function uploadDocument(
  file: File,
  title: string,
  source?: string,
): Promise<KnowledgeUploadResponse> {
  const content = await file.text();
  const res = await fetch('/api/knowledge/upload', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title, content, source: source ?? 'upload' }),
  });
  if (!res.ok) {
    let payload: unknown = null;
    try { payload = await res.json(); } catch { /* body may not be JSON */ }
    if (isSafetyPayload(payload)) {
      const p = payload as Record<string, unknown>;
      const display = buildSafetyBlockDisplay({
        stage: 'ingestion',
        decision: typeof p.decision === 'string' ? p.decision : 'Blocked',
        category: typeof p.category === 'string' ? p.category : undefined,
        severity: typeof p.severity === 'number' ? p.severity : undefined,
        detectionType: typeof p.detectionType === 'string' ? p.detectionType : undefined,
      });
      throw new KnowledgeUploadError(display, res.status);
    }
    if (isQuotaPayload(payload)) {
      const p = payload as Record<string, unknown>;
      const reason = typeof p.reason === 'string'
        ? p.reason
        : 'The knowledge provider quota was exceeded.';
      const quotas = (p.quotas && typeof p.quotas === 'object')
        ? (p.quotas as KnowledgeQuotas)
        : null;
      const usage = (p.usage && typeof p.usage === 'object')
        ? (p.usage as KnowledgeUsage)
        : null;
      throw new KnowledgeQuotaError(reason, quotas, usage, res.status);
    }
    if (isMutationUnsupportedPayload(payload)) {
      const p = payload as Record<string, unknown>;
      const reason = typeof p.reason === 'string'
        ? p.reason
        : 'The active knowledge provider is read-only.';
      throw new KnowledgeMutationUnsupportedError(reason, res.status);
    }
    throw new Error(`Failed to upload document: ${res.status}`);
  }
  return res.json();
}

export async function deleteDocument(id: string): Promise<void> {
  const res = await fetch(`/api/knowledge/documents/${id}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to delete document: ${res.status}`);
}

export async function searchKnowledgeBase(
  query: string,
  topK?: number,
): Promise<KBSearchResult[]> {
  const res = await fetch('/api/knowledge/search', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, topK }),
  });
  if (!res.ok) throw new Error(`Failed to search KB: ${res.status}`);
  const data: { query: string; results: KBSearchResult[] } = await res.json();
  return data.results;
}

export async function fetchKBStats(): Promise<KBStats> {
  const res = await fetch('/api/knowledge/stats');
  if (!res.ok) throw new Error(`Failed to fetch KB stats: ${res.status}`);
  return res.json();
}

/**
 * Fetches the active knowledge provider snapshot — capabilities (durable vs
 * volatile, relevance kind, quotas, score semantics), degradation state, the
 * named-source catalog, and every per-agent binding. Used by the Knowledge
 * panel to render honest provider visibility and the per-agent binding view.
 */
export async function fetchKnowledgeProviderSnapshot(): Promise<KnowledgeProviderSnapshot> {
  const res = await fetch('/api/knowledge/provider');
  if (!res.ok) throw new Error(`Failed to fetch knowledge provider snapshot: ${res.status}`);
  return res.json();
}
