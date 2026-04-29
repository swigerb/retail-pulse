import type { ChatRequest, ChatResponse } from '../types';

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

function isChatResponse(value: unknown): value is ChatResponse {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  return (
    typeof v.reply === 'string' &&
    typeof v.sessionId === 'string' &&
    Array.isArray(v.spans)
  );
}

export interface SendMessageOptions {
  signal?: AbortSignal;
}

export async function sendMessage(
  request: ChatRequest,
  options: SendMessageOptions = {},
): Promise<ChatResponse> {
  const res = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal: options.signal,
  });

  if (!res.ok) {
    const detail = await parseErrorBody(res);
    throw new Error(`API error ${res.status}: ${detail}`);
  }

  const data: unknown = await res.json();
  if (!isChatResponse(data)) {
    throw new Error('API error: malformed response payload');
  }
  return data;
}
