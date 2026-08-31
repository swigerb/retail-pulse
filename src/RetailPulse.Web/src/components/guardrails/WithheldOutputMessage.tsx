import { makeStyles, tokens } from '@fluentui/react-components';
import type { SafetyBlockDisplayModel } from '../../types';
import { buildSafetyBlockDisplay } from '../../utils/safetyDisplay';

/**
 * Rendered in place of an assistant reply when Content Safety blocks the
 * model output. The component deliberately renders NO passthrough for the
 * withheld reply — even if a caller mistakenly passes partial content it
 * will never surface here. Progressive rendering must be replaced with this
 * message, not overlaid on it.
 */
export interface WithheldOutputMessageProps {
  display?: SafetyBlockDisplayModel;
  /** Optional override for the default "you may retry" hint. */
  suggestion?: string;
}

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
    minWidth: 0,
  },
  title: {
    color: tokens.colorNeutralForeground1,
    fontWeight: 600,
  },
  reason: {
    color: tokens.colorNeutralForeground2,
    fontSize: '13px',
    overflowWrap: 'anywhere',
  },
  metaRow: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
  },
  metaChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '11px',
    fontWeight: 600,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

export function WithheldOutputMessage({ display, suggestion }: WithheldOutputMessageProps) {
  const styles = useStyles();
  const model = display ?? buildSafetyBlockDisplay({ stage: 'output', decision: 'Blocked' });

  return (
    <div
      className={styles.container}
      data-testid="withheld-output-message"
      data-safety-stage="output"
      data-safety-family={model.family}
      role="status"
      aria-live="polite"
    >
      <span className={styles.icon} aria-hidden="true">🚫</span>
      <div className={styles.content}>
        <span className={styles.title}>Response withheld for safety</span>
        <span className={styles.reason}>{model.reason}</span>
        {(model.categoryLabel || model.severityLabel) && (
          <div className={styles.metaRow} data-testid="withheld-output-meta">
            {model.categoryLabel && (
              <span
                className={styles.metaChip}
                data-testid="withheld-output-category"
                data-safety-category={model.categoryName ?? 'unknown'}
              >
                Category: {model.categoryLabel}
              </span>
            )}
            {model.severityLabel && (
              <span
                className={styles.metaChip}
                data-testid="withheld-output-severity"
                data-safety-severity={model.severityLabel}
              >
                Severity: {model.severityLabel}
              </span>
            )}
          </div>
        )}
        {suggestion && (
          <span className={styles.reason} data-testid="withheld-output-suggestion">
            💡 {suggestion}
          </span>
        )}
      </div>
    </div>
  );
}
