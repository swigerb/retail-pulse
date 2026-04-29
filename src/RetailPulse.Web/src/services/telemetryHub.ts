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
    joinedSessions.forEach(sid => {
      connection?.invoke('JoinSession', sid).catch(err => {
        if (import.meta.env.DEV) console.error('JoinSession (reconnect) failed:', err);
      });
    });
  });
  connection.onclose(() => onDisconnected?.());

  connection.start()
    .then(() => onConnected?.())
    .catch(err => {
      if (import.meta.env.DEV) {
        console.error('SignalR connection error:', err);
      }
      onDisconnected?.();
    });

  return connection;
}

/**
 * Joins the SignalR group for a given session so the hub will route
 * session-scoped spans here. Call this once per session, after the API
 * has returned a sessionId.
 */
export async function joinTelemetrySession(sessionId: string): Promise<void> {
  if (!sessionId || !connection) return;
  if (joinedSessions.has(sessionId)) return;
  joinedSessions.add(sessionId);
  try {
    if (connection.state === signalR.HubConnectionState.Connected) {
      await connection.invoke('JoinSession', sessionId);
    }
    // If not yet connected, the onreconnected/start path will join it.
  } catch (err) {
    if (import.meta.env.DEV) console.error('JoinSession failed:', err);
  }
}

export function disconnectTelemetryHub() {
  connection?.stop();
  connection = null;
  joinedSessions.clear();
}

