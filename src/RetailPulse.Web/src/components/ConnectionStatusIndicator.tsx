import { Badge, makeStyles } from '@fluentui/react-components';
import type { HubConnectionStatus } from '../services/telemetryHub';

/**
 * Compact "connected / reconnecting / disconnected" pill used inline in the
 * chat composer so a dropped real-time channel is visible next to the
 * message the user is about to send, not buried in a drawer (issue #92).
 *
 * A subtle "stalled" state (SignalR still reports Connected but no
 * application-level heartbeat in `staleAfterMs`) renders the same
 * "reconnecting" label so we degrade gracefully in front of intermediaries
 * that swallow frames.
 */
export interface ConnectionStatusIndicatorProps {
  readonly status: HubConnectionStatus;
  readonly stalled?: boolean;
  /** Optional className override for layout tweaks by the parent. */
  readonly className?: string;
}

type PresentedState = 'connected' | 'reconnecting' | 'disconnected' | 'connecting';

function presentedState(status: HubConnectionStatus, stalled: boolean | undefined): PresentedState {
  if (status === 'connected' && stalled) return 'reconnecting';
  return status;
}

const useStyles = makeStyles({
  wrapper: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    flexShrink: 0,
  },
});

const LABELS: Readonly<Record<PresentedState, string>> = {
  connected: 'Live',
  connecting: 'Connecting…',
  reconnecting: 'Reconnecting…',
  disconnected: 'Disconnected',
};

const ICONS: Readonly<Record<PresentedState, string>> = {
  connected: '🟢',
  connecting: '🟡',
  reconnecting: '🟡',
  disconnected: '🔴',
};

const COLORS: Readonly<Record<PresentedState, 'success' | 'warning' | 'danger'>> = {
  connected: 'success',
  connecting: 'warning',
  reconnecting: 'warning',
  disconnected: 'danger',
};

export function ConnectionStatusIndicator({
  status,
  stalled,
  className,
}: ConnectionStatusIndicatorProps) {
  const styles = useStyles();
  const presented = presentedState(status, stalled);
  return (
    <span
      className={className ? `${styles.wrapper} ${className}` : styles.wrapper}
      data-testid="connection-status-indicator"
      data-status={presented}
      role="status"
      aria-live="polite"
      aria-label={`Real-time channel: ${LABELS[presented]}`}
      title={LABELS[presented]}
    >
      <Badge appearance="filled" color={COLORS[presented]} size="small">
        <span aria-hidden="true">{ICONS[presented]}</span>&nbsp;{LABELS[presented]}
      </Badge>
    </span>
  );
}
