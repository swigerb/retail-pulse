import { makeStyles } from '@fluentui/react-components';
import {
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  Radar,
  ResponsiveContainer,
} from 'recharts';
import type { BrandScore } from '../../types';
import { SCORECARD_COLORS } from '../../constants/agentRouting';
import { WhyButton } from './WhyButton';

interface BrandScoreCardProps {
  brand: BrandScore;
  onWhyClick?: (dimension: string) => void;
}

const useStyles = makeStyles({
  container: {
    padding: '24px',
    backgroundColor: SCORECARD_COLORS.cardBg,
    border: `1px solid ${SCORECARD_COLORS.cardBorder}`,
    borderRadius: '16px',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '10px',
    marginBottom: '8px',
  },
  brandName: {
    fontSize: '18px',
    fontWeight: '700',
    color: '#f1f5f9',
    letterSpacing: '-0.3px',
  },
  scoreBlock: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    marginBottom: '20px',
  },
  score: {
    fontSize: '72px',
    fontWeight: '800',
    lineHeight: 1,
    letterSpacing: '-2px',
  },
  trend: {
    fontSize: '28px',
    lineHeight: 1,
  },
  radarWrap: {
    width: '100%',
    height: '240px',
    marginBottom: '20px',
  },
  dimensions: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  dimRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  dimLabel: {
    fontSize: '13px',
    color: '#94a3b8',
    width: '90px',
    flexShrink: 0,
    textTransform: 'capitalize',
  },
  dimBarTrack: {
    flex: 1,
    height: '6px',
    borderRadius: '3px',
    backgroundColor: 'rgba(255,255,255,0.06)',
    overflow: 'hidden',
  },
  dimValue: {
    fontSize: '13px',
    fontWeight: '600',
    color: '#e2e8f0',
    width: '32px',
    textAlign: 'right',
  },
});

function getScoreColor(score: number) {
  if (score > 75) return SCORECARD_COLORS.green;
  if (score >= 50) return SCORECARD_COLORS.amber;
  return SCORECARD_COLORS.red;
}

function getTrendArrow(trend: 'up' | 'down' | 'stable') {
  if (trend === 'up') return { symbol: '↑', color: SCORECARD_COLORS.green };
  if (trend === 'down') return { symbol: '↓', color: SCORECARD_COLORS.red };
  return { symbol: '→', color: '#64748b' };
}

const DIMENSION_META: Record<string, { label: string; color: string }> = {
  demand: { label: 'Demand', color: SCORECARD_COLORS.dimensionDemand },
  margin: { label: 'Margin', color: SCORECARD_COLORS.dimensionMargin },
  competitive: { label: 'Competitive', color: SCORECARD_COLORS.dimensionCompetitive },
  supply: { label: 'Supply', color: SCORECARD_COLORS.dimensionSupply },
};

export function BrandScoreCard({ brand, onWhyClick }: BrandScoreCardProps) {
  const styles = useStyles();
  const scoreColor = getScoreColor(brand.healthScore);
  const trend = getTrendArrow(brand.trend);

  const radarData = Object.entries(brand.dimensions).map(([key, value]) => ({
    dimension: DIMENSION_META[key]?.label ?? key,
    value,
    fullMark: 100,
  }));

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <span className={styles.brandName}>{brand.brandName}</span>
      </div>

      <div className={styles.scoreBlock}>
        <span className={styles.score} style={{ color: scoreColor }}>
          {brand.healthScore}
        </span>
        <span className={styles.trend} style={{ color: trend.color }}>
          {trend.symbol}
        </span>
      </div>

      <div className={styles.radarWrap}>
        <ResponsiveContainer width="100%" height="100%">
          <RadarChart data={radarData} cx="50%" cy="50%" outerRadius="75%">
            <PolarGrid stroke="rgba(255,255,255,0.08)" />
            <PolarAngleAxis
              dataKey="dimension"
              tick={{ fill: '#94a3b8', fontSize: 12 }}
            />
            <PolarRadiusAxis
              angle={90}
              domain={[0, 100]}
              tick={{ fill: '#64748b', fontSize: 10 }}
              axisLine={false}
            />
            <Radar
              name="Score"
              dataKey="value"
              stroke={scoreColor}
              fill={scoreColor}
              fillOpacity={0.3}
            />
          </RadarChart>
        </ResponsiveContainer>
      </div>

      <div className={styles.dimensions}>
        {Object.entries(brand.dimensions).map(([key, value]) => {
          const meta = DIMENSION_META[key];
          return (
            <div key={key} className={styles.dimRow}>
              <span className={styles.dimLabel}>{meta?.label ?? key}</span>
              <div className={styles.dimBarTrack}>
                <div
                  style={{
                    width: `${value}%`,
                    height: '100%',
                    borderRadius: '3px',
                    backgroundColor: meta?.color ?? '#6366f1',
                    transition: 'width 0.5s ease',
                  }}
                />
              </div>
              <span className={styles.dimValue}>{value}</span>
              <WhyButton size="small" onClick={() => onWhyClick?.(key)} />
            </div>
          );
        })}
      </div>
    </div>
  );
}
