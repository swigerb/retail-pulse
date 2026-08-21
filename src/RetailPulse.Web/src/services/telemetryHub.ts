import * as signalR from '@microsoft/signalr';
import type { AgentSpan } from '../types';
import { resolveTelemetryHubUrl } from '../config/telemetryHubUrl';
import { getHubAccessToken } from '../auth/tokenService';
import {
  computeReconnectDelayMs,
  DEFAULT_RECONNECT_BACKOFF,
  type ReconnectBackoffOptions,
} from './reconnectBackoff';

const HUB_URL = resolveTelemetryHubUrl();

let connection: signalR.HubConnection | null = null;
let startPromise: Promise<void> | null = null;
const joinedSessions = new Set<string>();
const progressListeners = new Set<(data: ProgressEvent) => void>();

/**
 * Application-level connection status surfaced to the UI (issue #92).
 *
 * - `connecting`: initial handshake in flight (or a manual restart).
 * - `connected`: hub is up and application-level events are flowing.
 * - `reconnecting`: transport dropped; SignalR is retrying per the backoff
 *   schedule in {@link reconnectBackoff}.
 * - `disconnected`: retry budget exhausted OR the app explicitly stopped
 *   the hub. Terminal state — the user must retry (or reload) to recover.
 */
export type HubConnectionStatus =
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'disconnected';

export interface HubHeartbeatEvent {
  readonly timestamp: string;
  readonly intervalMs?: number;
}

let currentStatus: HubConnectionStatus = 'disconnected';
let lastHeartbeatAt: number | null = null;
const statusListeners = new Set<(status: HubConnectionStatus) => void>();
const heartbeatListeners = new Set<(event: HubHeartbeatEvent) => void>();

function setStatus(next: HubConnectionStatus): void {
  if (currentStatus === next) return;
  currentStatus = next;
  statusListeners.forEach((listener) => {
    try {
      listener(next);
    } catch (err) {
      if (import.meta.env.DEV) console.error('Hub status listener threw:', err);
    }
  });
}

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

/**
 * SignalR retry policy backed by the shared exponential backoff schedule
 * (issue #92). Returning `null` transitions the connection to Disconnected,
 * which fires `onclose` and lets the UI render a terminal "disconnected"
 * state.
 */
class ExponentialReconnectPolicy implements signalR.IRetryPolicy {
  private readonly options: ReconnectBackoffOptions;

  constructor(options: ReconnectBackoffOptions) {
    this.options = options;
  }

  nextRetryDelayInMilliseconds(retryContext: signalR.RetryContext): number | null {
    return computeReconnectDelayMs(retryContext.previousRetryCount, this.options);
  }
}

export function connectTelemetryHub(
  onSpan: (span: AgentSpan) => void,
  onConnected?: () => void,
  onDisconnected?: () => void,
  backoffOptions: ReconnectBackoffOptions = DEFAULT_RECONNECT_BACKOFF,
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

  setStatus('connecting');

  connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL, {
      // Bearer token for the hub handshake. WebSocket handshakes can't set an
      // Authorization header, so SignalR appends the returned token as ?access_token=,
      // which the API honours ONLY for /hubs. Returns '' locally (no token needed).
      accessTokenFactory: getHubAccessToken,
    })
    .withAutomaticReconnect(new ExponentialReconnectPolicy(backoffOptions))
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

  // Application-level heartbeat emitted by HubHeartbeatBackgroundService on
  // both /hubs/telemetry and /hubs/streaming (issue #92). We record the
  // arrival time so the UI can flag a stalled channel even while SignalR
  // still reports Connected (e.g. a proxy silently swallowing frames).
  connection.on('heartbeat', (event: HubHeartbeatEvent) => {
    lastHeartbeatAt = Date.now();
    heartbeatListeners.forEach((listener) => {
      try {
        listener(event);
      } catch (err) {
        if (import.meta.env.DEV) console.error('Hub heartbeat listener threw:', err);
      }
    });
  });

  connection.onreconnecting(() => {
    setStatus('reconnecting');
  });

  // Re-join any sessions on reconnect — SignalR groups don't survive reconnects.
  connection.onreconnected(() => {
    setStatus('connected');
    onConnected?.();
    joinPendingSessions();
  });
  connection.onclose(() => {
    setStatus('disconnected');
    onDisconnected?.();
  });

  startPromise = connection.start()
    .then(() => {
      setStatus('connected');
      onConnected?.();
      joinPendingSessions();
    })
    .catch(err => {
      if (import.meta.env.DEV) {
        console.error('SignalR connection error:', err);
      }
      setStatus('disconnected');
      onDisconnected?.();
    });

  return connection;
}

/**
 * Joins all queued sessions on the current connection.
 * Called after initial connect and on every reconnect.
 *
 * Reconnect security note (issue #92): we deliberately re-invoke
 * `JoinSession` only for sessionIds this client previously joined via
 * {@link joinTelemetrySession}. Server payloads never influence this set,
 * so a hostile server message cannot trick the client into rejoining a
 * foreign group. The hub side also enforces subject ownership on every
 * join and rejoin (`ISessionOwnershipRegistry`).
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
  setStatus('disconnected');
}

/**
 * Subscribe to hub connection-status transitions surfaced to the UI. The
 * listener fires the CURRENT status immediately so the caller renders the
 * right badge on mount without probing internals.
 */
export function onHubConnectionStatus(
  listener: (status: HubConnectionStatus) => void,
): () => void {
  statusListeners.add(listener);
  try {
    listener(currentStatus);
  } catch (err) {
    if (import.meta.env.DEV) console.error('Hub status listener threw:', err);
  }
  return () => { statusListeners.delete(listener); };
}

/**
 * Subscribe to the application-level `heartbeat` event emitted by the
 * backend hub heartbeat service. Callers can use this to detect a stalled
 * hub even while SignalR still reports Connected.
 */
export function onHubHeartbeat(
  listener: (event: HubHeartbeatEvent) => void,
): () => void {
  heartbeatListeners.add(listener);
  return () => { heartbeatListeners.delete(listener); };
}

export function getHubConnectionStatus(): HubConnectionStatus {
  return currentStatus;
}

export function getLastHubHeartbeatAt(): number | null {
  return lastHeartbeatAt;
}

/**
 * Test-only reset for module-level state. Guarded so it never runs in a
 * production bundle. Vitest sets `import.meta.env.MODE === 'test'`.
 */
export function __resetTelemetryHubForTests(): void {
  if (import.meta.env.MODE !== 'test') {
    throw new Error('__resetTelemetryHubForTests is a vitest-only helper.');
  }
  connection = null;
  startPromise = null;
  joinedSessions.clear();
  progressListeners.clear();
  statusListeners.clear();
  heartbeatListeners.clear();
  lastHeartbeatAt = null;
  currentStatus = 'disconnected';
}
