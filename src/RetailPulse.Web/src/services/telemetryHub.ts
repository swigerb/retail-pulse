import * as signalR from '@microsoft/signalr';
import type { AgentSpan } from '../types';

const HUB_URL = '/hubs/telemetry';

let connection: signalR.HubConnection | null = null;

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

  connection.onreconnected(() => onConnected?.());
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

export function disconnectTelemetryHub() {
  connection?.stop();
  connection = null;
}
