import { makeStyles, tokens } from '@fluentui/react-components';
import type { SafetyBlockDisplayModel } from '../../types';
import { buildSafetyBlockDisplay } from '../../utils/safetyDisplay';

/**
 * Renders a single plan step (issue #93 plan-first orchestration) whose
 * execution was blocked by the safety layer. The component receives a
 * whitelisted `SafetyBlockDisplayModel`; only the step's ordinal, intent
 * label, and action summary are rendered from the plan context — the step's
 * `error` and `result` strings are treated as untrusted and are NOT
 * rendered.
 *
 * The `planPreserved` slot is a hint the plan-viewer wraps around the whole
 * plan so blocking one step doesn't collapse the overall plan into an error
 * state. When it's `true` (the default) we render an accessible note telling
 * the user the surrounding plan continues in a preserved state.
 */
export interface PlanStepSafetyBlockProps {
  stepIndex: number;
  intent: string;
  action: string;
  display?: SafetyBlockDisplayModel;
  /** When `false`, the overall-plan hint is suppressed. Defaults to `true`. */
  planPreserved?: boolean;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    gap: '12px',
    padding: '14px 18px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorStatusWarningBackground1,
    border: `1px solid ${tokens.colorStatusWarningBorder1}`,
    fontSize: '14px',
    lineHeight: '1.5',
  },
  icon: {
    fontSize: '18px',
    flexShrink: 0,
    lineHeight: '1.5',
  },
  body: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    minWidth: 0,
  },
  header: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '8px',
    color: tokens.colorNeutralForeground1,
    fontWeight: 600,
  },
  intent: {
    color: tokens.colorNeutralForeground2,
    fontWeight: 500,
  },
  action: {
    color: tokens.colorNeutralForeground2,
    fontSize: '13px',
  },
  reason: {
    color: tokens.colorNeutralForeground1,
  },
  metaRow: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
  },
  metaChip: {
    display: 'inline-flex',
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '11px',
    fontWeight: 600,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  planNote: {
    marginTop: '4px',
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
});

export function PlanStepSafetyBlock({
  stepIndex,
  intent,
  action,
  display,
  planPreserved = true,
}: PlanStepSafetyBlockProps) {
  const styles = useStyles();
  const model = display ?? buildSafetyBlockDisplay({ stage: 'plan-step', decision: 'Blocked' });
  const safeStep = Number.isFinite(stepIndex) ? Math.max(0, Math.floor(stepIndex)) : 0;

  return (
    <div
      className={styles.container}
      data-testid="plan-step-safety-block"
      data-safety-stage="plan-step"
      data-safety-family={model.family}
      data-plan-step-index={safeStep}
      role="alert"
    >
      <span className={styles.icon} aria-hidden="true">🛑</span>
      <div className={styles.body}>
        <span className={styles.header}>
          <span>Step {safeStep + 1} blocked</span>
          {intent && <span className={styles.intent}>· {intent}</span>}
        </span>
        {action && (
          <span className={styles.action} data-testid="plan-step-action">
            {action}
          </span>
        )}
        <span className={styles.reason} data-testid="plan-step-reason">
          {model.reason}
        </span>
        {(model.categoryLabel || model.severityLabel) && (
          <div className={styles.metaRow} data-testid="plan-step-meta">
            {model.categoryLabel && (
              <span className={styles.metaChip} data-testid="plan-step-category">
                Category: {model.categoryLabel}
              </span>
            )}
            {model.severityLabel && (
              <span className={styles.metaChip} data-testid="plan-step-severity">
                Severity: {model.severityLabel}
              </span>
            )}
          </div>
        )}
        {planPreserved && (
          <span
            className={styles.planNote}
            data-testid="plan-preserved-note"
            role="status"
          >
            The rest of the plan continues in its recorded state; earlier steps and remaining steps stay visible.
          </span>
        )}
      </div>
    </div>
  );
}
