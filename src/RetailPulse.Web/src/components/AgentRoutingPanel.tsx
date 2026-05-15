import { useMemo } from 'react';
import { Text, Badge, makeStyles } from '@fluentui/react-components';
import type { RoutingInfo, IntentCategory } from '../types';
import { getIntentCategory } from '../types';
import { AGENT_COLORS, AGENT_EMOJIS, AGENT_LABELS } from '../constants/agentRouting';

interface AgentRoutingPanelProps {
  routingHistory: RoutingInfo[];
}

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
    backgroundColor: 'var(--color-bg-elevated)',
    borderRadius: '8px',
    border: '1px solid var(--color-border)',
  },
  title: {
    fontSize: '14px',
    fontWeight: '700',
    color: 'var(--color-text)',
    letterSpacing: '0.3px',
  },
  statsRow: {
    display: 'flex',
    gap: '1px',
    backgroundColor: 'var(--color-border)',
    borderRadius: '8px',
    overflow: 'hidden',
  },
  stat: {
    flex: '1',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    padding: '12px 8px',
    backgroundColor: 'var(--color-bg-elevated)',
    gap: '2px',
  },
  statValue: {
    fontSize: '18px',
    fontWeight: '700',
    color: 'var(--brand-accent)',
  },
  statLabel: {
    fontSize: '10px',
    color: 'var(--color-text-subtle)',
    textTransform: 'uppercase',
    letterSpacing: '0.8px',
    whiteSpace: 'nowrap',
  },
  chartContainer: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  barRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  barLabel: {
    width: '100px',
    fontSize: '12px',
    fontWeight: '500',
    color: 'var(--color-text)',
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    flexShrink: '0',
  },
  barTrack: {
    flex: '1',
    height: '20px',
    borderRadius: '4px',
    backgroundColor: 'rgba(255,255,255,0.04)',
    overflow: 'hidden',
    position: 'relative',
  },
  barFill: {
    height: '100%',
    borderRadius: '4px',
    transition: 'width 0.4s ease',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    paddingRight: '6px',
    minWidth: '24px',
  },
  barCount: {
    fontSize: '10px',
    fontWeight: '700',
    color: '#fff',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    padding: '32px 16px',
    textAlign: 'center',
    color: 'var(--color-text-subtle)',
  },
  emptyText: {
    fontSize: '13px',
    marginTop: '8px',
  },
});

interface AgentStat {
  category: IntentCategory;
  count: number;
  avgConfidence: number;
}

export function AgentRoutingPanel({ routingHistory }: AgentRoutingPanelProps) {
  const styles = useStyles();

  const stats = useMemo<AgentStat[]>(() => {
    const map = new Map<IntentCategory, { count: number; totalConf: number }>();
    for (const r of routingHistory) {
      const cat = getIntentCategory(r.intent);
      const entry = map.get(cat) ?? { count: 0, totalConf: 0 };
      entry.count++;
      entry.totalConf += r.confidence;
      map.set(cat, entry);
    }
    return Array.from(map.entries())
      .map(([category, { count, totalConf }]) => ({
        category,
        count,
        avgConfidence: totalConf / count,
      }))
      .sort((a, b) => b.count - a.count);
  }, [routingHistory]);

  const totalQueries = routingHistory.length;
  const avgConfidence = totalQueries > 0
    ? routingHistory.reduce((sum, r) => sum + r.confidence, 0) / totalQueries
    : 0;
  const fallbackCount = routingHistory.filter(r => getIntentCategory(r.intent) === 'general').length;
  const fallbackRate = totalQueries > 0 ? fallbackCount / totalQueries : 0;
  const maxCount = stats.length > 0 ? stats[0].count : 1;

  if (totalQueries === 0) {
    return (
      <div className={styles.panel}>
        <Text className={styles.title}>🔀 Agent Routing</Text>
        <div className={styles.emptyState}>
          <span style={{ fontSize: '28px' }}>🔮</span>
          <Text className={styles.emptyText}>
            Routing statistics will appear as queries are processed.
          </Text>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.panel}>
      <Text className={styles.title}>🔀 Agent Routing</Text>

      <div className={styles.statsRow}>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{totalQueries}</Text>
          <Text className={styles.statLabel}>Queries</Text>
        </div>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{Math.round(avgConfidence * 100)}%</Text>
          <Text className={styles.statLabel}>Avg Confidence</Text>
        </div>
        <div className={styles.stat}>
          <Badge
            appearance="filled"
            color={fallbackRate > 0.3 ? 'warning' : 'success'}
            style={{ fontSize: '18px', fontWeight: 700 }}
          >
            {Math.round(fallbackRate * 100)}%
          </Badge>
          <Text className={styles.statLabel}>Fallback Rate</Text>
        </div>
      </div>

      <div className={styles.chartContainer}>
        {stats.map(({ category, count, avgConfidence: catConf }) => (
          <div key={category} className={styles.barRow}>
            <span className={styles.barLabel}>
              <span>{AGENT_EMOJIS[category]}</span>
              {AGENT_LABELS[category]}
            </span>
            <div className={styles.barTrack}>
              <div
                className={styles.barFill}
                style={{
                  width: `${(count / maxCount) * 100}%`,
                  backgroundColor: AGENT_COLORS[category],
                }}
                title={`${count} queries · ${Math.round(catConf * 100)}% avg confidence`}
              >
                <span className={styles.barCount}>{count}</span>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
