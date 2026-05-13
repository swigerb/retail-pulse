import { useState, useEffect, useMemo, useCallback } from 'react';
import { makeStyles, Card, Text, Button, Spinner } from '@fluentui/react-components';
import type { GuardrailsStats, GuardrailDetectionType, BlockedRequest } from '../../types';
import { fetchGuardrailsStats } from '../../services/guardrailsApi';
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid } from 'recharts';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    padding: '24px',
    height: '100%',
    overflowY: 'auto',
  },
  title: {
    fontSize: '22px',
    fontWeight: '700',
    color: 'var(--color-text, #e2e8f0)',
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  statsGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
    gap: '12px',
  },
  statCard: {
    padding: '16px',
    borderRadius: '12px',
    backgroundColor: 'var(--color-surface, #1e293b)',
    border: '1px solid var(--color-border, #334155)',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  statValue: {
    fontSize: '28px',
    fontWeight: '700',
    fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
  },
  statLabel: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  filterRow: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
  },
  filterChip: {
    padding: '6px 14px',
    borderRadius: '20px',
    fontSize: '12px',
    fontWeight: '500',
    cursor: 'pointer',
    border: '1px solid var(--color-border, #334155)',
    backgroundColor: 'var(--color-surface, #1e293b)',
    color: 'var(--color-text-muted, #94a3b8)',
    transition: 'all 0.2s ease',
    ':hover': {
      backgroundColor: 'var(--color-surface-hover, #334155)',
    },
  },
  filterChipActive: {
    backgroundColor: 'rgba(245, 158, 11, 0.15)',
    borderColor: 'rgba(245, 158, 11, 0.4)' as unknown as undefined,
    color: '#f59e0b',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  entry: {
    display: 'grid',
    gridTemplateColumns: '140px 1fr 100px 120px',
    gap: '12px',
    alignItems: 'center',
    padding: '10px 14px',
    borderRadius: '8px',
    backgroundColor: 'var(--color-surface, #1e293b)',
    border: '1px solid var(--color-border, #334155)',
    fontSize: '13px',
    '@media (max-width: 640px)': {
      gridTemplateColumns: '1fr',
      gap: '4px',
    },
  },
  timestamp: {
    color: 'var(--color-text-muted, #94a3b8)',
    fontSize: '12px',
    fontFamily: "'Courier New', monospace",
  },
  preview: {
    color: 'var(--color-text, #e2e8f0)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  typeBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: '12px',
    fontSize: '11px',
    fontWeight: '600',
    textTransform: 'uppercase',
  },
  chartSection: {
    borderRadius: '12px',
    padding: '16px',
    backgroundColor: 'var(--color-surface, #1e293b)',
    border: '1px solid var(--color-border, #334155)',
  },
  chartTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text, #e2e8f0)',
    marginBottom: '12px',
  },
  emptyState: {
    textAlign: 'center',
    padding: '40px 20px',
    color: 'var(--color-text-muted, #94a3b8)',
    fontSize: '14px',
  },
});

const TYPE_COLORS: Record<GuardrailDetectionType, { bg: string; color: string; icon: string }> = {
  jailbreak: { bg: 'rgba(239, 68, 68, 0.15)', color: '#ef4444', icon: '🚫' },
  pii: { bg: 'rgba(168, 85, 247, 0.15)', color: '#a855f7', icon: '🔐' },
  access: { bg: 'rgba(59, 130, 246, 0.15)', color: '#3b82f6', icon: '🔒' },
};

