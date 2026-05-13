import { makeStyles } from '@fluentui/react-components';
import { COUNCIL_COLORS, COUNCIL_DOMAIN_CONFIG } from '../../constants/agentRouting';
import type { CouncilAgentVote, HealthRating } from '../../types';

interface VoteCardProps {
  vote: CouncilAgentVote;
  index: number;
  animate?: boolean;
}

const RATING_CONFIG: Record<HealthRating, { emoji: string; label: string; color: string; bg: string; glow: string }> = {
  green: { emoji: '🟢', label: 'Healthy', color: COUNCIL_COLORS.green, bg: COUNCIL_COLORS.greenBg, glow: COUNCIL_COLORS.greenGlow },
  yellow: { emoji: '🟡', label: 'Caution', color: COUNCIL_COLORS.yellow, bg: COUNCIL_COLORS.yellowBg, glow: COUNCIL_COLORS.yellowGlow },
  red: { emoji: '🔴', label: 'At Risk', color: COUNCIL_COLORS.red, bg: COUNCIL_COLORS.redBg, glow: COUNCIL_COLORS.redGlow },
};

const useStyles = makeStyles({
  card: {
    flex: '1',
    minWidth: '260px',
    background: COUNCIL_COLORS.cardBg,
    border: `1px solid ${COUNCIL_COLORS.cardBorder}`,
    borderRadius: '12px',
    padding: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
    transition: 'all 0.4s cubic-bezier(0.34, 1.56, 0.64, 1)',
  },
  agentHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  agentIcon: {
    fontSize: '28px',
    width: '44px',
    height: '44px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: '50%',
    background: 'rgba(255,255,255,0.06)',
  },
  agentInfo: {
    flex: 1,
  },
  agentName: {
    fontSize: '15px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  agentDomain: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '1px',
  },
  responseTime: {
    fontSize: '10px',
    color: 'var(--color-text-muted)',
    background: 'rgba(255,255,255,0.06)',
    padding: '2px 8px',
    borderRadius: '4px',
    whiteSpace: 'nowrap',
  },
  ratingCircle: {
    width: '72px',
    height: '72px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    margin: '4px auto',
    fontSize: '14px',
    fontWeight: '800',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    transition: 'all 0.5s ease',
  },
  reasoning: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.6',
  },
  metricsRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px',
  },
  metricPill: {
    fontSize: '11px',
    padding: '3px 10px',
    borderRadius: '12px',
    background: 'rgba(255,255,255,0.06)',
    color: 'var(--color-text-muted)',
    whiteSpace: 'nowrap',
  },
  confidenceContainer: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  confidenceLabel: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    minWidth: '70px',
  },
  confidenceBar: {
    flex: 1,
    height: '6px',
    borderRadius: '3px',
    background: 'rgba(255,255,255,0.08)',
    overflow: 'hidden',
  },
  confidenceFill: {
    height: '100%',
    borderRadius: '3px',
    transition: 'width 1s cubic-bezier(0.34, 1.56, 0.64, 1)',
  },
  confidenceValue: {
    fontSize: '13px',
    fontWeight: '700',
    minWidth: '40px',
    textAlign: 'right',
  },
  timedOut: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    padding: '20px',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
  },
  thinking: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '12px',
    padding: '30px 20px',
    minHeight: '200px',
  },
  thinkingDots: {
    display: 'flex',
    gap: '6px',
  },
  thinkingDot: {
    width: '10px',
    height: '10px',
    borderRadius: '50%',
    animationName: {
      '0%, 80%, 100%': { opacity: 0.3, transform: 'scale(0.8)' },
      '40%': { opacity: 1, transform: 'scale(1.2)' },
    },
    animationDuration: '1.2s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-in-out',
  },
  thinkingLabel: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    animationName: {
      '0%, 100%': { opacity: 0.5 },
      '50%': { opacity: 1 },
    },
    animationDuration: '2s',
    animationIterationCount: 'infinite',
  },
});

