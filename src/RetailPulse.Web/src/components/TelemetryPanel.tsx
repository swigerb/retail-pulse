import { useState, useEffect, useRef } from 'react';
import { Button, Text, Badge, makeStyles } from '@fluentui/react-components';
import type { AgentSpan } from '../types';
import { SpanTimeline } from './SpanTimeline';
import { connectTelemetryHub, disconnectTelemetryHub } from '../services/telemetryHub';

interface Props {
  resetKey?: number;
}

const MAX_RETAINED_SPANS = 500;

const useStyles = makeStyles({
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

export function TelemetryPanel({ resetKey }: Props) {
  const [connected, setConnected] = useState(false);
  const [liveSpans, setLiveSpans] = useState<AgentSpan[]>([]);
  const prevResetKey = useRef(resetKey);
  const styles = useStyles();

  useEffect(() => {
    connectTelemetryHub(
      (span) => setLiveSpans(prev => {
        const next = [...prev, span];
        if (next.length > MAX_RETAINED_SPANS) {
          return next.slice(next.length - MAX_RETAINED_SPANS);
        }
        return next;
      }),
      () => setConnected(true),
      () => setConnected(false),
    );

    return () => { disconnectTelemetryHub(); };
  }, []);

  // Clear spans when a new chat starts
  useEffect(() => {
    if (prevResetKey.current !== resetKey) {
      setLiveSpans([]);
      prevResetKey.current = resetKey;
    }
  }, [resetKey]);

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
          onClick={() => setLiveSpans([])}
        >
          Clear
        </Button>
      )}
    </div>
  );
}
