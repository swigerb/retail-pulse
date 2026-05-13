import { useState } from 'react';
import { Tooltip, Text, makeStyles } from '@fluentui/react-components';
import { ChevronRight16Regular } from '@fluentui/react-icons';
import type { RoutingInfo } from '../types';
import { AGENT_COLORS, AGENT_EMOJIS } from '../constants/agentRouting';

interface AgentRoutingIndicatorProps {
  routing: RoutingInfo;
}

const useStyles = makeStyles({
  container: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    marginTop: '4px',
  },
  pill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '3px 10px',
    borderRadius: '16px',
    fontSize: '11px',
    fontWeight: '500',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    border: '1px solid transparent',
    ':hover': {
      opacity: '0.9',
    },
  },
  agentName: {
    fontWeight: '600',
    fontSize: '11px',
  },
  confidenceBar: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: '10px',
    opacity: '0.85',
  },
  barTrack: {
    width: '32px',
    height: '4px',
    borderRadius: '2px',
    backgroundColor: 'rgba(255,255,255,0.15)',
    overflow: 'hidden',
  },
  barFill: {
    height: '100%',
    borderRadius: '2px',
    transition: 'width 0.3s ease',
  },
  reasoning: {
    marginTop: '6px',
    padding: '8px 12px',
    backgroundColor: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '8px',
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
    animation: 'messageIn 0.2s ease',
  },
  chevron: {
    fontSize: '9px',
    transition: 'transform 0.2s ease',
  },
  chevronExpanded: {
    transform: 'rotate(90deg)',
  },
});

export function AgentRoutingIndicator({ routing }: AgentRoutingIndicatorProps) {
  const [expanded, setExpanded] = useState(false);
  const styles = useStyles();

  const color = AGENT_COLORS[routing.intentCategory] ?? AGENT_COLORS.general;
  const emoji = AGENT_EMOJIS[routing.intentCategory] ?? AGENT_EMOJIS.general;
  const pct = Math.round(routing.confidence * 100);

  const pillStyle: React.CSSProperties = {
    backgroundColor: `${color}18`,
    borderColor: `${color}40`,
    color,
  };

  const barFillStyle: React.CSSProperties = {
    width: `${pct}%`,
    backgroundColor: color,
  };

  const pillContent = (
    <button
      className={styles.pill}
      style={pillStyle}
      onClick={() => routing.reasoning && setExpanded(!expanded)}
      aria-expanded={routing.reasoning ? expanded : undefined}
      aria-label={`Routed to ${routing.agentName}, ${pct}% confidence`}
    >
      <span>{emoji}</span>
      <span className={styles.agentName}>{routing.agentName}</span>
      <span className={styles.confidenceBar}>
        <span className={styles.barTrack}>
          <span className={styles.barFill} style={barFillStyle} />
        </span>
        {pct}%
      </span>
      {routing.reasoning && (
        <span className={`${styles.chevron} ${expanded ? styles.chevronExpanded : ''}`}>
          <ChevronRight16Regular />
        </span>
      )}
    </button>
  );

  return (
    <div className={styles.container}>
      <div>
        {routing.reasoning ? (
          pillContent
        ) : (
          <Tooltip content={`${routing.intentCategory} intent · ${pct}% confidence`} relationship="description">
            {pillContent}
          </Tooltip>
        )}
        {expanded && routing.reasoning && (
          <div className={styles.reasoning}>
            <Text>{routing.reasoning}</Text>
          </div>
        )}
      </div>
    </div>
  );
}
