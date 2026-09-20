import { Text, makeStyles, mergeClasses } from '@fluentui/react-components';
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
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    paddingBottom: '6px',
    borderBottom: '1px solid var(--color-border)',
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
  const spanCount = spans.length;
  const startedAt = new Date(turn.startedAt);
  const startedLabel = Number.isNaN(startedAt.getTime())
    ? turn.startedAt
    : startedAt.toLocaleTimeString();

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
      <header className={styles.header}>
        <div className={styles.headerRow}>
          <Text
            id={headingId}
            className={mergeClasses(styles.turnIndex, isCurrent ? styles.turnIndexCurrent : undefined)}
          >
            Turn {turn.index}
          </Text>
          {isCurrent && (
            <span className={styles.currentBadge} aria-hidden="true">
              Current
            </span>
          )}
        </div>
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
      </header>
      {spanCount === 0 ? (
        <Text className={styles.emptyDetail}>
          Waiting for spans for this turn…
        </Text>
      ) : (
        <SpanTimeline spans={spans} />
      )}
    </section>
  );
}
