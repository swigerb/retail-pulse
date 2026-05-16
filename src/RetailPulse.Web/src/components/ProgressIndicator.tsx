import { makeStyles } from '@fluentui/react-components';
import type { ProgressEvent } from '../services/telemetryHub';

export interface ProgressStep {
  phase: string;
  detail: string;
  durationMs?: number;
  timestamp: string;
}

export interface ProgressIndicatorProps {
  /** Currently active phase text */
  currentPhase: string;
  /** Completed progress steps with optional durations */
  completedSteps: ProgressStep[];
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px 16px',
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '12px',
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    minWidth: '200px',
  },
  currentPhase: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontWeight: '500',
    color: 'var(--color-text)',
  },
  spinner: {
    display: 'inline-block',
    width: '8px',
    height: '8px',
    borderRadius: '50%',
    backgroundColor: 'var(--brand-accent, #60a5fa)',
    animationName: {
      '0%, 100%': { opacity: 0.4, transform: 'scale(0.8)' },
      '50%': { opacity: 1, transform: 'scale(1.2)' },
    },
    animationDuration: '1s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-in-out',
    flexShrink: 0,
  },
  completedStep: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: '12px',
    color: 'var(--color-text-muted)',
  },
  checkmark: {
    color: 'var(--brand-accent, #60a5fa)',
    fontSize: '11px',
    flexShrink: 0,
  },
  duration: {
    fontFamily: "'Courier New', monospace",
    fontSize: '11px',
    color: 'var(--color-text-subtle, #64748b)',
    marginLeft: 'auto',
    flexShrink: 0,
  },
});

/**
 * Extracts a ProgressStep from a ProgressEvent when a tool_result phase arrives.
 */
export function buildProgressStep(event: ProgressEvent & { durationMs?: number }): ProgressStep {
  return {
    phase: event.phase,
    detail: event.detail,
    durationMs: event.durationMs,
    timestamp: event.timestamp,
  };
}

export function ProgressIndicator({ currentPhase, completedSteps }: ProgressIndicatorProps) {
  const styles = useStyles();

  return (
    <div className={styles.container} data-testid="progress-indicator">
      {completedSteps.map((step, i) => (
        <div key={`step-${i}`} className={styles.completedStep} data-testid="progress-completed-step">
          <span className={styles.checkmark}>✓</span>
          <span>{step.detail}</span>
          {step.durationMs != null && (
            <span className={styles.duration}>{step.durationMs}ms</span>
          )}
        </div>
      ))}
      <div className={styles.currentPhase} data-testid="progress-current-phase">
        <span className={styles.spinner} />
        <span>{currentPhase}</span>
      </div>
    </div>
  );
}
