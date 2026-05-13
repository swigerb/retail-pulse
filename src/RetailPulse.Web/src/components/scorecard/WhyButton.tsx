import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { SCORECARD_COLORS } from '../../constants/agentRouting';

interface WhyButtonProps {
  traceId?: string;
  onClick?: () => void;
  loading?: boolean;
  size?: 'small' | 'medium';
}

const useStyles = makeStyles({
  button: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: '50%',
    border: 'none',
    cursor: 'pointer',
    fontWeight: '700',
    color: SCORECARD_COLORS.whyButton,
    backgroundColor: SCORECARD_COLORS.whyButtonBg,
    transitionProperty: 'transform, box-shadow',
    transitionDuration: '0.18s',
    transitionTimingFunction: 'ease',
    flexShrink: 0,
    ':hover': {
      transform: 'scale(1.15)',
      boxShadow: `0 0 10px rgba(139,92,246,0.4)`,
    },
    ':active': {
      transform: 'scale(0.95)',
    },
  },
  small: {
    width: '20px',
    height: '20px',
    fontSize: '11px',
  },
  medium: {
    width: '28px',
    height: '28px',
    fontSize: '14px',
  },
  spinning: {
    animationName: {
      from: { transform: 'rotate(0deg)' },
      to: { transform: 'rotate(360deg)' },
    },
    animationDuration: '0.8s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'linear',
  },
});

export function WhyButton({ onClick, loading = false, size = 'small' }: WhyButtonProps) {
  const styles = useStyles();

  return (
    <button
      className={mergeClasses(
        styles.button,
        size === 'small' ? styles.small : styles.medium,
        loading ? styles.spinning : undefined,
      )}
      onClick={(e) => {
        e.stopPropagation();
        onClick?.();
      }}
      title="Explain this recommendation"
      aria-label="Explain this recommendation"
    >
      ?
    </button>
  );
}
