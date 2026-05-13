import { makeStyles, Badge } from '@fluentui/react-components';
import { COUNCIL_COLORS } from '../../constants/agentRouting';
import type { CouncilVerdict as CouncilVerdictType, HealthRating } from '../../types';
import DisagreementHighlight from './DisagreementHighlight';

interface CouncilVerdictProps {
  verdict: CouncilVerdictType;
}

const RATING_DISPLAY: Record<HealthRating, { emoji: string; label: string; color: string; bg: string; glow: string }> = {
  green: { emoji: '🟢', label: 'Healthy', color: COUNCIL_COLORS.green, bg: COUNCIL_COLORS.greenBg, glow: COUNCIL_COLORS.greenGlow },
  yellow: { emoji: '🟡', label: 'Caution', color: COUNCIL_COLORS.yellow, bg: COUNCIL_COLORS.yellowBg, glow: COUNCIL_COLORS.yellowGlow },
  red: { emoji: '🔴', label: 'At Risk', color: COUNCIL_COLORS.red, bg: COUNCIL_COLORS.redBg, glow: COUNCIL_COLORS.redGlow },
};

const PRIORITY_COLORS: Record<number, string> = {
  1: COUNCIL_COLORS.red,
  2: COUNCIL_COLORS.yellow,
  3: COUNCIL_COLORS.green,
};

const useStyles = makeStyles({
  container: {
    background: COUNCIL_COLORS.verdictBg,
    border: `1px solid ${COUNCIL_COLORS.cardBorder}`,
    borderRadius: '14px',
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    animationName: {
      from: { opacity: 0, transform: 'translateY(12px)' },
      to: { opacity: 1, transform: 'translateY(0)' },
    },
    animationDuration: '0.5s',
    animationFillMode: 'both',
    animationTimingFunction: 'cubic-bezier(0.34, 1.56, 0.64, 1)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    flexWrap: 'wrap',
  },
  ratingDisplay: {
    width: '80px',
    height: '80px',
    borderRadius: '50%',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '2px',
    flexShrink: 0,
  },
  ratingEmoji: {
    fontSize: '28px',
  },
  ratingLabel: {
    fontSize: '11px',
    fontWeight: '800',
    textTransform: 'uppercase',
    letterSpacing: '1px',
  },
  headerText: {
    flex: 1,
  },
  verdictTitle: {
    fontSize: '20px',
    fontWeight: '700',
    color: 'var(--color-text)',
    marginBottom: '4px',
  },
  unanimousBadge: {
    fontSize: '12px',
    fontWeight: '700',
    padding: '4px 12px',
    borderRadius: '6px',
  },
  synthesisText: {
    fontSize: '14px',
    lineHeight: '1.7',
    color: 'var(--color-text)',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  sectionTitle: {
    fontSize: '13px',
    fontWeight: '700',
    color: 'var(--color-text)',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  actionItem: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '10px',
    fontSize: '13px',
    lineHeight: '1.5',
    color: 'var(--color-text-muted)',
    padding: '8px 12px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '8px',
  },
  priorityBadge: {
    fontSize: '11px',
    fontWeight: '800',
    minWidth: '24px',
    height: '24px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    color: '#fff',
  },
  conveneTime: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    textAlign: 'center',
    padding: '8px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '6px',
  },
});

export default function CouncilVerdictView({ verdict }: CouncilVerdictProps) {
  const styles = useStyles();
  const rating = RATING_DISPLAY[verdict.overallRating];

  return (
    <div className={styles.container} data-testid="council-verdict">
      <div className={styles.header}>
        <div
          className={styles.ratingDisplay}
          style={{
            background: rating.bg,
            border: `3px solid ${rating.color}`,
            boxShadow: rating.glow,
          }}
          data-testid="verdict-rating"
        >
          <span className={styles.ratingEmoji}>{rating.emoji}</span>
          <span className={styles.ratingLabel} style={{ color: rating.color }}>
            {rating.label}
          </span>
        </div>

        <div className={styles.headerText}>
          <div className={styles.verdictTitle}>Executive Verdict</div>
          <Badge
            className={styles.unanimousBadge}
            appearance="filled"
            style={{
              background: verdict.unanimous ? `${COUNCIL_COLORS.green}25` : `${COUNCIL_COLORS.yellow}25`,
              color: verdict.unanimous ? COUNCIL_COLORS.green : COUNCIL_COLORS.yellow,
            }}
            data-testid="unanimous-badge"
          >
            {verdict.unanimous ? '✓ Unanimous' : '⚠️ Split Decision'}
          </Badge>
        </div>
      </div>

      <div className={styles.synthesisText} data-testid="synthesis-text">
        {verdict.synthesisText}
      </div>

      {verdict.disagreements.length > 0 && (
        <div className={styles.section}>
          <div className={styles.sectionTitle}>
            <span>⚡ Disagreements</span>
          </div>
          <DisagreementHighlight disagreements={verdict.disagreements} />
        </div>
      )}

      {verdict.actionItems.length > 0 && (
        <div className={styles.section} data-testid="action-items">
          <div className={styles.sectionTitle}>
            <span>📋 Recommended Actions</span>
          </div>
          {verdict.actionItems.map((item, i) => (
            <div key={i} className={styles.actionItem}>
              <div
                className={styles.priorityBadge}
                style={{ background: PRIORITY_COLORS[item.priority] ?? COUNCIL_COLORS.green }}
              >
                {item.priority}
              </div>
              <span>{item.text}</span>
            </div>
          ))}
        </div>
      )}

      <div className={styles.conveneTime} data-testid="convene-time">
        ⏱️ Council convened in {(verdict.totalConveneTimeMs / 1000).toFixed(1)}s
      </div>
    </div>
  );
}
