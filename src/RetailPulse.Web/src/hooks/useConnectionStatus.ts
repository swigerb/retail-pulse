import { useEffect, useState } from 'react';
import {
  getHubConnectionStatus,
  onHubConnectionStatus,
  onHubHeartbeat,
  type HubConnectionStatus,
} from '../services/telemetryHub';

export interface ConnectionStatusState {
  readonly status: HubConnectionStatus;
  /**
   * True when SignalR reports "connected" but we have not observed an
   * application-level heartbeat within `staleAfterMs`. This flags a hub
   * that's technically alive but silent (e.g. an intermediary swallowing
   * frames) so the UI can render "Reconnecting…" instead of a stale
   * "Connected" badge.
   */
  readonly stalled: boolean;
}

export interface UseConnectionStatusOptions {
  /**
   * Milliseconds of heartbeat silence tolerated before we flag the channel
   * as stalled. Defaults to `2 × ApplicationHeartbeatInterval` — matches
   * the backend's 15s cadence, so 30s of silence trips the stalled flag.
   */
  readonly staleAfterMs?: number;
}

const DEFAULT_STALE_AFTER_MS = 30_000;

/**
 * Subscribes to the telemetry hub's connection-status transitions plus its
 * application-level heartbeat. Returns a stable `{ status, stalled }` pair
 * a component can render as a "connected / reconnecting / disconnected"
 * badge (issue #92).
 */
export function useConnectionStatus(
  options: UseConnectionStatusOptions = {},
): ConnectionStatusState {
  const staleAfterMs = options.staleAfterMs ?? DEFAULT_STALE_AFTER_MS;

  const [status, setStatus] = useState<HubConnectionStatus>(() => getHubConnectionStatus());
  const [stalled, setStalled] = useState(false);

  useEffect(() => {
    const off = onHubConnectionStatus((next) => {
      setStatus(next);
      if (next !== 'connected') {
        // Only "connected" can be stalled; other states already surface a
        // non-live indicator, so clear the stalled flag to avoid a
        // "reconnecting + stalled" double-signal.
        setStalled(false);
      }
    });
    return off;
  }, []);

  useEffect(() => {
    if (staleAfterMs <= 0) return;

    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const arm = () => {
      if (cancelled) return;
      if (timer !== null) clearTimeout(timer);
      timer = setTimeout(() => {
        if (!cancelled && getHubConnectionStatus() === 'connected') {
          setStalled(true);
        }
      }, staleAfterMs);
    };

    const offHeartbeat = onHubHeartbeat(() => {
      if (cancelled) return;
      setStalled(false);
      arm();
    });

    // Arm the initial stall detector so a connection that never emits a
    // heartbeat eventually flags itself.
    arm();

    return () => {
      cancelled = true;
      if (timer !== null) clearTimeout(timer);
      offHeartbeat();
    };
  }, [staleAfterMs]);

  return { status, stalled };
}
