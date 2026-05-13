import { makeStyles } from '@fluentui/react-components';
import { CARD_COLORS } from '../../constants/agentRouting';

interface EscalationBannerProps {
  reason: string;
  contextLink?: string;
}

const useStyles = makeStyles({
  banner: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '10px',
    padding: '12px 16px',
    borderRadius: '8px',
    background: CARD_COLORS.escalationBg,
    border: `1px solid ${CARD_COLORS.escalation}40`,
    animationName: {
      '0%': { opacity: 0, transform: 'translateY(-4px)' },
      '100%': { opacity: 1, transform: 'translateY(0)' },
    },
    animationDuration: '0.4s',
    animationTimingFunction: 'ease-out',
    animationFillMode: 'both',
  },
  icon: {
    fontSize: '18px',
    flexShrink: 0,
    lineHeight: '1.4',
    animationName: {
      '0%, 100%': { opacity: 1 },
      '50%': { opacity: 0.6 },
    },
    animationDuration: '2s',
    animationIterationCount: '3',
    animationTimingFunction: 'ease-in-out',
  },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    flex: 1,
  },
  title: {
    fontSize: '12px',
    fontWeight: '700',
    color: CARD_COLORS.escalation,
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  reason: {
    fontSize: '13px',
    color: 'var(--color-text)',
    lineHeight: '1.5',
  },
  link: {
    fontSize: '12px',
    color: CARD_COLORS.escalation,
    textDecoration: 'underline',
    textDecorationStyle: 'dotted',
    textUnderlineOffset: '2px',
    cursor: 'pointer',
    ':hover': {
      opacity: 0.8,
    },
  },
});

export default function EscalationBanner({ reason, contextLink }: EscalationBannerProps) {
  const styles = useStyles();

  return (
    <div className={styles.banner} data-testid="escalation-banner" role="alert">
      <span className={styles.icon}>⚠️</span>
      <div className={styles.content}>
        <span className={styles.title}>Escalation</span>
        <span className={styles.reason}>{reason}</span>
        {contextLink && (
          <a
            className={styles.link}
            href={contextLink}
            target="_blank"
            rel="noopener noreferrer"
          >
            View context →
          </a>
        )}
      </div>
    </div>
  );
}
