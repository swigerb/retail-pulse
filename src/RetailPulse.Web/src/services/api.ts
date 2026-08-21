import type { ChatRequest, ChatResponse, RoutingInfo } from '../types';
import { resolveApiUrl } from '../config/apiOrigin';

async function parseErrorBody(res: Response): Promise<string> {
  const contentType = res.headers.get('content-type') ?? '';
  try {
    if (contentType.includes('application/json')) {
      const data = await res.json();
      if (typeof data === 'string') return data;
      if (data && typeof data === 'object') {
        const obj = data as Record<string, unknown>;
        const msg = obj.message ?? obj.error ?? obj.detail ?? obj.title;
        if (typeof msg === 'string' && msg.length > 0) return msg;
        return JSON.stringify(data);
      }
    } else {
      const text = await res.text();
      if (text) return text;
    }
  } catch {
    // ignore parse failures and fall back to status text
  }
  return res.statusText || 'Unknown error';
}

function isRoutingInfo(value: unknown): value is RoutingInfo {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  if (
    typeof v.agentKey !== 'string' ||
    typeof v.agentName !== 'string' ||
    typeof v.intent !== 'string' ||
    typeof v.confidence !== 'number'
  ) {
    return false;
  }
  // Hybrid execution fields (issue #95). Optional, but reject wrong-typed
  // values instead of silently coercing — the UI relies on the type to
  // decide when to render the path badge / forced indicator.
  if (v.executionPath !== undefined && v.executionPath !== null) {
    if (
      v.executionPath !== 'fast' &&
      v.executionPath !== 'plan' &&
      v.executionPath !== 'council'
    ) {
      return false;
    }
  }
  if (
    v.executionPathForced !== undefined &&
    v.executionPathForced !== null &&
    typeof v.executionPathForced !== 'boolean'
  ) {
    return false;
  }
  return true;
}

function isChatResponse(value: unknown): value is ChatResponse {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  if (
    typeof v.reply !== 'string' ||
    typeof v.sessionId !== 'string' ||
    !Array.isArray(v.spans)
  ) {
    return false;
  }
  // routing is optional but must be valid when present
  if (v.routing !== undefined && v.routing !== null && !isRoutingInfo(v.routing)) {
    return false;
  }
  return true;
}

export interface SendMessageOptions {
  signal?: AbortSignal;
  /**
   * Client-side timeout in milliseconds. If the request exceeds this duration
   * the fetch is aborted and an Error is thrown so the UI can clear its
   * loading state instead of spinning forever. Defaults to 180_000 (3 min) to
   * align with the backend Azure OpenAI network timeout.
   */
  timeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 180_000;

/**
 * Combines an optional caller signal with a timeout signal. Returns the
 * combined AbortSignal, a flag we can read to detect a timeout-driven abort,
 * and a cleanup callback that clears the timer.
 */
function withTimeout(
  signal: AbortSignal | undefined,
  timeoutMs: number,
): { signal: AbortSignal; didTimeOut: () => boolean; cleanup: () => void } {
  const controller = new AbortController();
  let timedOut = false;

  const onCallerAbort = () => controller.abort(signal?.reason);
  if (signal) {
    if (signal.aborted) {
      controller.abort(signal.reason);
    } else {
      signal.addEventListener('abort', onCallerAbort, { once: true });
    }
  }

  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort(new DOMException('Request timed out', 'TimeoutError'));
  }, timeoutMs);

  return {
    signal: controller.signal,
    didTimeOut: () => timedOut,
    cleanup: () => {
      clearTimeout(timer);
      signal?.removeEventListener('abort', onCallerAbort);
    },
  };
}

export async function sendMessage(
  request: ChatRequest,
  options: SendMessageOptions = {},
): Promise<ChatResponse> {
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const { signal, didTimeOut, cleanup } = withTimeout(options.signal, timeoutMs);

  let res: Response;
  try {
    // Chat can exceed the linked Static Web Apps backend timeout while a model
    // completes tool calls. Use the exact configured ACA origin when present;
    // local development retains the same-origin route.
    res = await fetch(resolveApiUrl('/api/chat'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
      signal,
    });
  } catch (err) {
    if (didTimeOut()) {
      throw new Error(
        `Request timed out after ${Math.round(timeoutMs / 1000)}s. The server may be busy — please try again.`,
      );
    }
    throw err;
  } finally {
    cleanup();
  }

  if (!res.ok) {
    const detail = await parseErrorBody(res);
    if (res.status === 429) {
      throw new Error('The AI service is busy. Please wait a moment and try again.');
    }
    throw new Error(`API error ${res.status}: ${detail}`);
  }

  const data: unknown = await res.json();
  if (!isChatResponse(data)) {
    throw new Error('API error: malformed response payload');
  }
  return data;
}

/**
 * Returns true when the reply text indicates a backend error wrapped in a
 * 200 OK response (e.g. rate-limit or timeout caught by the pipeline).
 * When this returns true the frontend should suppress routing/telemetry
 * metadata so the user doesn't see misleading "Agent X — 78% confidence".
 */
export function isErrorReply(reply: string): boolean {
  return reply.startsWith('⏳');
}
