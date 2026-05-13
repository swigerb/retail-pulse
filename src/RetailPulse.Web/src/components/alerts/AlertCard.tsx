import { useState, useEffect, useRef, useCallback } from 'react';
import { Button, Badge, Text, makeStyles } from '@fluentui/react-components';
import { Dismiss16Regular, ChevronDown16Regular, ChevronUp16Regular, Timer16Regular } from '@fluentui/react-icons';
import type { Alert, AlertSeverity, SnoozeDuration } from '../../types';

export interface AlertCardProps {
  alert: Alert;
  onDismiss?: (id: string) => void;
  onSnooze?: (id: string, duration: SnoozeDuration) => void;
  onViewDetails?: (id: string) => void;
  autoDismissMs?: number;
  animate?: boolean;
}

const SEVERITY_COLORS: Record<AlertSeverity, string> = {
  high: '#ef4444',
  medium: '#f59e0b',
  low: '#22c55e',
};

const SEVERITY_EMOJIS: Record<AlertSeverity, string> = {
  high: '🔴',
  medium: '🟡',
  low: '🟢',
};

const SEVERITY_LABELS: Record<AlertSeverity, string> = {
  high: 'High',
  medium: 'Medium',
  low: 'Low',
};

const SNOOZE_OPTIONS: { label: string; value: SnoozeDuration }[] = [
  { label: '1 hour', value: '1h' },
  { label: '4 hours', value: '4h' },
  { label: '24 hours', value: '24h' },
  { label: '1 week', value: '1wk' },
];

const useStyles = makeStyles({
  card: {
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderLeftWidth: '4px',
    borderRadius: '8px',
    padding: '14px 16px',
    position: 'relative',
    overflow: 'hidden',
    transition: 'all 0.3s ease',
    ':hover': {
      background: 'var(--color-surface-hover)',
    },
  },
  animateIn: {
    animationName: {
      from: { transform: 'translateX(100%)', opacity: 0 },
      to: { transform: 'translateX(0)', opacity: 1 },
    },
    animationDuration: '0.4s',
    animationTimingFunction: 'cubic-bezier(0.4, 0, 0.2, 1)',
    animationFillMode: 'forwards',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginBottom: '8px',
  },
  severityBadge: {
    fontSize: '11px',
    fontWeight: '700',
    padding: '2px 8px',
    borderRadius: '4px',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    animationName: {
      '0%': { opacity: 1 },
      '50%': { opacity: 0.6 },
      '100%': { opacity: 1 },
    },
    animationDuration: '2s',
    animationIterationCount: 'infinite',
  },
  title: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
    flex: '1',
    lineHeight: '1.3',
  },
  dismissBtn: {
    minWidth: 'auto',
    padding: '4px',
    flexShrink: 0,
  },
  context: {
    display: 'flex',
    gap: '8px',
    marginBottom: '8px',
    flexWrap: 'wrap',
  },
  contextTag: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    background: 'rgba(255,255,255,0.06)',
    padding: '2px 8px',
    borderRadius: '4px',
  },
  description: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
    marginBottom: '8px',
  },
  details: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
    padding: '12px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '6px',
    marginBottom: '8px',
    borderTop: '1px solid var(--color-border)',
  },
  actions: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  snoozeDropdown: {
    position: 'relative',
    display: 'inline-block',
  },
  snoozeMenu: {
    position: 'absolute',
    bottom: '100%',
    left: '0',
    marginBottom: '4px',
    background: 'var(--color-bg-elevated)',
    border: '1px solid var(--color-border)',
    borderRadius: '6px',
    padding: '4px',
    zIndex: 10,
    minWidth: '120px',
    boxShadow: '0 4px 12px rgba(0,0,0,0.3)',
  },
  snoozeItem: {
    display: 'block',
    width: '100%',
    textAlign: 'left',
    padding: '6px 12px',
    fontSize: '12px',
    color: 'var(--color-text)',
    background: 'none',
    border: 'none',
    borderRadius: '4px',
    cursor: 'pointer',
    ':hover': {
      background: 'rgba(255,255,255,0.08)',
    },
  },
  changePercent: {
    fontWeight: '700',
    fontSize: '14px',
  },
  autoDismissBar: {
    position: 'absolute',
    bottom: '0',
    left: '0',
    height: '2px',
    background: 'var(--color-text-subtle)',
    opacity: 0.3,
    transition: 'width 0.1s linear',
  },
});

