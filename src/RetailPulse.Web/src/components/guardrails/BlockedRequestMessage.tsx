import { makeStyles, tokens } from '@fluentui/react-components';
import type { SafetyBlockDisplayModel } from '../../types';
import { buildSafetyBlockDisplay } from '../../utils/safetyDisplay';

/**
 * BlockedRequestMessage explains a safety decision to the user. It now
 * accepts a whitelisted `SafetyBlockDisplayModel` (issue #101) that carries
 * only fields safe to render: plain-language reason, plain-language category
 * label, severity descriptor (`low`/`medium`/`high`/`severe`), and an
 * optional suggestion.
 *
 * Raw regex patterns, numeric thresholds, analyzer names, rule IDs, or any
 * bypass-useful strings must never be threaded through the display prop —
 * `buildSafetyBlockDisplay` is the whitelisting seam. The legacy
 * `{ reason, suggestion }` shape is preserved for callers that don't yet
 * pass a display model.
 */
export type BlockedRequestMessageProps =
  | { display: SafetyBlockDisplayModel; reason?: never; suggestion?: never }
  | { display?: undefined; reason: string; suggestion?: string };

const useStyles = makeStyles({
  container: {
    display: 'flex',
    gap: '12px',
    padding: '14px 18px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorStatusWarningBackground1,
    border: `1px solid ${tokens.colorStatusWarningBorder1}`,
    color: tokens.colorNeutralForeground1,
    fontSize: '14px',
    lineHeight: '1.6',
    animationName: {
      '0%': { opacity: 0, transform: 'translateY(4px)' },
      '100%': { opacity: 1, transform: 'translateY(0)' },
    },
    animationDuration: '300ms',
    animationTimingFunction: 'ease-out',
  },
  icon: {
    fontSize: '20px',
    flexShrink: 0,
    lineHeight: '1.6',
  },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    // Sit next to a flex-shrink:0 icon, so claim a shrinkable track that lets
    // long reason sentences wrap instead of widening the alert.
    minWidth: 0,
  },
  reason: {
    color: tokens.colorNeutralForeground1,
    fontWeight: 500,
    overflowWrap: 'anywhere',
  },
  metaRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px',
    marginTop: '2px',
  },
  metaChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '11px',
    fontWeight: 600,
    letterSpacing: '0.2px',
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  suggestion: {
    fontSize: '13px',
    color: tokens.colorNeutralForeground2,
    fontStyle: 'italic',
  },
});

function resolveDisplay(props: BlockedRequestMessageProps): SafetyBlockDisplayModel {
  if (props.display) return props.display;
  return buildSafetyBlockDisplay({
    stage: 'input',
    reason: props.reason,
    suggestion: props.suggestion,
  });
}

export function BlockedRequestMessage(props: BlockedRequestMessageProps) {
  const styles = useStyles();
  const display = resolveDisplay(props);

  return (
    <div
      className={styles.container}
      data-testid="blocked-request-message"
      data-safety-stage={display.stage}
      data-safety-family={display.family}
      role="alert"
    >
      <span className={styles.icon} aria-hidden="true">🛡️</span>
      <div className={styles.content}>
        <span className={styles.reason}>{display.reason}</span>
        {(display.categoryLabel || display.severityLabel || display.decision) && (
          <div className={styles.metaRow} data-testid="blocked-request-meta">
            {display.categoryLabel && (
              <span
                className={styles.metaChip}
                data-testid="blocked-request-category"
                data-safety-category={display.categoryName ?? 'unknown'}
              >
                Category: {display.categoryLabel}
              </span>
            )}
            {display.severityLabel && (
              <span
                className={styles.metaChip}
                data-testid="blocked-request-severity"
                data-safety-severity={display.severityLabel}
              >
                Severity: {display.severityLabel}
              </span>
            )}
            {display.decision && display.decision !== 'Blocked' && (
              <span
                className={styles.metaChip}
                data-testid="blocked-request-decision"
                data-safety-decision={display.decision}
              >
                {display.decision === 'Flagged' ? 'Flagged' : 'Safety service unavailable'}
              </span>
            )}
          </div>
        )}
        {display.suggestion && (
          <span className={styles.suggestion} data-testid="blocked-request-suggestion">
            💡 {display.suggestion}
          </span>
        )}
      </div>
    </div>
  );
}
