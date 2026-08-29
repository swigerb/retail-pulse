import { makeStyles } from '@fluentui/react-components';
import {
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  Radar,
  ResponsiveContainer,
} from 'recharts';
import type { BrandScore, ScorecardDimensionKey } from '../../types';
import { SCORECARD_COLORS } from '../../constants/agentRouting';
import {
  describeDimension,
  formatCompositeCalculation,
  getBrandDimensionDetails,
  getCompositeBand,
  getTrendDisclosure,
} from '../../scorecardModel';
import { WhyButton } from './WhyButton';

interface BrandScoreCardProps {
  brand: BrandScore;
  onWhyClick?: (dimension: ScorecardDimensionKey) => void;
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
  explainer: {
    marginBottom: '20px',
    padding: '16px',
    borderRadius: '12px',
    backgroundColor: 'rgba(255,255,255,0.04)',
    border: `1px solid ${SCORECARD_COLORS.cardBorder}`,
  },
  explainerTitle: {
    fontSize: '13px',
    fontWeight: '700',
    color: '#f1f5f9',
    marginBottom: '8px',
  },
  explainerText: {
    fontSize: '13px',
    lineHeight: '1.5',
    color: '#cbd5e1',
    margin: '0 0 8px 0',
  },
  sourceText: {
    fontSize: '12px',
    lineHeight: '1.4',
    color: '#94a3b8',
    margin: 0,
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
  dimInsight: {
    fontSize: '12px',
    lineHeight: '1.45',
    color: '#cbd5e1',
    marginLeft: '100px',
    marginTop: '-4px',
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
  store: { label: 'Store', color: SCORECARD_COLORS.dimensionStore },
};

export function BrandScoreCard({ brand, onWhyClick }: BrandScoreCardProps) {
  const styles = useStyles();
  const scoreColor = getScoreColor(brand.healthScore);
  const trend = getTrendArrow(brand.trend);
  const dimensionDetails = getBrandDimensionDetails(brand);
  const band = getCompositeBand(brand.healthScore);

  const radarData = dimensionDetails.map((detail) => ({
    dimension: DIMENSION_META[detail.key]?.label ?? detail.shortLabel,
    value: detail.score,
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

      <div className={styles.explainer}>
        <div className={styles.explainerTitle}>What this score means</div>
        <p className={styles.explainerText}>
          {brand.healthScore}/100 is a {band.label} score: {band.description}.
        </p>
        <p className={styles.explainerText}>
          {formatCompositeCalculation(dimensionDetails, brand.healthScore)}
        </p>
        <p className={styles.explainerText}>
          The API returns each specialist score on a 1 to 10 scale and this view shows it on a 0 to 100 scale.
        </p>
        <p className={styles.explainerText}>{getTrendDisclosure(brand)}</p>
        <p className={styles.sourceText}>
          Numbers come from the scorecard specialist assessments returned by /api/scorecard.
        </p>
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
        {dimensionDetails.map((detail) => {
          const meta = DIMENSION_META[detail.key];
          return (
            <div key={detail.key}>
              <div className={styles.dimRow}>
                <span className={styles.dimLabel}>{meta?.label ?? detail.shortLabel}</span>
                <div className={styles.dimBarTrack}>
                  <div
                    style={{
                      width: `${detail.score}%`,
                      height: '100%',
                      borderRadius: '3px',
                      backgroundColor: meta?.color ?? '#6366f1',
                      transition: 'width 0.5s ease',
                    }}
                  />
                </div>
                <span className={styles.dimValue}>{Math.round(detail.score)}</span>
                <WhyButton
                  size="small"
                  onClick={() => onWhyClick?.(detail.key)}
                  ariaLabel={`Explain ${detail.shortLabel} score`}
                />
              </div>
              <div className={styles.dimInsight}>{describeDimension(detail, brand.brandName)}</div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
