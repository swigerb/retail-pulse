import { useEffect, useState } from 'react';
import { Text, makeStyles, mergeClasses } from '@fluentui/react-components';
import { ChevronRight16Regular } from '@fluentui/react-icons';
import type { AgentSpan, TelemetryTurn } from '../types';
import { SpanTimeline } from './SpanTimeline';

interface Props {
  turn: TelemetryTurn;
  spans: AgentSpan[];
  isCurrent: boolean;
}

const useStyles = makeStyles({
  group: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '10px 10px 12px',
    borderRadius: '8px',
    border: '1px solid var(--color-border)',
    borderLeftWidth: '3px',
    background: 'var(--color-bg-elevated)',
    // Older turns are visually de-emphasised so the current answer's spans
    // are unambiguous at a glance — the whole point of issue #303.
    opacity: 0.72,
  },
  groupCurrent: {
    opacity: 1,
    borderTopColor: 'var(--brand-accent-border, var(--brand-accent))',
    borderRightColor: 'var(--brand-accent-border, var(--brand-accent))',
    borderBottomColor: 'var(--brand-accent-border, var(--brand-accent))',
    borderLeftColor: 'var(--brand-accent)',
    boxShadow: '0 0 0 1px var(--brand-accent-border-faint, transparent)',
    background: 'var(--color-surface, var(--color-bg-elevated))',
  },
  header: {
    width: '100%',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    paddingBottom: '6px',
    borderBottom: '1px solid var(--color-border)',
    borderTop: 0,
    borderRight: 0,
    borderLeft: 0,
    background: 'transparent',
    color: 'inherit',
    font: 'inherit',
    textAlign: 'left',
    cursor: 'pointer',
    ':focus-visible': {
      outline: '2px solid var(--brand-accent)',
      outlineOffset: '3px',
      borderRadius: '4px',
    },
  },
  headerContent: {
    display: 'flex',
    flex: 1,
    minWidth: 0,
    flexDirection: 'column',
    gap: '2px',
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  turnIndex: {
    fontSize: '10px',
    fontWeight: '700',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    color: 'var(--color-text-subtle)',
  },
  turnIndexCurrent: {
    color: 'var(--brand-accent)',
  },
  currentBadge: {
    fontSize: '9px',
    fontWeight: '700',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    padding: '2px 6px',
    borderRadius: '4px',
    background: 'var(--brand-accent)',
    color: 'var(--color-bg-elevated, #000)',
  },
  label: {
    fontSize: '13px',
    fontWeight: '600',
    color: 'var(--color-text)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  meta: {
    fontSize: '11px',
    color: 'var(--color-text-subtle)',
  },
  chevron: {
    flexShrink: 0,
    color: 'var(--color-text-subtle)',
    transition: 'transform 0.2s ease',
  },
  chevronExpanded: {
    transform: 'rotate(90deg)',
  },
  content: {
    paddingTop: '2px',
  },
  emptyDetail: {
    fontSize: '12px',
    color: 'var(--color-text-subtle)',
    fontStyle: 'italic',
    padding: '6px 0',
  },
});

/**
 * A single chat-turn's worth of telemetry spans, rendered as an accessible
 * region with a stable header. Introduced for issue #303 so the Live Spans
 * panel no longer displays every turn's spans as one indistinguishable
 * flat list. The current turn is highlighted and marked `aria-current` so
 * screen readers and viewers can both tell at a glance which spans belong
 * to the answer sitting next to the panel.
 */
export function TelemetryTurnGroup({ turn, spans, isCurrent }: Props) {
  const styles = useStyles();
  const headingId = `telemetry-turn-heading-${turn.id}`;
  const contentId = `telemetry-turn-content-${turn.id}`;
  const [expanded, setExpanded] = useState(isCurrent);
  const spanCount = spans.length;
  const startedAt = new Date(turn.startedAt);
  const startedLabel = Number.isNaN(startedAt.getTime())
    ? turn.startedAt
    : startedAt.toLocaleTimeString();

  useEffect(() => {
    setExpanded(isCurrent);
  }, [isCurrent]);

  return (
    <section
      aria-labelledby={headingId}
      aria-current={isCurrent ? 'true' : undefined}
      data-testid={`telemetry-turn-${turn.id}`}
      data-turn-id={turn.id}
      data-turn-index={turn.index}
      data-turn-current={isCurrent ? 'true' : 'false'}
      className={mergeClasses(styles.group, isCurrent ? styles.groupCurrent : undefined)}
    >
      <button
        type="button"
        id={headingId}
        className={styles.header}
        aria-expanded={expanded}
        aria-controls={contentId}
        onClick={() => setExpanded(value => !value)}
      >
        <span className={styles.headerContent}>
          <span className={styles.headerRow}>
            <Text
              className={mergeClasses(styles.turnIndex, isCurrent ? styles.turnIndexCurrent : undefined)}
            >
              Turn {turn.index}
            </Text>
            {isCurrent && (
              <span className={styles.currentBadge} aria-hidden="true">
                Current
              </span>
            )}
          </span>
          <Text
            className={styles.label}
            title={turn.label}
            aria-label={`Prompt: ${turn.label}`}
          >
            {turn.label}
          </Text>
          <Text className={styles.meta}>
            {startedLabel} · {spanCount} {spanCount === 1 ? 'span' : 'spans'}
          </Text>
        </span>
        <span
          className={mergeClasses(styles.chevron, expanded ? styles.chevronExpanded : undefined)}
          aria-hidden="true"
        >
          <ChevronRight16Regular />
        </span>
      </button>
      {expanded && (
        <div id={contentId} className={styles.content}>
          {spanCount === 0 ? (
            <Text className={styles.emptyDetail}>
              Waiting for spans for this turn…
            </Text>
          ) : (
            <SpanTimeline spans={spans} />
          )}
        </div>
      )}
    </section>
  );
}
