import { useState, useEffect, useCallback } from 'react';
import { Badge, Button, Tooltip, makeStyles } from '@fluentui/react-components';
import { Checkmark24Regular } from '@fluentui/react-icons';
import { fetchPendingApprovals } from '../services/approvalApi';
import type { ApprovalRequest } from '../types';

export interface PendingApprovalsProps {
  /** Externally-managed pending count (e.g. from SignalR). Overrides internal polling. */
  pendingCount?: number;
  /** Approvals list pushed from parent (avoids internal fetch). */
  pendingApprovals?: ApprovalRequest[];
  onClick?: () => void;
}

const useStyles = makeStyles({
  container: {
    position: 'relative',
    display: 'inline-flex',
  },
  button: {
    position: 'relative',
  },
  badge: {
    position: 'absolute',
    top: '-4px',
    right: '-4px',
    minWidth: '18px',
    height: '18px',
    borderRadius: '9px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '10px',
    fontWeight: '700',
  },
  '@keyframes pulse': {
    '0%': { transform: 'scale(1)' },
    '50%': { transform: 'scale(1.15)' },
    '100%': { transform: 'scale(1)' },
  },
  pulsing: {
    animationName: {
      '0%': { transform: 'scale(1)' },
      '50%': { transform: 'scale(1.15)' },
      '100%': { transform: 'scale(1)' },
    },
    animationDuration: '1.5s',
    animationIterationCount: '3',
    animationTimingFunction: 'ease-in-out',
  },
});

export function PendingApprovals({ pendingCount: externalCount, pendingApprovals, onClick }: PendingApprovalsProps) {
  const [internalCount, setInternalCount] = useState(0);
  const [pulsing, setPulsing] = useState(false);
  const styles = useStyles();

  const count = externalCount ?? pendingApprovals?.length ?? internalCount;

  // Only poll if not externally managed
  useEffect(() => {
    if (externalCount !== undefined || pendingApprovals !== undefined) return;
    let cancelled = false;
    const poll = async () => {
      try {
        const pending = await fetchPendingApprovals();
        if (!cancelled) setInternalCount(pending.length);
      } catch {
        // Silently fail — badge just won't update
      }
    };
    poll();
    const interval = setInterval(poll, 15000);
    return () => { cancelled = true; clearInterval(interval); };
  }, [externalCount, pendingApprovals]);

  // Pulse when count increases
  const prevCount = useCallback(() => count, [])(); // capture initial
  useEffect(() => {
    if (count > 0 && count > prevCount) {
      setPulsing(true);
      const timer = setTimeout(() => setPulsing(false), 4500);
      return () => clearTimeout(timer);
    }
  }, [count, prevCount]);

  return (
    <Tooltip content={count > 0 ? `${count} pending approval${count !== 1 ? 's' : ''}` : 'No pending approvals'} relationship="label">
      <div className={styles.container}>
        <Button
          appearance="subtle"
          icon={<Checkmark24Regular />}
          onClick={onClick}
          aria-label={`${count} pending approvals`}
          data-testid="pending-approvals-button"
          className={styles.button}
        >
          Approvals
        </Button>
        {count > 0 && (
          <Badge
            appearance="filled"
            color="danger"
            size="small"
            className={`${styles.badge} ${pulsing ? styles.pulsing : ''}`}
            data-testid="pending-count-badge"
          >
            {count}
          </Badge>
        )}
      </div>
    </Tooltip>
  );
}
