import { useState } from 'react';
import { Text, makeStyles } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronUp16Regular } from '@fluentui/react-icons';
import { TraceTimeline } from './TraceTimeline';
import type { Trace } from '../../types';

export interface TraceCardProps {
  trace: Trace;
}

const useStyles = makeStyles({
  card: {
    borderRadius: '6px',
    overflow: 'hidden',
    marginTop: '4px',
  },
  summary: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 8px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '4px',
    cursor: 'pointer',
    fontSize: '12px',
    color: 'var(--color-text-subtle)',
    transition: 'background 0.2s',
    border: 'none',
    width: '100%',
    textAlign: 'left',
    ':hover': {
      background: 'rgba(255,255,255,0.06)',
    },
  },
  summaryText: {
    flex: 1,
    fontSize: '11px',
    color: 'var(--color-text-muted)',
  },
  chevron: {
    color: 'var(--color-text-subtle)',
    fontSize: '12px',
  },
  expanded: {
    padding: '12px',
    background: 'rgba(255,255,255,0.02)',
    borderTop: '1px solid var(--color-border)',
  },
  steps: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    marginBottom: '8px',
    paddingBottom: '8px',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
  },
  step: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    padding: '2px 0',
  },
  stepIcon: {
    fontSize: '12px',
    flexShrink: 0,
  },
  stepName: {
    flex: 1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  stepDuration: {
    color: 'var(--color-text-subtle)',
    fontSize: '10px',
    flexShrink: 0,
  },
});

const STEP_ICONS: Record<string, string> = {
  routing: '🔀',
  agent: '🤖',
  tool: '🔧',
  memory: '🧠',
  approval: '✅',
};

function formatDuration(ms: number): string {
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${ms.toFixed(0)}ms`;
}

function formatCost(usd: number): string {
  if (usd === 0) return '$0.000';
  if (usd < 0.01) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(3)}`;
}

export function TraceCard({ trace }: TraceCardProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);

  const toolCount = (trace.spans ?? []).filter(s => s?.type === 'tool').length;
  const summaryLine = `⚡ ${formatDuration(trace.totalDurationMs)} · ${toolCount} tool${toolCount !== 1 ? 's' : ''} · ${formatCost(trace.totalCostUsd)}`;

  return (
    <div className={styles.card} data-testid="trace-card">
      <button
        className={styles.summary}
        onClick={() => setExpanded(prev => !prev)}
        aria-expanded={expanded}
        aria-label="How I got this answer"
      >
        <Text className={styles.summaryText}>{summaryLine}</Text>
        {expanded ? (
          <ChevronUp16Regular className={styles.chevron} />
        ) : (
          <ChevronDown16Regular className={styles.chevron} />
        )}
      </button>

      {expanded && (
        <div className={styles.expanded} data-testid="trace-card-expanded">
          <div className={styles.steps}>
            <Text style={{ fontSize: '11px', fontWeight: 600, color: 'var(--color-text)', marginBottom: '4px' }}>
              Steps taken:
            </Text>
            {trace.spans.map((span) => (
              <div key={span.id} className={styles.step}>
                <span className={styles.stepIcon}>{STEP_ICONS[span.type] || '⚡'}</span>
                <span className={styles.stepName}>{span.name}</span>
                <span className={styles.stepDuration}>{formatDuration(span.durationMs)}</span>
              </div>
            ))}
          </div>
          <TraceTimeline trace={trace} />
        </div>
      )}
    </div>
  );
}
