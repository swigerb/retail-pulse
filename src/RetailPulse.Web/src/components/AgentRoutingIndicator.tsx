import { Tooltip, makeStyles } from '@fluentui/react-components';
import type { RoutingInfo } from '../types';
import { getIntentCategory } from '../types';
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
});

export function AgentRoutingIndicator({ routing }: AgentRoutingIndicatorProps) {
  const styles = useStyles();

  const category = getIntentCategory(routing.intent);
  const color = AGENT_COLORS[category] ?? AGENT_COLORS.general;
  const emoji = AGENT_EMOJIS[category] ?? AGENT_EMOJIS.general;
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
    </button>
  );

  return (
    <div className={styles.container}>
      <div>
        <Tooltip content={`${category} intent · ${pct}% confidence${routing.durationMs ? ` · ${routing.durationMs}ms` : ''}`} relationship="description">
          {pillContent}
        </Tooltip>
      </div>
    </div>
  );
}
