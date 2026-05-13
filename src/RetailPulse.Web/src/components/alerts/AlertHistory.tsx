import { useState, useMemo } from 'react';
import { Input, Badge, Text, makeStyles } from '@fluentui/react-components';
import { Search16Regular } from '@fluentui/react-icons';
import type { Alert, AlertSeverity } from '../../types';

export interface AlertHistoryProps {
  alerts: Alert[];
}

const SEVERITY_COLORS: Record<AlertSeverity, string> = {
  high: '#ef4444',
  medium: '#f59e0b',
  low: '#22c55e',
};

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  title: {
    fontSize: '16px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  filters: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    alignItems: 'center',
  },
  filterChip: {
    fontSize: '11px',
    padding: '4px 10px',
    borderRadius: '12px',
    border: '1px solid var(--color-border)',
    background: 'transparent',
    color: 'var(--color-text-muted)',
    cursor: 'pointer',
    transition: 'all 0.2s',
    ':hover': {
      background: 'rgba(255,255,255,0.06)',
    },
  },
  filterChipActive: {
    background: 'rgba(99, 102, 241, 0.15)',
    borderTopColor: '#6366f1',
    borderRightColor: '#6366f1',
    borderBottomColor: '#6366f1',
    borderLeftColor: '#6366f1',
    color: '#6366f1',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '12px',
  },
  th: {
    textAlign: 'left',
    padding: '8px 10px',
    fontSize: '10px',
    fontWeight: '600',
    color: 'var(--color-text-subtle)',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    borderBottom: '1px solid var(--color-border)',
  },
  td: {
    padding: '8px 10px',
    color: 'var(--color-text-muted)',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
    verticalAlign: 'top',
  },
  row: {
    transition: 'background 0.2s',
    ':hover': {
      background: 'rgba(255,255,255,0.03)',
    },
  },
  statusBadge: {
    fontSize: '10px',
    fontWeight: '600',
    textTransform: 'capitalize',
  },
  snoozeInfo: {
    fontSize: '10px',
    color: 'var(--color-text-subtle)',
    display: 'block',
    marginTop: '2px',
  },
  empty: {
    textAlign: 'center',
    padding: '32px',
    color: 'var(--color-text-subtle)',
  },
});

const STATUS_COLORS: Record<string, 'informative' | 'success' | 'warning' | 'danger'> = {
  active: 'danger',
  snoozed: 'warning',
  dismissed: 'informative',
};

export function AlertHistory({ alerts }: AlertHistoryProps) {
  const styles = useStyles();
  const [search, setSearch] = useState('');
  const [severityFilter, setSeverityFilter] = useState<AlertSeverity | 'all'>('all');

  const filtered = useMemo(() => {
    return alerts
      .filter(a => {
        if (severityFilter !== 'all' && a.severity !== severityFilter) return false;
        if (search) {
          const q = search.toLowerCase();
          return (
            a.title.toLowerCase().includes(q) ||
            (a.brand?.toLowerCase().includes(q) ?? false) ||
            (a.region?.toLowerCase().includes(q) ?? false)
          );
        }
        return true;
      })
      .sort((a, b) => new Date(b.firedAt).getTime() - new Date(a.firedAt).getTime());
  }, [alerts, search, severityFilter]);

  const formatTime = (iso: string) => {
    const d = new Date(iso);
    return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  };

  const formatSnoozeUntil = (iso?: string) => {
    if (!iso) return null;
    const d = new Date(iso);
    return `Snoozed until ${d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}`;
  };

  return (
    <div className={styles.panel} data-testid="alert-history">
      <Text className={styles.title}>📋 Alert History</Text>

      <div className={styles.filters}>
        <Input
          size="small"
          placeholder="Search alerts..."
          contentBefore={<Search16Regular />}
          value={search}
          onChange={(_, d) => setSearch(d.value)}
          style={{ flex: 1, minWidth: '140px' }}
        />
        {(['all', 'high', 'medium', 'low'] as const).map(sev => (
          <button
            key={sev}
            className={`${styles.filterChip} ${severityFilter === sev ? styles.filterChipActive : ''}`}
            onClick={() => setSeverityFilter(sev)}
          >
            {sev === 'all' ? 'All' : sev.charAt(0).toUpperCase() + sev.slice(1)}
          </button>
        ))}
      </div>

      {filtered.length === 0 ? (
        <div className={styles.empty}>
          <Text>No alerts match your filters</Text>
        </div>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th className={styles.th}>Time</th>
                <th className={styles.th}>Severity</th>
                <th className={styles.th}>Title</th>
                <th className={styles.th}>Brand</th>
                <th className={styles.th}>Region</th>
                <th className={styles.th}>Status</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(alert => (
                <tr key={alert.id} className={styles.row}>
                  <td className={styles.td} style={{ whiteSpace: 'nowrap' }}>
                    {formatTime(alert.firedAt)}
                  </td>
                  <td className={styles.td}>
                    <Badge
                      appearance="filled"
                      style={{ background: SEVERITY_COLORS[alert.severity], color: '#fff', fontSize: '10px' }}
                    >
                      {alert.severity.toUpperCase()}
                    </Badge>
                  </td>
                  <td className={styles.td} style={{ maxWidth: '200px' }}>
                    {alert.title}
                  </td>
                  <td className={styles.td}>{alert.brand || '—'}</td>
                  <td className={styles.td}>{alert.region || '—'}</td>
                  <td className={styles.td}>
                    <Badge
                      appearance="tint"
                      color={STATUS_COLORS[alert.status] || 'informative'}
                      className={styles.statusBadge}
                    >
                      {alert.status}
                    </Badge>
                    {alert.status === 'snoozed' && alert.snoozedUntil && (
                      <span className={styles.snoozeInfo}>
                        {formatSnoozeUntil(alert.snoozedUntil)}
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
