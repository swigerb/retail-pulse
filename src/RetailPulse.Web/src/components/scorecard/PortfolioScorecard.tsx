import { makeStyles } from '@fluentui/react-components';
import type { BrandScore } from '../../types';
import { SCORECARD_COLORS } from '../../constants/agentRouting';
import { WhyButton } from './WhyButton';

interface PortfolioScorecardProps {
  brands: BrandScore[];
  loading?: boolean;
  generationTimeMs?: number;
  onBrandClick?: (brandName: string) => void;
  onWhyClick?: (brandName: string) => void;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))',
    gap: '16px',
  },
  card: {
    position: 'relative',
    padding: '20px',
    borderRadius: '14px',
    backgroundColor: SCORECARD_COLORS.cardBg,
    border: `1px solid ${SCORECARD_COLORS.cardBorder}`,
    cursor: 'pointer',
    transitionProperty: 'transform, box-shadow, border-color',
    transitionDuration: '0.2s',
    transitionTimingFunction: 'ease',
    ':hover': {
      transform: 'translateY(-2px)',
      borderColor: 'rgba(255,255,255,0.15)' as unknown as undefined,
      boxShadow: '0 8px 24px rgba(0,0,0,0.3)',
    },
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: '12px',
  },
  brandName: {
    fontSize: '15px',
    fontWeight: '700',
    color: '#f1f5f9',
    letterSpacing: '-0.2px',
  },
  scoreRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '14px',
  },
  scoreRing: {
    position: 'relative',
    width: '56px',
    height: '56px',
    flexShrink: 0,
  },
  scoreRingSvg: {
    width: '56px',
    height: '56px',
    transform: 'rotate(-90deg)',
  },
  scoreNumber: {
    position: 'absolute',
    inset: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '20px',
    fontWeight: '800',
    letterSpacing: '-1px',
  },
  trendArrow: {
    fontSize: '20px',
    fontWeight: '700',
  },
  pills: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  pill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: '11px',
    fontWeight: '500',
    padding: '3px 10px',
    borderRadius: '20px',
    maxWidth: '100%',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  genTime: {
    fontSize: '12px',
    color: '#64748b',
    textAlign: 'center',
  },
  empty: {
    textAlign: 'center',
    color: '#64748b',
    padding: '48px 0',
    fontSize: '14px',
  },
  // Skeleton card
  skeletonCard: {
    padding: '20px',
    borderRadius: '14px',
    backgroundColor: SCORECARD_COLORS.cardBg,
    border: `1px solid ${SCORECARD_COLORS.cardBorder}`,
  },
  skeletonBlock: {
    borderRadius: '6px',
    backgroundColor: SCORECARD_COLORS.skeletonBg,
  },
});

function getScoreColor(score: number) {
  if (score > 75) return { color: SCORECARD_COLORS.green, bg: SCORECARD_COLORS.greenBg, glow: SCORECARD_COLORS.greenGlow };
  if (score >= 50) return { color: SCORECARD_COLORS.amber, bg: SCORECARD_COLORS.amberBg, glow: SCORECARD_COLORS.amberGlow };
  return { color: SCORECARD_COLORS.red, bg: SCORECARD_COLORS.redBg, glow: SCORECARD_COLORS.redGlow };
}

function getTrend(trend: 'up' | 'down' | 'stable') {
  if (trend === 'up') return { symbol: '↑', color: SCORECARD_COLORS.green };
  if (trend === 'down') return { symbol: '↓', color: SCORECARD_COLORS.red };
  return { symbol: '→', color: '#64748b' };
}

