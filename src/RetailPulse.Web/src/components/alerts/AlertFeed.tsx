import { useState, useMemo, useCallback } from 'react';
import { Button, Badge, Text, makeStyles } from '@fluentui/react-components';
import { Dismiss16Regular } from '@fluentui/react-icons';
import { AlertCard } from './AlertCard';
import type { Alert, AlertSeverity, SnoozeDuration } from '../../types';

export interface AlertFeedProps {
  alerts: Alert[];
  onDismiss?: (id: string) => void;
  onSnooze?: (id: string, duration: SnoozeDuration) => void;
  onClearAll?: () => void;
}

const SEVERITY_ORDER: Record<AlertSeverity, number> = { high: 0, medium: 1, low: 2 };

const useStyles = makeStyles({
  feed: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '0 4px',
  },
  headerLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  title: {
    fontSize: '16px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  badge: {
    fontSize: '11px',
    fontWeight: '700',
  },
  groupLabel: {
    fontSize: '11px',
    fontWeight: '600',
    color: 'var(--color-text-subtle)',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    padding: '8px 4px 4px',
  },
  empty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '48px 16px',
    textAlign: 'center',
    color: 'var(--color-text-subtle)',
  },
  emptyIcon: {
    fontSize: '32px',
    marginBottom: '8px',
  },
});

export function AlertFeed({ alerts, onDismiss, onSnooze, onClearAll }: AlertFeedProps) {
  const styles = useStyles();
  const [recentlyAdded] = useState<Set<string>>(new Set());

  const activeAlerts = useMemo(
    () => alerts
      .filter(a => a.status === 'active')
      .sort((a, b) => {
        const sevDiff = SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity];
        if (sevDiff !== 0) return sevDiff;
        return new Date(b.firedAt).getTime() - new Date(a.firedAt).getTime();
      }),
    [alerts],
  );

  const grouped = useMemo(() => {
    const groups: Record<AlertSeverity, Alert[]> = { high: [], medium: [], low: [] };
    activeAlerts.forEach(a => groups[a.severity].push(a));
    return groups;
  }, [activeAlerts]);

  const handleDismiss = useCallback((id: string) => {
    onDismiss?.(id);
  }, [onDismiss]);

  const handleSnooze = useCallback((id: string, duration: SnoozeDuration) => {
    onSnooze?.(id, duration);
  }, [onSnooze]);

  const activeCount = activeAlerts.length;

  return (
    <div className={styles.feed} data-testid="alert-feed">
      <div className={styles.header}>
        <div className={styles.headerLeft}>
          <Text className={styles.title}>🔔 Alerts</Text>
          {activeCount > 0 && (
            <Badge
              data-testid="alert-count-badge"
              appearance="filled"
              color="danger"
              className={styles.badge}
            >
              {activeCount}
            </Badge>
          )}
        </div>
        {activeCount > 0 && (
          <Button
            appearance="subtle"
            size="small"
            icon={<Dismiss16Regular />}
            onClick={onClearAll}
          >
            Clear All
          </Button>
        )}
      </div>

      {activeCount === 0 ? (
        <div className={styles.empty}>
          <span className={styles.emptyIcon}>✅</span>
          <Text>No active alerts</Text>
          <Text style={{ fontSize: '12px', marginTop: '4px', opacity: 0.6 }}>
            Alerts will appear here when anomalies are detected
          </Text>
        </div>
      ) : (
        (['high', 'medium', 'low'] as AlertSeverity[]).map(severity => {
          const group = grouped[severity];
          if (group.length === 0) return null;
          return (
            <div key={severity}>
              <Text className={styles.groupLabel}>
                {severity === 'high' ? '🔴' : severity === 'medium' ? '🟡' : '🟢'}{' '}
                {severity.toUpperCase()} ({group.length})
              </Text>
              {group.map(alert => (
                <div key={alert.id} style={{ marginBottom: '8px' }}>
                  <AlertCard
                    alert={alert}
                    onDismiss={handleDismiss}
                    onSnooze={handleSnooze}
                    animate={!recentlyAdded.has(alert.id)}
                    autoDismissMs={0}
                  />
                </div>
              ))}
            </div>
          );
        })
      )}
    </div>
  );
}
