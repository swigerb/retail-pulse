import { makeStyles } from '@fluentui/react-components';
import { CARD_LIFECYCLE_CONFIG, CARD_COLORS } from '../../constants/agentRouting';
import type { CardLifecycleState } from '../../types';

interface CardLifecycleIndicatorProps {
  currentState: CardLifecycleState;
  stateChangedAt: string;
}

const STEPS: CardLifecycleState[] = ['active', 'voting', 'decided', 'archived'];

function formatElapsed(since: string): string {
  const ms = Date.now() - new Date(since).getTime();
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  return `${Math.floor(hours / 24)}d`;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    alignItems: 'center',
    gap: '0px',
    padding: '8px 0',
  },
  stepWrapper: {
    display: 'flex',
    alignItems: 'center',
    gap: '0px',
  },
  step: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '4px',
    position: 'relative',
  },
  dot: {
    width: '10px',
    height: '10px',
    borderRadius: '50%',
    transition: 'all 0.3s ease',
  },
  dotCurrent: {
    width: '16px',
    height: '16px',
    borderRadius: '50%',
    transition: 'all 0.3s ease',
  },
  label: {
    fontSize: '10px',
    color: 'var(--color-text-muted)',
    whiteSpace: 'nowrap',
    transition: 'color 0.3s ease',
  },
  labelCurrent: {
    fontSize: '10px',
    fontWeight: '700',
    whiteSpace: 'nowrap',
    transition: 'color 0.3s ease',
  },
  connector: {
    width: '24px',
    height: '2px',
    background: CARD_COLORS.cardBorder,
    marginBottom: '16px',
    transition: 'background 0.3s ease',
  },
  elapsed: {
    fontSize: '9px',
    color: 'var(--color-text-muted)',
    background: 'rgba(255,255,255,0.06)',
    padding: '1px 6px',
    borderRadius: '4px',
    marginTop: '2px',
    whiteSpace: 'nowrap',
  },
});

export default function CardLifecycleIndicator({ currentState, stateChangedAt }: CardLifecycleIndicatorProps) {
  const styles = useStyles();
  const currentIndex = STEPS.indexOf(currentState);

  return (
    <div className={styles.container} data-testid="card-lifecycle-indicator">
      {STEPS.map((step, i) => {
        const config = CARD_LIFECYCLE_CONFIG[step];
        const isCurrent = step === currentState;
        const isPast = i < currentIndex;
        const dotColor = isCurrent ? config.color : isPast ? config.color : 'rgba(255,255,255,0.12)';
        const labelColor = isCurrent ? config.color : isPast ? 'var(--color-text-muted)' : 'rgba(255,255,255,0.25)';

        return (
          <div key={step} className={styles.stepWrapper}>
            {i > 0 && (
              <div
                className={styles.connector}
                style={{ background: isPast || isCurrent ? config.color : undefined }}
              />
            )}
            <div className={styles.step}>
              <div
                className={isCurrent ? styles.dotCurrent : styles.dot}
                style={{
                  background: dotColor,
                  boxShadow: isCurrent ? `0 0 12px ${config.color}60` : undefined,
                }}
              />
              <span
                className={isCurrent ? styles.labelCurrent : styles.label}
                style={{ color: labelColor }}
              >
                {config.label}
              </span>
              {isCurrent && (
                <span className={styles.elapsed}>{formatElapsed(stateChangedAt)}</span>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
