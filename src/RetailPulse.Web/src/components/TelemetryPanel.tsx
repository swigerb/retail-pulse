import { Button, Text, Badge, makeStyles } from '@fluentui/react-components';
import type { AgentSpan } from '../types';
import { SpanTimeline } from './SpanTimeline';

interface Props {
  connected: boolean;
  liveSpans: AgentSpan[];
  onClear: () => void;
}

const useStyles= makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    backgroundColor: 'var(--color-bg-elevated)',
    overflow: 'hidden',
  },
  stats: {
    display: 'flex',
    gap: '1px',
    backgroundColor: 'var(--color-border)',
    borderBottom: '1px solid var(--color-border)',
    flexShrink: '0',
  },
  stat: {
    flex: '1',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    padding: '14px 8px',
    backgroundColor: 'var(--color-bg-elevated)',
    gap: '2px',
  },
  statValue: {
    fontSize: '20px',
    fontWeight: '700',
    color: 'var(--brand-accent)',
  },
  statLabel: {
    fontSize: '10px',
    color: 'var(--color-text-subtle)',
    textTransform: 'uppercase',
    letterSpacing: '1px',
  },
  spans: {
    flex: '1',
    overflowY: 'auto',
    padding: '12px',
    '::-webkit-scrollbar': {
      width: '4px',
    },
    '::-webkit-scrollbar-track': {
      background: 'transparent',
    },
    '::-webkit-scrollbar-thumb': {
      background: 'var(--color-border)',
      borderRadius: '2px',
    },
  },
  clearButton: {
    margin: '12px',
    flexShrink: '0',
  },
});

export function TelemetryPanel({ connected, liveSpans, onClear }: Props) {
  const styles = useStyles();

  const totalDuration = liveSpans.reduce((sum, s) => sum + s.durationMs, 0);
  const toolCalls = liveSpans.filter(s => s.type === 'tool_call').length;
  const agentCalls = liveSpans.filter(s => s.type === 'agent_delegation' || s.type === 'agent_call').length;

  return (
    <div className={styles.panel}>
      <div className={styles.stats}>
        <div className={styles.stat}>
          <Badge
            appearance="filled"
            color={connected ? 'success' : 'danger'}
          >
            {connected ? '🟢 Live' : '🔴 Disconnected'}
          </Badge>
          <Text className={styles.statLabel}>Status</Text>
        </div>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{liveSpans.length}</Text>
          <Text className={styles.statLabel}>Spans</Text>
        </div>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{toolCalls}</Text>
          <Text className={styles.statLabel}>Tool Calls</Text>
        </div>
        {agentCalls > 0 && (
          <div className={styles.stat}>
            <Text className={styles.statValue}>{agentCalls}</Text>
            <Text className={styles.statLabel}>Agent Calls</Text>
          </div>
        )}
        <div className={styles.stat}>
          <Text className={styles.statValue}>{totalDuration.toFixed(0)}ms</Text>
          <Text className={styles.statLabel}>Total Duration</Text>
        </div>
      </div>

      <div className={styles.spans}>
        <SpanTimeline spans={liveSpans} />
      </div>

      {liveSpans.length > 0 && (
        <Button
          appearance="subtle"
          className={styles.clearButton}
          onClick={onClear}
        >
          Clear
        </Button>
      )}
    </div>
  );
}