export function GuardrailsDashboard() {
  const styles = useStyles();
  const [stats, setStats] = useState<GuardrailsStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<GuardrailDetectionType | 'all'>('all');

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    fetchGuardrailsStats()
      .then(data => { if (!cancelled) { setStats(data); setError(null); } })
      .catch(err => { if (!cancelled) setError(err instanceof Error ? err.message : 'Failed to load'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const handleRefresh = useCallback(() => {
    setLoading(true);
    fetchGuardrailsStats()
      .then(data => { setStats(data); setError(null); })
      .catch(err => setError(err instanceof Error ? err.message : 'Failed to load'))
      .finally(() => setLoading(false));
  }, []);

  const filteredRequests = useMemo(() => {
    if (!stats) return [];
    const list = stats.recentBlocked.slice(0, 50);
    if (filter === 'all') return list;
    return list.filter(r => r.detectionType === filter);
  }, [stats, filter]);

  if (loading) {
    return (
      <div className={styles.container}>
        <div className={styles.emptyState}><Spinner size="medium" label="Loading guardrails data..." /></div>
      </div>
    );
  }

  if (error || !stats) {
    return (
      <div className={styles.container}>
        <div className={styles.emptyState}>
          <Text>⚠️ {error || 'No guardrails data available'}</Text>
          <br />
          <Button appearance="subtle" onClick={handleRefresh} style={{ marginTop: '12px' }}>Retry</Button>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.container} data-testid="guardrails-dashboard">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <span className={styles.title}>🛡️ Guardrails Security</span>
        <Button appearance="subtle" onClick={handleRefresh}>Refresh</Button>
      </div>

      {/* Stats Cards */}
      <div className={styles.statsGrid}>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue} style={{ color: '#f59e0b' }}>{stats.totalBlocked}</span>
          <span className={styles.statLabel}>Total Blocked</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue} style={{ color: '#ef4444' }}>{stats.jailbreakAttempts}</span>
          <span className={styles.statLabel}>Jailbreak Attempts</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue} style={{ color: '#a855f7' }}>{stats.piiDetections}</span>
          <span className={styles.statLabel}>PII Detections</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue} style={{ color: '#3b82f6' }}>{stats.accessDenials}</span>
          <span className={styles.statLabel}>Access Denials</span>
        </Card>
      </div>

      {/* Trend Chart */}
      {stats.blocksPerHour.length > 0 && (
        <div className={styles.chartSection}>
          <div className={styles.chartTitle}>Blocks Per Hour (Last 24h)</div>
          <ResponsiveContainer width="100%" height={200}>
            <BarChart data={stats.blocksPerHour}>
              <CartesianGrid strokeDasharray="3 3" stroke="#334155" />
              <XAxis
                dataKey="hour"
                tick={{ fill: '#94a3b8', fontSize: 11 }}
                tickFormatter={(v: string) => {
                  const d = new Date(v);
                  return isNaN(d.getTime()) ? String(v) : `${d.getHours()}:00`;
                }}
              />
              <YAxis tick={{ fill: '#94a3b8', fontSize: 11 }} allowDecimals={false} />
              <Tooltip
                contentStyle={{ backgroundColor: '#1e293b', border: '1px solid #334155', borderRadius: '8px' }}
                labelStyle={{ color: '#e2e8f0' }}
              />
              <Bar dataKey="count" fill="#f59e0b" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* Filter */}
      <div className={styles.filterRow}>
        {(['all', 'jailbreak', 'pii', 'access'] as const).map(type => (
          <button
            key={type}
            className={`${styles.filterChip} ${filter === type ? styles.filterChipActive : ''}`}
            onClick={() => setFilter(type)}
          >
            {type === 'all' ? '🔍 All' : `${TYPE_COLORS[type].icon} ${type.charAt(0).toUpperCase() + type.slice(1)}`}
          </button>
        ))}
      </div>

      {/* Recent Blocked Requests */}
      <div className={styles.list}>
        {filteredRequests.length === 0 ? (
          <div className={styles.emptyState}>No blocked requests found for this filter.</div>
        ) : (
          filteredRequests.map((req: BlockedRequest) => (
            <div key={req.id} className={styles.entry}>
              <span className={styles.timestamp}>
                {new Date(req.timestamp).toLocaleString()}
              </span>
              <span className={styles.preview} title={req.requestPreview}>
                {req.requestPreview}
              </span>
              <span
                className={styles.typeBadge}
                style={{
                  backgroundColor: TYPE_COLORS[req.detectionType].bg,
                  color: TYPE_COLORS[req.detectionType].color,
                }}
              >
                {TYPE_COLORS[req.detectionType].icon} {req.detectionType}
              </span>
              <span className={styles.timestamp}>{req.actionTaken}</span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
