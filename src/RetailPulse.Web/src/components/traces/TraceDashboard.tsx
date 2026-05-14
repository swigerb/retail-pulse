import { useState, useMemo } from 'react';
import { Text, Badge, makeStyles } from '@fluentui/react-components';
import { TraceTimeline } from './TraceTimeline';
import type { Trace } from '../../types';

export interface TraceDashboardProps {
  traces: Trace[];
  maxDisplay?: number;
}

const useStyles = makeStyles({
  panel: {
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
  title: {
    fontSize: '16px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  stats: {
    display: 'flex',
    gap: '1px',
    background: 'var(--color-border)',
    borderRadius: '8px',
    overflow: 'hidden',
  },
  stat: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    padding: '12px 8px',
    background: 'var(--color-surface)',
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
    letterSpacing: '0.5px',
    whiteSpace: 'nowrap',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  traceItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '8px 10px',
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '6px',
    cursor: 'pointer',
    transition: 'background 0.2s',
    ':hover': {
      background: 'var(--color-surface-hover)',
    },
  },
  traceItemSelected: {
    borderTopColor: '#6366f1',
    borderRightColor: '#6366f1',
    borderBottomColor: '#6366f1',
    borderLeftColor: '#6366f1',
    background: 'rgba(99, 102, 241, 0.06)',
  },
  traceIntent: {
    flex: 1,
    fontSize: '12px',
    fontWeight: '600',
    color: 'var(--color-text)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  traceAgent: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    maxWidth: '80px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  traceDuration: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    whiteSpace: 'nowrap',
  },
  traceCost: {
    fontSize: '11px',
    color: '#22c55e',
    whiteSpace: 'nowrap',
  },
  traceTime: {
    fontSize: '10px',
    color: 'var(--color-text-subtle)',
    whiteSpace: 'nowrap',
  },
  expandedTrace: {
    padding: '12px',
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '6px',
  },
  toolDistribution: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    marginTop: '4px',
  },
  toolChip: {
    fontSize: '10px',
    color: 'var(--color-text-muted)',
    background: 'rgba(255,255,255,0.06)',
    padding: '2px 8px',
    borderRadius: '4px',
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
});

function formatDuration(ms: number): string {
  if (ms == null || isNaN(ms)) return '0ms';
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)}s`;
  return `${ms.toFixed(0)}ms`;
}

function formatCost(usd: number): string {
  if (usd == null || isNaN(usd)) return '$0.00';
  if (usd === 0) return '$0.00';
  if (usd < 0.01) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(3)}`;
}

export function TraceDashboard({ traces, maxDisplay = 20 }: TraceDashboardProps) {
  const styles = useStyles();
  const [selectedTraceId, setSelectedTraceId] = useState<string | null>(null);

  const recentTraces = useMemo(
    () => [...traces]
      .sort((a, b) => new Date(b.startTime).getTime() - new Date(a.startTime).getTime())
      .slice(0, maxDisplay),
    [traces, maxDisplay],
  );

  const aggregates = useMemo(() => {
    if (traces.length === 0) return { avgDuration: 0, avgCost: 0, toolUsage: new Map<string, number>() };
    const durations = traces.map(t => t.totalDurationMs ?? 0);
    const costs = traces.map(t => t.totalCostUsd ?? 0);
    const toolUsage = new Map<string, number>();
    traces.forEach(t => (t.spans ?? []).forEach(s => {
      if (s?.type === 'tool') {
        toolUsage.set(s.name, (toolUsage.get(s.name) ?? 0) + 1);
      }
    }));
    return {
      avgDuration: durations.reduce((a, b) => a + b, 0) / durations.length,
      avgCost: costs.reduce((a, b) => a + b, 0) / costs.length,
      toolUsage,
    };
  }, [traces]);

  const selectedTrace = recentTraces.find(t => t.traceId === selectedTraceId);

  const toolDistEntries = useMemo(
    () => [...aggregates.toolUsage.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8),
    [aggregates.toolUsage],
  );

  if (traces.length === 0) {
    return (
      <div className={styles.empty} data-testid="trace-dashboard">
        <Text style={{ fontSize: '28px', marginBottom: '8px' }}>🔍</Text>
        <Text>No traces recorded yet</Text>
        <Text style={{ fontSize: '12px', marginTop: '4px', opacity: 0.6 }}>
          Send a message to generate traces
        </Text>
      </div>
    );
  }

  return (
    <div className={styles.panel} data-testid="trace-dashboard">
      <Text className={styles.title}>🔍 Trace Dashboard</Text>

      <div className={styles.stats}>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{traces.length}</Text>
          <Text className={styles.statLabel}>Traces</Text>
        </div>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{formatDuration(aggregates.avgDuration)}</Text>
          <Text className={styles.statLabel}>Avg Duration</Text>
        </div>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{formatCost(aggregates.avgCost)}</Text>
          <Text className={styles.statLabel}>Avg Cost</Text>
        </div>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{aggregates.toolUsage.size}</Text>
          <Text className={styles.statLabel}>Unique Tools</Text>
        </div>
      </div>

      {toolDistEntries.length > 0 && (
        <div>
          <Text style={{ fontSize: '11px', color: 'var(--color-text-subtle)', fontWeight: 600, marginBottom: '4px', display: 'block' }}>
            Tool Usage Distribution
          </Text>
          <div className={styles.toolDistribution}>
            {toolDistEntries.map(([name, count]) => (
              <span key={name} className={styles.toolChip}>
                🔧 {name} ({count})
              </span>
            ))}
          </div>
        </div>
      )}

      <div className={styles.list}>
        {recentTraces.map(trace => (
          <div
            key={trace.traceId}
            className={`${styles.traceItem} ${selectedTraceId === trace.traceId ? styles.traceItemSelected : ''}`}
            onClick={() => setSelectedTraceId(prev => prev === trace.traceId ? null : trace.traceId)}
            role="button"
            tabIndex={0}
            onKeyDown={e => { if (e.key === 'Enter') setSelectedTraceId(prev => prev === trace.traceId ? null : trace.traceId); }}
          >
            <span className={styles.traceTime}>
              {new Date(trace.startTime).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })}
            </span>
            <span className={styles.traceIntent}>{trace.intent || 'Unknown intent'}</span>
            <span className={styles.traceAgent}>{trace.agentName}</span>
            <span className={styles.traceDuration}>{formatDuration(trace.totalDurationMs)}</span>
            <span className={styles.traceCost}>{formatCost(trace.totalCostUsd)}</span>
            <Badge appearance="filled" color={trace.status === 'completed' ? 'success' : trace.status === 'error' ? 'danger' : 'warning'} style={{ fontSize: '9px' }}>
              {(trace.spans ?? []).length}
            </Badge>
          </div>
        ))}
      </div>

      {selectedTrace && (
        <div className={styles.expandedTrace}>
          <TraceTimeline trace={selectedTrace} />
        </div>
      )}
    </div>
  );
}
