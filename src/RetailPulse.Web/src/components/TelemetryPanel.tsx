import { useState, useEffect, useRef } from 'react';
import { Button, Text, Badge, makeStyles } from '@fluentui/react-components';
import { Dismiss24Regular } from '@fluentui/react-icons';
import type { AgentSpan } from '../types';
import { SpanTimeline } from './SpanTimeline';
import { connectTelemetryHub, disconnectTelemetryHub } from '../services/telemetryHub';

interface Props {
  resetKey?: number;
  onClose?: () => void;
}

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    backgroundColor: '#0D0D0D',
    overflow: 'hidden',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '16px 20px',
    borderBottom: '1px solid rgba(255, 255, 255, 0.08)',
    flexShrink: '0',
  },
  headerTitle: {
    fontSize: '16px',
    fontWeight: '600',
    color: '#F5F5F0',
  },
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  stats: {
    display: 'flex',
    gap: '1px',
    backgroundColor: 'rgba(255, 255, 255, 0.08)',
    borderBottom: '1px solid rgba(255, 255, 255, 0.08)',
    flexShrink: '0',
  },
  stat: {
    flex: '1',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    padding: '14px 8px',
    backgroundColor: '#0D0D0D',
    gap: '2px',
  },
  statValue: {
    fontSize: '20px',
    fontWeight: '700',
    color: '#C8A951',
  },
  statLabel: {
    fontSize: '10px',
    color: '#666666',
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
      background: 'rgba(255, 255, 255, 0.1)',
      borderRadius: '2px',
    },
  },
  clearButton: {
    margin: '12px',
    flexShrink: '0',
  },
});

export function TelemetryPanel({ resetKey, onClose }: Props) {
  const [connected, setConnected] = useState(false);
  const [liveSpans, setLiveSpans] = useState<AgentSpan[]>([]);
  const prevResetKey = useRef(resetKey);
  const styles = useStyles();

  useEffect(() => {
    connectTelemetryHub(
      (span) => setLiveSpans(prev => [...prev, span]),
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
      <div className={styles.header}>
        <Text className={styles.headerTitle}>📡 Real-Time Telemetry</Text>
        <div className={styles.headerActions}>
          <Badge
            appearance="filled"
            color={connected ? 'success' : 'danger'}
          >
            {connected ? '🟢 Live' : '🔴 Disconnected'}
          </Badge>
          {onClose && (
            <Button
              appearance="subtle"
              icon={<Dismiss24Regular />}
              onClick={onClose}
            />
          )}
        </div>
      </div>

      <div className={styles.stats}>
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