export default function VoteCard({ vote, index, animate = true }: VoteCardProps) {
  const styles = useStyles();
  const domainConfig = COUNCIL_DOMAIN_CONFIG[vote.domain] ?? { emoji: '🤖', label: vote.domain, color: '#6b7280' };

  if (vote.timedOut) {
    return (
      <div
        className={styles.card}
        style={{
          opacity: 0.5,
          animationDelay: animate ? `${index * 150}ms` : '0ms',
        }}
        data-testid="vote-card-timedout"
        role="article"
        aria-label={`${vote.agentName} timed out`}
      >
        <div className={styles.agentHeader}>
          <div className={styles.agentIcon} style={{ borderColor: domainConfig.color }}>
            {domainConfig.emoji}
          </div>
          <div className={styles.agentInfo}>
            <div className={styles.agentName}>{vote.agentName}</div>
            <div className={styles.agentDomain}>{domainConfig.label}</div>
          </div>
        </div>
        <div className={styles.timedOut}>
          <span>⏱️</span>
          <span>Timed out — excluded from synthesis</span>
        </div>
      </div>
    );
  }

  const rating = RATING_CONFIG[vote.rating];

  return (
    <div
      className={styles.card}
      style={{
        borderColor: `${rating.color}40`,
        animationDelay: animate ? `${index * 150}ms` : '0ms',
      }}
      data-testid="vote-card"
      role="article"
      aria-label={`${vote.agentName} votes ${rating.label}`}
    >
      <div className={styles.agentHeader}>
        <div className={styles.agentIcon} style={{ border: `2px solid ${domainConfig.color}` }}>
          {domainConfig.emoji}
        </div>
        <div className={styles.agentInfo}>
          <div className={styles.agentName}>{vote.agentName}</div>
          <div className={styles.agentDomain}>{domainConfig.label}</div>
        </div>
        <span className={styles.responseTime}>
          ⚡ {vote.responseTimeMs}ms
        </span>
      </div>

      <div
        className={styles.ratingCircle}
        style={{
          background: rating.bg,
          color: rating.color,
          border: `3px solid ${rating.color}`,
          boxShadow: rating.glow,
        }}
        data-testid="vote-rating"
      >
        {rating.label}
      </div>

      <div className={styles.reasoning}>{vote.reasoning}</div>

      {vote.keyMetrics.length > 0 && (
        <div className={styles.metricsRow} data-testid="vote-metrics">
          {vote.keyMetrics.map((metric, i) => (
            <span key={i} className={styles.metricPill}>{metric}</span>
          ))}
        </div>
      )}

      <div className={styles.confidenceContainer}>
        <span className={styles.confidenceLabel}>Confidence</span>
        <div className={styles.confidenceBar}>
          <div
            className={styles.confidenceFill}
            style={{
              width: `${vote.confidence}%`,
              background: rating.color,
            }}
            data-testid="confidence-bar"
          />
        </div>
        <span className={styles.confidenceValue} style={{ color: rating.color }}>
          {vote.confidence}%
        </span>
      </div>
    </div>
  );
}

/** Shows the "thinking" placeholder before a vote arrives */
export function VoteCardThinking({ domain, index }: { domain: string; index: number }) {
  const styles = useStyles();
  const domainConfig = COUNCIL_DOMAIN_CONFIG[domain] ?? { emoji: '🤖', label: domain, color: '#6b7280' };

  return (
    <div
      className={styles.card}
      style={{ animationDelay: `${index * 150}ms` }}
      data-testid="vote-card-thinking"
    >
      <div className={styles.agentHeader}>
        <div className={styles.agentIcon} style={{ border: `2px solid ${domainConfig.color}` }}>
          {domainConfig.emoji}
        </div>
        <div className={styles.agentInfo}>
          <div className={styles.agentName}>{domainConfig.label}</div>
          <div className={styles.agentDomain}>Deliberating...</div>
        </div>
      </div>
      <div className={styles.thinking}>
        <div className={styles.thinkingDots}>
          {[0, 1, 2].map(i => (
            <div
              key={i}
              className={styles.thinkingDot}
              style={{
                backgroundColor: domainConfig.color,
                animationDelay: `${i * 0.2}s`,
              }}
            />
          ))}
        </div>
        <span className={styles.thinkingLabel}>Analyzing brand health...</span>
      </div>
    </div>
  );
}
