import { useMemo } from 'react';
import { Text, Badge, Tooltip, makeStyles } from '@fluentui/react-components';
import type { TraceSpan, Trace } from '../../types';

export interface TraceTimelineProps {
  trace: Trace;
}

const SPAN_TYPE_COLORS: Record<string, string> = {
  routing: '#6366f1',
  agent: '#a855f7',
  tool: '#3b82f6',
  memory: '#22c55e',
  approval: '#f59e0b',
};

const SPAN_TYPE_ICONS: Record<string, string> = {
  routing: '🔀',
  agent: '🤖',
  tool: '🔧',
  memory: '🧠',
  approval: '✅',
};

const useStyles = makeStyles({
  timeline: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    fontFamily: "'JetBrains Mono', 'Cascadia Code', 'Fira Code', monospace",
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '8px 0',
    marginBottom: '4px',
    borderBottom: '1px solid var(--color-border)',
    flexWrap: 'wrap',
    gap: '8px',
  },
  headerLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  traceTitle: {
    fontSize: '14px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  statusBadge: {
    fontSize: '10px',
  },
  headerStats: {
    display: 'flex',
    gap: '12px',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    flexWrap: 'wrap',
  },
  statItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '0',
    height: '28px',
    transition: 'background 0.15s',
    borderRadius: '4px',
    ':hover': {
      background: 'rgba(255,255,255,0.04)',
    },
  },
  labelCol: {
    width: '180px',
    minWidth: '180px',
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    paddingRight: '8px',
    overflow: 'hidden',
  },
  indent: {
    flexShrink: 0,
  },
  icon: {
    fontSize: '12px',
    flexShrink: 0,
  },
  spanName: {
    fontSize: '11px',
    color: 'var(--color-text)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  barCol: {
    flex: 1,
    position: 'relative',
    height: '18px',
    display: 'flex',
    alignItems: 'center',
  },
  bar: {
    height: '14px',
    borderRadius: '3px',
    minWidth: '4px',
    position: 'relative',
    transition: 'width 0.3s ease',
  },
  durationCol: {
    width: '70px',
    minWidth: '70px',
    textAlign: 'right',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    paddingLeft: '8px',
  },
  tokensCol: {
    width: '70px',
    minWidth: '70px',
    textAlign: 'right',
    fontSize: '10px',
    color: 'var(--color-text-subtle)',
    paddingLeft: '4px',
  },
  footer: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingTop: '8px',
    marginTop: '4px',
    borderTop: '1px solid var(--color-border)',
    fontSize: '12px',
  },
  footerCost: {
    color: '#22c55e',
    fontWeight: '600',
  },
  footerDuration: {
    color: 'var(--color-text-muted)',
  },
  empty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '32px',
    textAlign: 'center',
    color: 'var(--color-text-subtle)',
  },
  legend: {
    display: 'flex',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '8px 0',
    marginBottom: '4px',
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: '10px',
    color: 'var(--color-text-muted)',
  },
  legendDot: {
    width: '8px',
    height: '8px',
    borderRadius: '2px',
  },
});

interface FlatSpan extends TraceSpan {
  depth: number;
}

function flattenSpans(spans: TraceSpan[]): FlatSpan[] {
  const validSpans = spans.filter((s): s is TraceSpan => s != null && s.id != null);
  const byParent = new Map<string, TraceSpan[]>();
  for (const s of validSpans) {
    const key = s.parentId || '__root__';
    if (!byParent.has(key)) byParent.set(key, []);
    byParent.get(key)!.push(s);
  }

  const result: FlatSpan[] = [];
  function walk(parentId: string, depth: number) {
    const children = byParent.get(parentId) || [];
    children.sort((a, b) => new Date(a.startTime).getTime() - new Date(b.startTime).getTime());
    for (const child of children) {
      result.push({ ...child, depth });
      walk(child.id, depth + 1);
    }
  }
  walk('__root__', 0);
  return result;
}

function formatDuration(ms: number): string {
  if (ms == null || isNaN(ms)) return '0ms';
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)}s`;
  return `${ms.toFixed(0)}ms`;
}

function formatTokens(count: number): string {
  if (count == null || isNaN(count)) return '0';
  if (count >= 1000) return `${(count / 1000).toFixed(1)}K`;
  return count.toString();
}

function formatCost(usd: number): string {
  if (usd == null || isNaN(usd)) return '$0.00';
  if (usd === 0) return '$0.00';
  if (usd < 0.01) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(3)}`;
}

