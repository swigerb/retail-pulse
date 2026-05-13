import { useState, useEffect, useRef, useCallback } from 'react';
import {
  Button,
  Badge,
  Text,
  Textarea,
  makeStyles,
} from '@fluentui/react-components';
import type { ApprovalRequest, ApprovalDecision } from '../types';
import { respondToApproval } from '../services/approvalApi';

export interface ApprovalCardProps {
  approval: ApprovalRequest;
  onResolved?: (id: string, decision: ApprovalDecision) => void;
}

const URGENCY_CONFIG: Record<string, { color: string; bgColor: string; borderColor: string; label: string; badge: 'danger' | 'warning' | 'success' }> = {
  high: { color: '#fca5a5', bgColor: 'rgba(239, 68, 68, 0.08)', borderColor: 'rgba(239, 68, 68, 0.3)', label: '🔴 High', badge: 'danger' },
  medium: { color: '#fde68a', bgColor: 'rgba(234, 179, 8, 0.08)', borderColor: 'rgba(234, 179, 8, 0.3)', label: '🟡 Medium', badge: 'warning' },
  low: { color: '#86efac', bgColor: 'rgba(34, 197, 94, 0.08)', borderColor: 'rgba(34, 197, 94, 0.3)', label: '🟢 Low', badge: 'success' },
};

const DECISION_DISPLAY: Record<string, { emoji: string; label: string; color: string }> = {
  approved: { emoji: '✅', label: 'Approved', color: '#22c55e' },
  rejected: { emoji: '❌', label: 'Rejected', color: '#ef4444' },
  modified: { emoji: '✏️', label: 'Modified', color: '#3b82f6' },
  timed_out: { emoji: '⏰', label: 'Timed Out', color: '#6b7280' },
};

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: '12px',
    animation: 'messageIn 0.3s ease',
    transition: 'all 0.2s ease',
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
  },
  headerLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flex: '1',
    minWidth: '0',
  },
  actionTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
    flex: '1',
    minWidth: '0',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  sectionLabel: {
    fontSize: '11px',
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    color: 'var(--color-text-subtle)',
  },
  sectionText: {
    fontSize: '13px',
    lineHeight: '1.5',
    color: 'var(--color-text)',
  },
  timer: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: '12px',
    fontWeight: '500',
    fontFamily: "'Courier New', monospace",
  },
  actions: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    marginTop: '4px',
  },
  commentField: {
    marginTop: '4px',
  },
  resolvedBanner: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '10px 14px',
    borderRadius: '8px',
    fontSize: '13px',
    fontWeight: '500',
    backgroundColor: 'var(--color-surface-hover)',
    border: '1px solid var(--color-border)',
  },
});

function useCountdown(timeoutAt: string, active: boolean): string {
  const [remaining, setRemaining] = useState('');
  const intervalRef = useRef<ReturnType<typeof setInterval>>(undefined);

  useEffect(() => {
    if (!active) return;
    const update = () => {
      const now = Date.now();
      const end = new Date(timeoutAt).getTime();
      const diff = Math.max(0, end - now);
      if (diff <= 0) {
        setRemaining('0:00');
        return;
      }
      const mins = Math.floor(diff / 60000);
      const secs = Math.floor((diff % 60000) / 1000);
      setRemaining(`${mins}:${secs.toString().padStart(2, '0')}`);
    };
    update();
    intervalRef.current = setInterval(update, 1000);
    return () => clearInterval(intervalRef.current);
  }, [timeoutAt, active]);

  return remaining;
}