export function AlertCard({
  alert,
  onDismiss,
  onSnooze,
  onViewDetails,
  autoDismissMs = 30_000,
  animate = false,
}: AlertCardProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const [snoozeOpen, setSnoozeOpen] = useState(false);
  const [interacted, setInteracted] = useState(false);
  const [progress, setProgress] = useState(100);
  const timerRef = useRef<ReturnType<typeof setInterval>>(undefined);
  const startTimeRef = useRef(Date.now());

  const handleInteraction = useCallback(() => {
    setInteracted(true);
    if (timerRef.current) {
      clearInterval(timerRef.current);
      timerRef.current = undefined;
    }
    setProgress(0);
  }, []);

  // Auto-dismiss timer
  useEffect(() => {
    if (interacted || alert.status !== 'active' || autoDismissMs <= 0) return;

    startTimeRef.current = Date.now();
    timerRef.current = setInterval(() => {
      const elapsed = Date.now() - startTimeRef.current;
      const remaining = Math.max(0, 100 - (elapsed / autoDismissMs) * 100);
      setProgress(remaining);
      if (remaining <= 0) {
        clearInterval(timerRef.current);
        timerRef.current = undefined;
        onDismiss?.(alert.id);
      }
    }, 100);

    return () => {
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, [alert.id, alert.status, autoDismissMs, interacted, onDismiss]);

  const borderColor = SEVERITY_COLORS[alert.severity];

  return (
    <div
      data-testid="alert-card"
      className={`${styles.card} ${animate ? styles.animateIn : ''}`}
      style={{ borderLeftColor: borderColor }}
      onMouseEnter={handleInteraction}
      onFocus={handleInteraction}
      role="alert"
      aria-label={`${alert.severity} severity alert: ${alert.title}`}
    >
      <div className={styles.header}>
        <span>{SEVERITY_EMOJIS[alert.severity]}</span>
        <Badge
          data-testid="severity-badge"
          appearance="filled"
          style={{ background: borderColor, color: '#fff' }}
          className={styles.severityBadge}
        >
          {SEVERITY_LABELS[alert.severity]}
        </Badge>
        <span className={styles.title}>{alert.title}</span>
        <Button
          appearance="subtle"
          size="small"
          className={styles.dismissBtn}
          icon={<Dismiss16Regular />}
          onClick={() => onDismiss?.(alert.id)}
          aria-label="Dismiss alert"
        />
      </div>

      {(alert.brand || alert.region) && (
        <div className={styles.context}>
          {alert.brand && <span className={styles.contextTag}>🏷️ {alert.brand}</span>}
          {alert.region && <span className={styles.contextTag}>📍 {alert.region}</span>}
          {alert.changePercent != null && (
            <span
              className={styles.changePercent}
              style={{ color: alert.changePercent >= 0 ? '#22c55e' : '#ef4444' }}
            >
              {alert.changePercent >= 0 ? '↑' : '↓'} {Math.abs(alert.changePercent).toFixed(1)}%
            </span>
          )}
        </div>
      )}

      <Text className={styles.description}>{alert.description}</Text>

      {expanded && (
        <div className={styles.details} data-testid="alert-details">
          <Text style={{ fontWeight: 600, marginBottom: '4px', display: 'block', color: 'var(--color-text)' }}>
            💡 Recommended Action
          </Text>
          <Text>{alert.recommendedAction}</Text>
        </div>
      )}

      <div className={styles.actions}>
        <Button
          appearance="subtle"
          size="small"
          icon={expanded ? <ChevronUp16Regular /> : <ChevronDown16Regular />}
          onClick={() => {
            handleInteraction();
            setExpanded(prev => !prev);
            onViewDetails?.(alert.id);
          }}
        >
          {expanded ? 'Hide Details' : 'View Details'}
        </Button>

        <div className={styles.snoozeDropdown}>
          <Button
            appearance="subtle"
            size="small"
            icon={<Timer16Regular />}
            onClick={() => {
              handleInteraction();
              setSnoozeOpen(prev => !prev);
            }}
            aria-label="Snooze alert"
          >
            Snooze
          </Button>
          {snoozeOpen && (
            <div className={styles.snoozeMenu} data-testid="snooze-menu" role="menu">
              {SNOOZE_OPTIONS.map(opt => (
                <button
                  key={opt.value}
                  className={styles.snoozeItem}
                  role="menuitem"
                  onClick={() => {
                    onSnooze?.(alert.id, opt.value);
                    setSnoozeOpen(false);
                  }}
                >
                  {opt.label}
                </button>
              ))}
            </div>
          )}
        </div>
      </div>

      {!interacted && alert.status === 'active' && autoDismissMs > 0 && (
        <div
          className={styles.autoDismissBar}
          style={{ width: `${progress}%` }}
          data-testid="auto-dismiss-bar"
        />
      )}
    </div>
  );
}