export function TraceTimeline({ trace }: TraceTimelineProps) {
  const styles = useStyles();
  const spans = trace.spans ?? [];

  const flatSpans = useMemo(() => flattenSpans(spans), [spans]);

  const traceStart = useMemo(() => {
    if (spans.length === 0) return 0;
    return Math.min(...spans.filter(s => s != null).map(s => new Date(s.startTime).getTime()));
  }, [spans]);

  const totalMs = trace.totalDurationMs || Math.max(1, ...spans.filter(s => s != null).map(s => {
    const offset = new Date(s.startTime).getTime() - traceStart;
    return offset + (s.durationMs ?? 0);
  }));

  if (spans.length === 0) {
    return (
      <div className={styles.empty} data-testid="trace-timeline">
        <Text>No spans recorded for this trace</Text>
      </div>
    );
  }

  return (
    <div className={styles.timeline} data-testid="trace-timeline">
      <div className={styles.header}>
        <div className={styles.headerLeft}>
          <Text className={styles.traceTitle}>⚡ {trace.intent || 'Trace'}</Text>
          <Badge
            appearance="filled"
            color={trace.status === 'completed' ? 'success' : trace.status === 'error' ? 'danger' : 'warning'}
            className={styles.statusBadge}
          >
            {trace.status}
          </Badge>
        </div>
        <div className={styles.headerStats}>
          <span className={styles.statItem}>🤖 {trace.model
            || (trace.spans ?? [])
                .find(s => s.type === 'agent' && s.attributes?.['llm.model'])
                ?.attributes?.['llm.model']
            || trace.agentName
            || 'Unknown'}</span>
          <span className={styles.statItem}>⏱️ {formatDuration(trace.totalDurationMs)}</span>
          <span className={styles.statItem}>🪙 {formatTokens(trace.totalTokens)}</span>
        </div>
      </div>

      <div className={styles.legend}>
        {Object.entries(SPAN_TYPE_COLORS).map(([type, color]) => (
          <span key={type} className={styles.legendItem}>
            <span className={styles.legendDot} style={{ background: color }} />
            {type}
          </span>
        ))}
      </div>

      {flatSpans.map((span) => {
        const offset = new Date(span.startTime).getTime() - traceStart;
        const leftPct = Math.max(0, (offset / totalMs) * 100);
        const widthPct = Math.max(0.5, (span.durationMs / totalMs) * 100);
        const color = SPAN_TYPE_COLORS[span.type] || '#6b7280';
        const tokens = (span.inputTokens ?? 0) + (span.outputTokens ?? 0);

        const tooltipContent = [
          span.name,
          `Duration: ${formatDuration(span.durationMs)}`,
          tokens > 0 ? `Tokens: ${tokens.toLocaleString()}` : null,
          span.estimatedCostUsd ? `Cost: ${formatCost(span.estimatedCostUsd)}` : null,
          ...(Object.entries(span.attributes || {}).map(([k, v]) => `${k}: ${v}`)),
        ].filter(Boolean).join('\n');

        return (
          <Tooltip
            key={span.id}
            content={<span style={{ whiteSpace: 'pre-line', fontSize: '11px' }}>{tooltipContent}</span>}
            relationship="description"
            positioning="above"
          >
            <div className={styles.row} data-testid="trace-span-row">
              <div className={styles.labelCol}>
                <span className={styles.indent} style={{ width: `${span.depth * 16}px` }} />
                <span className={styles.icon}>{SPAN_TYPE_ICONS[span.type] || '⚡'}</span>
                <span className={styles.spanName}>{span.name}</span>
              </div>
              <div className={styles.barCol}>
                <div
                  className={styles.bar}
                  style={{
                    marginLeft: `${leftPct}%`,
                    width: `${widthPct}%`,
                    background: `${color}cc`,
                    boxShadow: `0 0 4px ${color}40`,
                  }}
                  data-testid="trace-span-bar"
                />
              </div>
              <div className={styles.durationCol}>
                {formatDuration(span.durationMs)}
              </div>
              <div className={styles.tokensCol}>
                {tokens > 0 ? `🪙 ${formatTokens(tokens)}` : ''}
              </div>
            </div>
          </Tooltip>
        );
      })}

      <div className={styles.footer}>
        <span className={styles.footerDuration}>
          Total: {formatDuration(trace.totalDurationMs)} · {spans.length} spans
        </span>
        <span className={styles.footerCost}>
          💰 {formatCost(trace.totalCostUsd)}
        </span>
      </div>
    </div>
  );
}
