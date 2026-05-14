import { Button, Text, makeStyles } from '@fluentui/react-components';
import type { AgentSpan, TokenUsage } from '../types';
import { SpanTimeline } from './SpanTimeline';

interface Props {
  connected?: boolean;
  liveSpans: AgentSpan[];
  totalDurationMs?: number;
  totalTokenUsage?: TokenUsage;
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
    padding: '14px 6px',
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
    whiteSpace: 'nowrap',
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

export function TelemetryPanel({ liveSpans, totalDurationMs, totalTokenUsage, onClear }: Props) {
  const styles = useStyles();

  const totalDuration = totalDurationMs ?? liveSpans.reduce((sum, s) => sum + (s?.durationMs ?? 0), 0);
  const toolCalls = liveSpans.filter(s => s?.type === 'tool_call').length;
  const agentCalls = liveSpans.filter(s => s?.type === 'agent_delegation' || s?.type === 'agent_call').length;
  const routingSpans = liveSpans.filter(s => s?.type === 'routing').length;
  const totalTokens = totalTokenUsage?.totalTokens ?? 0;
  const totalCost = totalTokenUsage?.estimatedCostUsd ?? 0;

  const formatDuration = (ms: number) => {
    if (ms >= 1000) return `${(ms / 1000).toFixed(2)}s`;
    return `${ms.toFixed(0)}ms`;
  };

  const formatTokens = (count: number) => {
    if (count >= 1_000_000) return `${(count / 1_000_000).toFixed(1)}M`;
    if (count >= 1_000) return `${(count / 1_000).toFixed(1)}K`;
    return count.toString();
  };

  const formatCost = (usd: number) => {
    if (usd === 0) return '$0.00';
    if (usd < 0.01) return `$${usd.toFixed(4)}`;
    return `$${usd.toFixed(2)}`;
  };

  return (
    <div className={styles.panel}>
      <div className={styles.stats}>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{formatTokens(totalTokens)}</Text>
          <Text className={styles.statLabel}>Total Tokens</Text>
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
        {routingSpans > 0 && (
          <div className={styles.stat}>
            <Text className={styles.statValue}>{routingSpans}</Text>
            <Text className={styles.statLabel}>Routing</Text>
          </div>
        )}
        <div className={styles.stat}>
          <Text className={styles.statValue}>{formatDuration(totalDuration)}</Text>
          <Text className={styles.statLabel}>Total Duration</Text>
        </div>
        <div className={styles.stat}>
          <Text className={styles.statValue}>{formatCost(totalCost)}</Text>
          <Text className={styles.statLabel}>Total Cost</Text>
        </div>
      </div>

      <div className={styles.spans}>
        <SpanTimeline spans={liveSpans} />
      </div>

      {liveSpans.length > 0 && (
        <Button
          appearance="outline"
          className={styles.clearButton}
          onClick={onClear}
          icon={<span>🗑️</span>}
        >
          Clear Telemetry
        </Button>
      )}
    </div>
  );
}
