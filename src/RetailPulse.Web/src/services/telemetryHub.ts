import * as signalR from '@microsoft/signalr';
import type { AgentSpan } from '../types';

const HUB_URL = '/hubs/telemetry';

let connection: signalR.HubConnection | null = null;
const joinedSessions = new Set<string>();

export function connectTelemetryHub(
  onSpan: (span: AgentSpan) => void,
  onConnected?: () => void,
  onDisconnected?: () => void,
): signalR.HubConnection {
  if (connection) return connection;

  connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL)
    .withAutomaticReconnect()
    .build();

  connection.on('SpanReceived', (span: AgentSpan) => {
    onSpan(span);
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

  connection.start()
    .then(() => {
      onConnected?.();
      // Join any sessions that were queued before the connection was ready
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

export function disconnectTelemetryHub() {
  connection?.stop();
  connection = null;
  joinedSessions.clear();
}

