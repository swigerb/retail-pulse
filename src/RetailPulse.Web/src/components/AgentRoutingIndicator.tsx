import { Tooltip, makeStyles } from '@fluentui/react-components';
import type { ExecutionPath, RoutingInfo } from '../types';
import { getIntentCategory } from '../types';
import { AGENT_COLORS, AGENT_EMOJIS } from '../constants/agentRouting';

interface AgentRoutingIndicatorProps {
  routing: RoutingInfo;
}

const useStyles = makeStyles({
  container: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
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
  pathPill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: '10px',
    fontSize: '10px',
    fontWeight: '600',
    letterSpacing: '0.02em',
    textTransform: 'uppercase',
    color: 'var(--color-text-muted)',
    backgroundColor: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
  },
  pathPillForced: {
    color: 'var(--brand-accent)',
    backgroundColor: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent-border)',
  },
  forcedDot: {
    display: 'inline-block',
    width: '5px',
    height: '5px',
    borderRadius: '50%',
    backgroundColor: 'var(--brand-accent)',
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

const PATH_LABEL: Record<ExecutionPath, string> = {
  fast: 'Fast',
  plan: 'Plan',
  council: 'Council',
};

const PATH_DESCRIPTION: Record<ExecutionPath, string> = {
  fast: 'single-specialist single-shot execution',
  plan: 'plan-first workflow',
  council: 'consensus-council interception',
};

export function AgentRoutingIndicator({ routing }: AgentRoutingIndicatorProps) {
  const styles = useStyles();

  const category = getIntentCategory(routing.intent);
  const color = AGENT_COLORS[category] ?? AGENT_COLORS.general;
  const emoji = AGENT_EMOJIS[category] ?? AGENT_EMOJIS.general;
  const pct = Math.round(routing.confidence * 100);
  const path: ExecutionPath | undefined = routing.executionPath;
  const forced = routing.executionPathForced === true;

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
      {path && (
        <Tooltip
          content={`Execution path: ${PATH_LABEL[path]} — ${PATH_DESCRIPTION[path]}${forced ? ' (forced by user override)' : ''}.`}
          relationship="description"
        >
          <span
            className={`${styles.pathPill} ${forced ? styles.pathPillForced : ''}`}
            data-testid="execution-path-pill"
            data-execution-path={path}
            data-execution-path-forced={forced ? 'true' : 'false'}
            aria-label={
              forced
                ? `Execution path: ${PATH_LABEL[path]} (forced)`
                : `Execution path: ${PATH_LABEL[path]}`
            }
          >
            {forced && <span aria-hidden="true" className={styles.forcedDot} />}
            <span>{PATH_LABEL[path]}</span>
          </span>
        </Tooltip>
      )}
    </div>
  );
}
