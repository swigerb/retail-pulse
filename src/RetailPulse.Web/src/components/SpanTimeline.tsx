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
};

const spanIcons: Record<string, string> = {
  thought: '🧠',
  tool_call: '🔧',
  tool_result: '📊',
  response: '💬',
  agent_delegation: '🤝',
  agent_call: '🏭',
  agent_response: '📋',
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
    color: '#666666',
  },
  emptyText: {
    fontSize: '14px',
    marginBottom: '4px',
  },
  emptyHint: {
    fontSize: '12px',
    color: '#666666',
    opacity: '0.6',
  },
  item: {
    background: '#1A1A1A',
    border: '1px solid rgba(255, 255, 255, 0.08)',
    borderRadius: '6px',
    padding: '10px 12px',
    transition: 'background 0.2s ease',
    ':hover': {
      background: '#242424',
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
    color: '#F5F5F0',
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
    color: '#A0A0A0',
    background: 'rgba(255, 255, 255, 0.04)',
    padding: '1px 6px',
    borderRadius: '4px',
  },
  detail: {
    fontSize: '12px',
    color: '#A0A0A0',
    lineHeight: '1.4',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    display: '-webkit-box',
    WebkitLineClamp: '2',
    WebkitBoxOrient: 'vertical',
  },
  time: {
    fontSize: '10px',
    color: '#666666',
    marginTop: '4px',
  },
});

export function SpanTimeline({ spans }: Props) {
  const styles = useStyles();

  return (
    <div className={styles.timeline}>
      {spans.length === 0 && (
        <div className={styles.empty}>
          <Text className={styles.emptyText}>Waiting for agent activity...</Text>
          <Text className={styles.emptyHint}>Send a message to see real-time telemetry</Text>
        </div>
      )}
      {spans.map((span, i) => (
        <div
          key={i}
          className={styles.item}
          style={{ borderLeftColor: spanColors[span.type] || '#666', borderLeftWidth: '3px' }}
        >
          <div className={styles.header}>
            <span className={styles.icon}>{spanIcons[span.type] || '⚡'}</span>
            <span className={styles.name}>{span.name}</span>
            <Badge
              className={styles.type}
              appearance="filled"
              style={{ color: spanColors[span.type] }}
            >
              {span.type}
            </Badge>
            {span.durationMs > 0 && (
              <span className={styles.duration}>{span.durationMs.toFixed(0)}ms</span>
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
