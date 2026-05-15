import * as signalR from '@microsoft/signalr';
import type { AgentSpan } from '../types';

const HUB_URL = '/hubs/telemetry';

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
    if (connection.state === signalR.HubConnectionState.Connected) {
      onConnected?.();
    }
    return connection;
  }

  connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL)
    .withAutomaticReconnect()
    .build();

  connection.on('SpanReceived', (span: AgentSpan) => {
    onSpan(span);
  });

  connection.on('progress', (data: ProgressEvent) => {
    progressListeners.forEach(listener => listener(data));
  });

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