export function ApprovalCard({ approval, onResolved }: ApprovalCardProps) {
  const [responding, setResponding] = useState(false);
  const [showComment, setShowComment] = useState(false);
  const [comment, setComment] = useState('');
  const [localStatus, setLocalStatus] = useState<ApprovalDecision>(approval.status);
  const styles = useStyles();

  const isPending = localStatus === 'pending';
  const countdown = useCountdown(approval.timeoutAt, isPending);
  const urgencyConfig = URGENCY_CONFIG[approval.urgency] ?? URGENCY_CONFIG.medium;

  // Check for timeout
  useEffect(() => {
    if (!isPending) return;
    const end = new Date(approval.timeoutAt).getTime();
    const now = Date.now();
    if (now >= end) {
      setLocalStatus('timed_out');
      onResolved?.(approval.id, 'timed_out');
      return;
    }
    const timer = setTimeout(() => {
      setLocalStatus('timed_out');
      onResolved?.(approval.id, 'timed_out');
    }, end - now);
    return () => clearTimeout(timer);
  }, [approval.id, approval.timeoutAt, isPending, onResolved]);

  // Sync external status changes
  useEffect(() => {
    setLocalStatus(approval.status);
  }, [approval.status]);

  const handleDecision = useCallback(async (decision: 'approved' | 'rejected' | 'modified') => {
    setResponding(true);
    try {
      await respondToApproval(approval.id, {
        decision,
        comment: comment.trim() || undefined,
      });
      setLocalStatus(decision);
      onResolved?.(approval.id, decision);
    } catch {
      // Keep pending on error — user can retry
    } finally {
      setResponding(false);
    }
  }, [approval.id, comment, onResolved]);

  const cardStyle: React.CSSProperties = {
    backgroundColor: isPending ? urgencyConfig.bgColor : 'var(--color-surface)',
    border: `1px solid ${isPending ? urgencyConfig.borderColor : 'var(--color-border)'}`,
    opacity: isPending ? 1 : 0.85,
  };

  const timerColor = isPending
    ? (countdown <= '1:00' ? '#ef4444' : countdown <= '2:00' ? '#eab308' : 'var(--color-text-muted)')
    : 'var(--color-text-subtle)';

  const decisionInfo = !isPending ? DECISION_DISPLAY[localStatus] : null;

  return (
    <div className={styles.card} style={cardStyle} data-testid="approval-card">
      <div className={styles.headerRow}>
        <div className={styles.headerLeft}>
          <Badge appearance="filled" color={urgencyConfig.badge} data-testid="urgency-badge">
            {urgencyConfig.label}
          </Badge>
          <Text className={styles.actionTitle} truncate>{approval.action}</Text>
        </div>
        <div className={styles.timer} style={{ color: timerColor }} data-testid="approval-timer">
          ⏱️ {isPending ? countdown : '—'}
        </div>
      </div>

      <div className={styles.section}>
        <span className={styles.sectionLabel}>Reasoning</span>
        <span className={styles.sectionText}>{approval.reasoning}</span>
      </div>

      <div className={styles.section}>
        <span className={styles.sectionLabel}>Impact</span>
        <span className={styles.sectionText}>{approval.impact}</span>
      </div>

      {isPending ? (
        <>
          {showComment && (
            <Textarea
              className={styles.commentField}
              value={comment}
              onChange={(_e, data) => setComment(data.value)}
              placeholder="Add a comment (optional)..."
              size="small"
              resize="vertical"
              data-testid="approval-comment"
            />
          )}
          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="small"
              onClick={() => handleDecision('approved')}
              disabled={responding}
              data-testid="approve-button"
              style={{ backgroundColor: '#22c55e', borderColor: '#22c55e' }}
            >
              ✅ Approve
            </Button>
            <Button
              appearance="primary"
              size="small"
              onClick={() => handleDecision('rejected')}
              disabled={responding}
              data-testid="reject-button"
              style={{ backgroundColor: '#ef4444', borderColor: '#ef4444' }}
            >
              ❌ Reject
            </Button>
            <Button
              appearance="outline"
              size="small"
              onClick={() => {
                if (showComment && comment.trim()) {
                  handleDecision('modified');
                } else {
                  setShowComment(!showComment);
                }
              }}
              disabled={responding}
              data-testid="modify-button"
            >
              ✏️ Modify
            </Button>
          </div>
        </>
      ) : (
        decisionInfo && (
          <div className={styles.resolvedBanner} data-testid="resolved-banner">
            <span>{decisionInfo.emoji}</span>
            <span style={{ color: decisionInfo.color, fontWeight: '600' }}>{decisionInfo.label}</span>
            {approval.decidedBy && (
              <span style={{ color: 'var(--color-text-muted)', fontSize: '12px' }}>
                by {approval.decidedBy}
              </span>
            )}
            {approval.comment && (
              <span style={{ color: 'var(--color-text-muted)', fontSize: '12px', fontStyle: 'italic' }}>
                — &quot;{approval.comment}&quot;
              </span>
            )}
          </div>
        )
      )}
    </div>
  );
}
