import { useMemo } from 'react';
import { Button, Text, makeStyles } from '@fluentui/react-components';
import type { AgentSpan, TelemetryTurn, TokenUsage } from '../types';
import { SpanTimeline } from './SpanTimeline';
import { TelemetryTurnGroup } from './TelemetryTurnGroup';

interface Props {
  connected?: boolean;
  liveSpans: AgentSpan[];
  /**
   * Ordered per-user-prompt turn markers (issue #303). When present, the
   * panel groups `liveSpans` under these headers with visible boundaries
   * and marks the last entry as `aria-current`. Absent for isolated
   * component tests that only care about totals + the flat timeline.
   */
  turns?: TelemetryTurn[];
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
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
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
  preTurnHeader: {
    fontSize: '10px',
    fontWeight: '700',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    color: 'var(--color-text-subtle)',
    marginBottom: '6px',
  },
  clearButton: {
    margin: '12px',
    flexShrink: '0',
  },
});

export function TelemetryPanel({ liveSpans, turns, totalDurationMs, totalTokenUsage, onClear }: Props) {
  const styles = useStyles();

  const totalDuration = totalDurationMs ?? liveSpans.reduce((sum, s) => sum + (s?.durationMs ?? 0), 0);
  const toolCalls = liveSpans.filter(s => s?.type === 'tool_call').length;
  const agentCalls = liveSpans.filter(s => s?.type === 'agent_delegation' || s?.type === 'agent_call').length;
  const routingSpans = liveSpans.filter(s => s?.type === 'routing').length;
  const totalTokens = totalTokenUsage?.totalTokens ?? 0;
  const totalCost = totalTokenUsage?.estimatedCostUsd ?? 0;

  // Group spans by turnId in a single pass so we don't re-scan liveSpans
  // once per turn. Spans without a turnId (arrived before the first prompt
  // or from a legacy code path) land in a dedicated pre-turn bucket so
  // nothing goes missing — the flat total still equals liveSpans.length.
  const { spansByTurn, preTurnSpans } = useMemo(() => {
    const byTurn = new Map<string, AgentSpan[]>();
    const preTurn: AgentSpan[] = [];
    for (const span of liveSpans) {
      if (!span) continue;
      if (span.turnId) {
        const bucket = byTurn.get(span.turnId);
        if (bucket) bucket.push(span);
        else byTurn.set(span.turnId, [span]);
      } else {
        preTurn.push(span);
      }
    }
    return { spansByTurn: byTurn, preTurnSpans: preTurn };
  }, [liveSpans]);

  const hasTurns = (turns?.length ?? 0) > 0;
  const currentTurnId = hasTurns ? turns![turns!.length - 1].id : null;

  const formatDuration = (ms: number) => {
    if (ms == null || isNaN(ms)) return '0ms';
    if (ms >= 1000) return `${(ms / 1000).toFixed(2)}s`;
    return `${ms.toFixed(0)}ms`;
  };

  const formatTokens = (count: number) => {
    if (count == null || isNaN(count)) return '0';
    if (count >= 1_000_000) return `${(count / 1_000_000).toFixed(1)}M`;
    if (count >= 1_000) return `${(count / 1_000).toFixed(1)}K`;
    return count.toString();
  };

  const formatCost = (usd: number) => {
    if (usd == null || isNaN(usd)) return '$0.00';
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

      <div
        className={styles.spans}
        role="list"
        aria-label="Live spans grouped by chat turn"
        data-testid="telemetry-spans-region"
      >
        {hasTurns ? (
          <>
            {preTurnSpans.length > 0 && (
              <div role="listitem" data-testid="telemetry-pre-turn">
                <Text className={styles.preTurnHeader}>Before first prompt</Text>
                <SpanTimeline spans={preTurnSpans} />
              </div>
            )}
            {turns!.map((turn) => (
              <div role="listitem" key={turn.id}>
                <TelemetryTurnGroup
                  turn={turn}
                  spans={spansByTurn.get(turn.id) ?? []}
                  isCurrent={turn.id === currentTurnId}
                />
              </div>
            ))}
          </>
        ) : (
          <SpanTimeline spans={liveSpans} />
        )}
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
