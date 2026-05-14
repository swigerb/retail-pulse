import { useMemo } from 'react';
import { Text, Badge, makeStyles } from '@fluentui/react-components';
import type { AgentSpan } from '../types';

interface Props {
  spans: AgentSpan[];
}

const spanColors: Record<string, string> = {
  thought: '#6366f1',
  tool_call: '#f59e0b',
  tool_result: '#10b981',
  response: '#3b82f6',
  agent_delegation: '#c084fc',
  agent_call: '#a855f7',
  agent_response: '#8b5cf6',
  routing: '#06b6d4',
};

const spanIcons: Record<string, string> = {
  thought: '🧠',
  tool_call: '🔧',
  tool_result: '📥',
  response: '💬',
  agent_delegation: '🤝',
  agent_call: '🏭',
  agent_response: '📋',
  routing: '🔀',
};

const useStyles = makeStyles({
  timeline: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
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
  emptyText: {
    fontSize: '14px',
    marginBottom: '4px',
  },
  emptyHint: {
    fontSize: '12px',
    color: 'var(--color-text-subtle)',
    opacity: '0.6',
  },
  item: {
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '6px',
    padding: '10px 12px',
    transition: 'background 0.2s ease',
    borderLeftWidth: '3px',
    ':hover': {
      background: 'var(--color-surface-hover)',
    },
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    marginBottom: '4px',
    flexWrap: 'wrap',
  },
  icon: {
    fontSize: '14px',
  },
  name: {
    fontSize: '13px',
    fontWeight: '600',
    color: 'var(--color-text)',
    flex: '1',
    minWidth: '0',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  type: {
    fontSize: '10px',
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    padding: '2px 6px',
    borderRadius: '4px',
    background: 'rgba(255, 255, 255, 0.05)',
  },
  duration: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    background: 'rgba(255, 255, 255, 0.04)',
    padding: '1px 6px',
    borderRadius: '4px',
  },
  detail: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.4',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    display: '-webkit-box',
    WebkitLineClamp: '2',
    WebkitBoxOrient: 'vertical',
  },
  time: {
    fontSize: '10px',
    color: 'var(--color-text-subtle)',
    marginTop: '4px',
  },
});

export function SpanTimeline({ spans }: Props) {
  const styles = useStyles();
  const validSpans = useMemo(() => spans.filter((s): s is AgentSpan => s != null && s.type != null), [spans]);
  const itemStyles = useMemo(
    () => validSpans.map((span) => ({ borderLeftColor: spanColors[span.type] || '#666' })),
    [validSpans],
  );
  const badgeStyles = useMemo(
    () => validSpans.map((span) => ({ color: spanColors[span.type] })),
    [validSpans],
  );

  return (
    <div className={styles.timeline}>
      {validSpans.length === 0 && (
        <div className={styles.empty}>
          <Text className={styles.emptyText}>Waiting for agent activity...</Text>
          <Text className={styles.emptyHint}>Send a message to see real-time telemetry</Text>
        </div>
      )}
      {validSpans.map((span, i) => (
        <div
          key={`span-${span.name}-${span.timestamp}`}
          className={styles.item}
          style={itemStyles[i]}
        >
          <div className={styles.header}>
            <span className={styles.icon}>{spanIcons[span.type] || '⚡'}</span>
            <span className={styles.name}>{span.name}</span>
            <Badge
              className={styles.type}
              appearance="filled"
              style={badgeStyles[i]}
            >
              {span.type}
            </Badge>
            {span.durationMs > 0 && (
              <span className={styles.duration}>{span.durationMs.toFixed(0)}ms</span>
            )}
            {(span.inputTokens || span.outputTokens) && (
              <span className={styles.duration} title={`In: ${span.inputTokens ?? 0} / Out: ${span.outputTokens ?? 0}`}>
                🪙 {((span.inputTokens ?? 0) + (span.outputTokens ?? 0)).toLocaleString()}
              </span>
            )}
          </div>
          <Text className={styles.detail}>{span.detail}</Text>
          <Text className={styles.time}>
            {new Date(span.timestamp).toLocaleTimeString()}
          </Text>
        </div>
      ))}
    </div>
  );
}
