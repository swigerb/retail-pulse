import { useState, useEffect } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { fetchKBStats } from '../../services/knowledgeApi';
import type { KBStats } from '../../types';

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
          <div className={styles.statValue}>{stats.documentCount}</div>
          <div className={styles.statLabel}>Documents</div>
        </div>
        <div className={styles.stat}>
          <div className={styles.statValue}>{stats.chunkCount}</div>
          <div className={styles.statLabel}>Chunks</div>
        </div>
      </div>

      <div className={styles.stat}>
        <div className={styles.statValue}>{stats.averageChunksPerDocument}</div>
        <div className={styles.statLabel}>Avg Chunks / Doc</div>
      </div>
    </div>
  );
}