function ScoreRing({ score }: { score: number }) {
  const styles = useStyles();
  const { color, glow } = getScoreColor(score);
  const radius = 22;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference - (score / 100) * circumference;

  return (
    <div className={styles.scoreRing} style={{ filter: `drop-shadow(${glow})` }}>
      <svg className={styles.scoreRingSvg} viewBox="0 0 56 56">
        <circle
          cx="28" cy="28" r={radius}
          fill="none"
          stroke={SCORECARD_COLORS.ringTrack}
          strokeWidth="4"
        />
        <circle
          cx="28" cy="28" r={radius}
          fill="none"
          stroke={color}
          strokeWidth="4"
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
          style={{ transition: 'stroke-dashoffset 0.8s ease' }}
        />
      </svg>
      <span className={styles.scoreNumber} style={{ color }}>
        {score}
      </span>
    </div>
  );
}

function SkeletonCards() {
  const styles = useStyles();
  const shimmerKeyframes = `
    @keyframes scorecardShimmer {
      0% { background-position: -200% 0; }
      100% { background-position: 200% 0; }
    }
  `;
  const shimmerStyle: React.CSSProperties = {
    backgroundImage: `linear-gradient(90deg, ${SCORECARD_COLORS.skeletonBg} 25%, ${SCORECARD_COLORS.skeletonShimmer} 50%, ${SCORECARD_COLORS.skeletonBg} 75%)`,
    backgroundSize: '200% 100%',
    animation: 'scorecardShimmer 1.8s ease-in-out infinite',
  };

  return (
    <>
      <style>{shimmerKeyframes}</style>
      {Array.from({ length: 6 }).map((_, i) => (
        <div key={i} className={styles.skeletonCard}>
          <div className={styles.skeletonBlock} style={{ ...shimmerStyle, height: '18px', width: '60%', marginBottom: '14px' }} />
          <div className={styles.skeletonBlock} style={{ ...shimmerStyle, height: '48px', width: '48px', borderRadius: '50%', marginBottom: '14px' }} />
          <div className={styles.skeletonBlock} style={{ ...shimmerStyle, height: '14px', width: '80%', marginBottom: '8px' }} />
          <div className={styles.skeletonBlock} style={{ ...shimmerStyle, height: '14px', width: '70%' }} />
        </div>
      ))}
    </>
  );
}

export function PortfolioScorecard({
  brands,
  loading = false,
  generationTimeMs,
  onBrandClick,
  onWhyClick,
}: PortfolioScorecardProps) {
  const styles = useStyles();

  if (!loading && brands.length === 0) {
    return <div className={styles.empty}>No brand data available yet.</div>;
  }

  return (
    <div className={styles.container}>
      <div className={styles.grid}>
        {loading ? (
          <SkeletonCards />
        ) : (
          brands.map((brand) => {
            const trend = getTrend(brand.trend);
            return (
              <div
                key={brand.brandName}
                className={styles.card}
                onClick={() => onBrandClick?.(brand.brandName)}
              >
                <div className={styles.cardHeader}>
                  <span className={styles.brandName}>{brand.brandName}</span>
                  <WhyButton
                    size="small"
                    onClick={() => onWhyClick?.(brand.brandName)}
                  />
                </div>

                <div className={styles.scoreRow}>
                  <ScoreRing score={brand.healthScore} />
                  <span className={styles.trendArrow} style={{ color: trend.color }}>
                    {trend.symbol}
                  </span>
                </div>

                <div className={styles.pills}>
                  {brand.topRisk && (
                    <span
                      className={styles.pill}
                      style={{ backgroundColor: SCORECARD_COLORS.redBg, color: SCORECARD_COLORS.red }}
                    >
                      ⚠ {brand.topRisk}
                    </span>
                  )}
                  {brand.topOpportunity && (
                    <span
                      className={styles.pill}
                      style={{ backgroundColor: SCORECARD_COLORS.greenBg, color: SCORECARD_COLORS.green }}
                    >
                      ★ {brand.topOpportunity}
                    </span>
                  )}
                </div>
              </div>
            );
          })
        )}
      </div>

      {generationTimeMs != null && !loading && (
        <div className={styles.genTime}>
          Generated in {(generationTimeMs / 1000).toFixed(1)}s
        </div>
      )}
    </div>
  );
}
