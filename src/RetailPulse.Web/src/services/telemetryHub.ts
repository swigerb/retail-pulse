import * as signalR from '@microsoft/signalr';
import type { AgentSpan } from '../types';
import { resolveTelemetryHubUrl } from '../config/telemetryHubUrl';
import { getHubAccessToken } from '../auth/tokenService';

const HUB_URL = resolveTelemetryHubUrl();

let connection: signalR.HubConnection | null = null;
let startPromise: Promise<void> | null = null;
const joinedSessions = new Set<string>();
const progressListeners = new Set<(data: ProgressEvent) => void>();

export interface ProgressEvent {
  sessionId: string;
  phase: string;
  detail: string;
  timestamp: string;
}

// ── Multi-listener hub event dispatch (issue #96) ──────────────────────────
// SignalR's `.on()` supports multiple handlers, but existing callers rely on
// `off()`+`on()` to avoid duplicates on re-renders. Rather than reshape those
// call sites, we register one managed dispatcher per event and let new
// consumers subscribe against our own listener set. Unsubscribe is safe to
// call after connection teardown.
type HubEventHandler = (payload: unknown) => void;
const eventListeners = new Map<string, Set<HubEventHandler>>();
const managedEvents = new Set<string>();

function attachManagedHandler(event: string): void {
  if (managedEvents.has(event)) {
    // Already tracked; only attach the dispatcher if a connection has
    // materialised since the last register.
    if (connection) {
      connection.off(event);
      connection.on(event, (payload: unknown) => dispatchManaged(event, payload));
    }
    return;
  }
  managedEvents.add(event);
  if (connection) {
    connection.on(event, (payload: unknown) => dispatchManaged(event, payload));
  }
}

function dispatchManaged(event: string, payload: unknown): void {
  const listeners = eventListeners.get(event);
  if (!listeners) return;
  for (const handler of listeners) {
    try {
      handler(payload);
    } catch (err) {
      if (import.meta.env.DEV) console.error(`Hub handler for ${event} threw:`, err);
    }
  }
}

export function subscribeHubEvent(
  event: string,
  handler: HubEventHandler,
): () => void {
  let listeners = eventListeners.get(event);
  if (!listeners) {
    listeners = new Set();
    eventListeners.set(event, listeners);
  }
  listeners.add(handler);
  attachManagedHandler(event);
  return () => {
    const set = eventListeners.get(event);
    if (!set) return;
    set.delete(handler);
    if (set.size === 0) {
      eventListeners.delete(event);
    }
  };
}

function reattachManagedHandlers(): void {
  if (!connection) return;
  for (const event of managedEvents) {
    connection.off(event);
    connection.on(event, (payload: unknown) => dispatchManaged(event, payload));
  }
}

export function connectTelemetryHub(
  onSpan: (span: AgentSpan) => void,
  onConnected?: () => void,
  onDisconnected?: () => void,
): signalR.HubConnection {
  // If we already have a connection that isn't disconnected, reuse it
  if (connection && connection.state !== signalR.HubConnectionState.Disconnected) {
    // Re-register callbacks for the new React render cycle
    connection.off('SpanReceived');
    connection.on('SpanReceived', (span: AgentSpan) => onSpan(span));
    reattachManagedHandlers();
    if (connection.state === signalR.HubConnectionState.Connected) {
      onConnected?.();
    }
    return connection;
  }

  connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL, {
      // Bearer token for the hub handshake. WebSocket handshakes can't set an
      // Authorization header, so SignalR appends the returned token as ?access_token=,
      // which the API honours ONLY for /hubs. Returns '' locally (no token needed).
      accessTokenFactory: getHubAccessToken,
    })
    .withAutomaticReconnect()
    .build();

  connection.on('SpanReceived', (span: AgentSpan) => {
    onSpan(span);
  });

  connection.on('progress', (data: ProgressEvent) => {
    progressListeners.forEach(listener => listener(data));
  });

  reattachManagedHandlers();

  connection.on('Connected', (msg: string) => {
    if (import.meta.env.DEV) {
      console.log('Telemetry:', msg);
    }
  });

  // Re-join any sessions on reconnect — SignalR groups don't survive reconnects.
  connection.onreconnected(() => {
    onConnected?.();
    joinPendingSessions();
  });
  connection.onclose(() => onDisconnected?.());

  startPromise = connection.start()
    .then(() => {
      onConnected?.();
      joinPendingSessions();
    })
    .catch(err => {
      if (import.meta.env.DEV) {
        console.error('SignalR connection error:', err);
      }
      onDisconnected?.();
    });

  return connection;
}

/**
 * Joins all queued sessions on the current connection.
 * Called after initial connect and on every reconnect.
 */
function joinPendingSessions(): void {
  joinedSessions.forEach(sid => {
    connection?.invoke('JoinSession', sid).catch(err => {
      if (import.meta.env.DEV) console.error('JoinSession failed:', err);
    });
  });
}

/**
 * Joins the SignalR group for a given session so the hub will route
 * session-scoped spans here. Safe to call before connection is ready —
 * the session will be joined once the connection starts.
 */
export async function joinTelemetrySession(sessionId: string): Promise<void> {
  if (!sessionId) return;
  if (joinedSessions.has(sessionId)) return;
  joinedSessions.add(sessionId);
  try {
    if (connection?.state === signalR.HubConnectionState.Connected) {
      await connection.invoke('JoinSession', sessionId);
    }
    // If not yet connected, joinPendingSessions() will pick it up after start/reconnect.
  } catch (err) {
    if (import.meta.env.DEV) console.error('JoinSession failed:', err);
  }
}

/**
 * Subscribe to real-time progress events from the server.
 * Returns an unsubscribe function.
 */
export function onProgress(listener: (data: ProgressEvent) => void): () => void {
  progressListeners.add(listener);
  return () => { progressListeners.delete(listener); };
}

export async function disconnectTelemetryHub(): Promise<void> {
  if (connection) {
    try {
      // Wait for any in-flight start to finish before stopping
      if (startPromise) {
        await startPromise.catch(() => {});
        startPromise = null;
      }
      await connection.stop();
    } catch (err) {
      if (import.meta.env.DEV) console.error('SignalR disconnect failed:', err);
    }
  }
  connection = null;
  joinedSessions.clear();
}
