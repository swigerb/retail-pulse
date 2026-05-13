import { useState, useEffect } from 'react';
import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
} from 'recharts';
import { makeStyles, Badge } from '@fluentui/react-components';
import { KB_COLORS } from '../../constants/agentRouting';
import { fetchKBStats } from '../../services/knowledgeApi';
import type { KBStats } from '../../types';

const AXIS_TICK = { fill: '#94a3b8', fontSize: 11 } as const;

const tooltipContentStyle = {
  backgroundColor: '#1e1b2e',
  border: '1px solid rgba(6,182,212,0.3)',
  borderRadius: 8,
  color: '#f1f5f9',
  fontSize: 12,
} as const;

const useStyles = makeStyles({
  wrapper: {
    padding: '16px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.02))',
    border: '1px solid rgba(255,255,255,0.06)',
    borderRadius: '12px',
  },
  title: {
    fontSize: '14px',
    fontWeight: '600',
    color: '#06b6d4' as const,
    marginBottom: '14px',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  statsGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '10px',
    marginBottom: '16px',
  },
  stat: {
    padding: '10px',
    borderRadius: '8px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    border: '1px solid rgba(255,255,255,0.06)',
    textAlign: 'center',
  },
  statValue: {
    fontSize: '18px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  statLabel: {
    fontSize: '10px',
    color: '#94a3b8',
    marginTop: '2px',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  section: {
    marginTop: '14px',
  },
  sectionTitle: {
    fontSize: '12px',
    fontWeight: '600',
    color: '#94a3b8',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    marginBottom: '8px',
  },
  citedList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  citedItem: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '6px 10px',
    borderRadius: '6px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    fontSize: '12px',
  },
  citedTitle: {
    color: 'var(--color-text-muted)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flex: 1,
    marginRight: '8px',
  },
  loading: {
    padding: '20px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '13px',
  },
});

export default function KnowledgeStats() {
  const styles = useStyles();
  const [stats, setStats] = useState<KBStats | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    fetchKBStats()
      .then(setStats)
      .catch(() => setStats(null))
      .finally(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <div className={styles.wrapper}>
        <div className={styles.title}>📊 KB Health</div>
        <div className={styles.loading}>Loading stats...</div>
      </div>
    );
  }

  if (!stats) {
    return (
      <div className={styles.wrapper} data-testid="knowledge-stats">
        <div className={styles.title}>📊 KB Health</div>
        <div className={styles.loading}>No stats available</div>
      </div>
    );
  }

  return (
    <div className={styles.wrapper} data-testid="knowledge-stats">
      <div className={styles.title}>📊 KB Health</div>

      <div className={styles.statsGrid}>
        <div className={styles.stat}>
          <div className={styles.statValue}>{stats.totalDocuments}</div>
          <div className={styles.statLabel}>Documents</div>
        </div>
        <div className={styles.stat}>
          <div className={styles.statValue}>{stats.totalChunks}</div>
          <div className={styles.statLabel}>Chunks</div>
        </div>
      </div>

      <div className={styles.stat} style={{ marginBottom: '14px' }}>
        <div className={styles.statLabel}>Last Ingestion</div>
        <div style={{ fontSize: '13px', color: 'var(--color-text)', fontWeight: 500, marginTop: 2 }}>
          {new Date(stats.lastIngestionDate).toLocaleDateString()}
        </div>
      </div>

      {stats.documentsBySourceType.length > 0 && (
        <div className={styles.section}>
          <div className={styles.sectionTitle}>Documents by Source</div>
          <ResponsiveContainer width="100%" height={120}>
            <BarChart data={stats.documentsBySourceType} margin={{ top: 5, right: 10, bottom: 5, left: 10 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
              <XAxis dataKey="sourceType" tick={AXIS_TICK} />
              <YAxis tick={AXIS_TICK} allowDecimals={false} />
              <Tooltip contentStyle={tooltipContentStyle} />
              <Bar dataKey="count" fill={KB_COLORS.primary} radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}

      {stats.mostCitedDocuments.length > 0 && (
        <div className={styles.section}>
          <div className={styles.sectionTitle}>Most Cited</div>
          <div className={styles.citedList}>
            {stats.mostCitedDocuments.slice(0, 5).map((d, i) => (
              <div key={i} className={styles.citedItem}>
                <span className={styles.citedTitle}>{d.title}</span>
                <Badge appearance="filled" style={{ background: 'rgba(6,182,212,0.15)', color: '#67e8f9', fontSize: 10 }}>
                  {d.citations}
                </Badge>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
