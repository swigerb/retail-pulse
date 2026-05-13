import { makeStyles } from '@fluentui/react-components';

export interface BlockedRequestMessageProps {
  reason: string;
  suggestion?: string;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    gap: '12px',
    padding: '14px 18px',
    borderRadius: '12px',
    backgroundColor: 'rgba(245, 158, 11, 0.06)',
    border: '1px solid rgba(245, 158, 11, 0.25)',
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
  },
  reason: {
    color: 'var(--color-text, #e2e8f0)',
    fontWeight: '500',
  },
  suggestion: {
    fontSize: '13px',
    color: 'var(--color-text-muted, #94a3b8)',
    fontStyle: 'italic',
  },
});

export function BlockedRequestMessage({ reason, suggestion }: BlockedRequestMessageProps) {
  const styles = useStyles();

  return (
    <div className={styles.container} data-testid="blocked-request-message" role="alert">
      <span className={styles.icon}>🛡️</span>
      <div className={styles.content}>
        <span className={styles.reason}>This request was blocked because: {reason}</span>
        {suggestion && (
          <span className={styles.suggestion}>
            💡 Try rephrasing your question about {suggestion}
          </span>
        )}
      </div>
    </div>
  );
}
