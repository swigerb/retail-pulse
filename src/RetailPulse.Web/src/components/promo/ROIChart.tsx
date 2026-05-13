import { useMemo } from 'react';
import {
  ResponsiveContainer,
  ComposedChart,
  Bar,
  Scatter,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ReferenceLine,
  Legend,
  ErrorBar,
  Cell,
} from 'recharts';
import { makeStyles, Card } from '@fluentui/react-components';
import { PROMO_COLORS } from '../../constants/agentRouting';

interface ROIChartProps {
  proposedRoi: number;
  proposedRoiLower: number;
  proposedRoiUpper: number;
  historicalAvgRoi: number;
  historicalCampaigns?: Array<{ name: string; roi: number }>;
  promoType: string;
}

const AXIS_TICK = { fill: '#94a3b8', fontSize: 12 } as const;

const tooltipContentStyle = {
  backgroundColor: '#1e1b2e',
  border: '1px solid rgba(34,197,94,0.3)',
  borderRadius: 8,
  color: '#f1f5f9',
  fontSize: 13,
} as const;

const useStyles = makeStyles({
  wrapper: {
    padding: '20px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.02))',
    border: '1px solid rgba(255,255,255,0.06)',
    borderRadius: '12px',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '16px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: '#22c55e',
  },
  badge: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '4px',
    backgroundColor: 'rgba(34,197,94,0.15)',
    color: '#86efac',
    fontWeight: '500',
  },
});

export default function ROIChart({
  proposedRoi,
  proposedRoiLower,
  proposedRoiUpper,
  historicalAvgRoi,
  historicalCampaigns = [],
  promoType,
}: ROIChartProps) {
  const styles = useStyles();

  const barData = useMemo(() => [
    {
      name: 'Proposed',
      roi: proposedRoi,
      errorLow: proposedRoi - proposedRoiLower,
      errorHigh: proposedRoiUpper - proposedRoi,
    },
    {
      name: `${promoType} Avg`,
      roi: historicalAvgRoi,
      errorLow: 0,
      errorHigh: 0,
    },
  ], [proposedRoi, proposedRoiLower, proposedRoiUpper, historicalAvgRoi, promoType]);

  const scatterData = useMemo(() =>
    historicalCampaigns.map((c, i) => ({
      name: c.name,
      x: i + 1,
      roi: c.roi,
    })),
    [historicalCampaigns],
  );

  return (
    <Card className={styles.wrapper} appearance="subtle" data-testid="roi-chart">
      <div className={styles.titleRow}>
        <span className={styles.title}>📊 ROI Comparison</span>
        <span className={styles.badge}>{promoType}</span>
      </div>

      <ResponsiveContainer width="100%" height={320}>
        <ComposedChart
          data={barData}
          margin={{ top: 20, right: 30, bottom: 24, left: 10 }}
        >
          <defs>
            <linearGradient id="roiBarGradient" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={PROMO_COLORS.roi} stopOpacity={0.9} />
              <stop offset="100%" stopColor={PROMO_COLORS.roi} stopOpacity={0.4} />
            </linearGradient>
          </defs>

          <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />

          <XAxis
            dataKey="name"
            tick={AXIS_TICK}
          />
          <YAxis
            tick={AXIS_TICK}
            label={{ value: 'ROI (x)', fill: '#94a3b8', angle: -90, position: 'insideLeft' }}
          />

          <Tooltip
            contentStyle={tooltipContentStyle}
            labelStyle={{ color: '#86efac' }}
            formatter={(value, name) => [
              typeof value === 'number' ? `${value.toFixed(1)}x` : String(value ?? ''),
              String(name),
            ]}
          />

          <Legend wrapperStyle={{ color: '#94a3b8', fontSize: 12, paddingTop: 12 }} />

          {/* Break-even line */}
          <ReferenceLine
            y={1}
            stroke={PROMO_COLORS.breakEven}
            strokeDasharray="6 4"
            strokeWidth={1.5}
            label={{
              value: 'Break Even (1.0x)',
              position: 'insideTopRight',
              fill: '#94a3b8',
              fontSize: 11,
            }}
          />

          <Bar
            dataKey="roi"
            name="ROI"
            barSize={48}
            radius={[4, 4, 0, 0]}
            isAnimationActive={true}
            animationDuration={800}
          >
            {barData.map((entry, index) => (
              <Cell
                key={index}
                fill={entry.roi >= 1 ? PROMO_COLORS.roi : PROMO_COLORS.roiBelow}
                fillOpacity={index === 0 ? 0.9 : 0.5}
              />
            ))}
            <ErrorBar
              dataKey="errorHigh"
              direction="y"
              stroke={PROMO_COLORS.confidence}
              strokeWidth={2}
            />
          </Bar>

          {scatterData.length > 0 && (
            <Scatter
              name="Historical Campaigns"
              data={scatterData}
              dataKey="roi"
              fill={PROMO_COLORS.historical}
              fillOpacity={0.5}
              shape="circle"
              legendType="circle"
            />
          )}
        </ComposedChart>
      </ResponsiveContainer>
    </Card>
  );
}
