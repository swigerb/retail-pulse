import { useMemo } from 'react';
import {
  ResponsiveContainer,
  ComposedChart,
  Line,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ReferenceLine,
  ReferenceArea,
} from 'recharts';
import { makeStyles, Card } from '@fluentui/react-components';
import { FORECAST_COLORS, SEASONAL_COLORS } from '../../constants/agentRouting';
import type { ForecastData } from '../../types';
import ForecastSummary from './ForecastSummary';
import DemandRiskCards from './DemandRiskCards';

const AXIS_TICK = { fill: '#94a3b8', fontSize: 12 } as const;

const tooltipContentStyle = {
  backgroundColor: '#1e1b2e',
  border: '1px solid rgba(139,92,246,0.3)',
  borderRadius: 8,
  color: '#f1f5f9',
  fontSize: 13,
} as const;

const useStyles = makeStyles({
  wrapper: {
    padding: '20px',
    backgroundColor: 'var(--color-surface-alt)',
    border: '1px solid var(--color-border)',
    borderRadius: '12px',
    marginTop: '12px',
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
    color: '#6366f1',
  },
  badge: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '4px',
    backgroundColor: 'rgba(99,102,241,0.15)',
    color: '#a5b4fc',
    fontWeight: '500',
  },
});

interface ChartRow {
  date: string;
  actual?: number;
  predicted?: number;
  upper?: number;
  lower?: number;
  confidenceBand?: [number, number];
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
}

export default function ForecastChart({ data }: { data: ForecastData }) {
  const styles = useStyles();

  const { rows, todayDate } = useMemo(() => {
    const map = new Map<string, ChartRow>();

    for (const h of data.historical) {
      map.set(h.date, { date: h.date, actual: h.actual });
    }

    for (const p of data.predicted) {
      const existing = map.get(p.date);
      if (existing) {
        existing.predicted = p.value;
        existing.upper = p.upper;
        existing.lower = p.lower;
        existing.confidenceBand = [p.lower, p.upper];
      } else {
        map.set(p.date, {
          date: p.date,
          predicted: p.value,
          upper: p.upper,
          lower: p.lower,
          confidenceBand: [p.lower, p.upper],
        });
      }
    }

    const sorted = Array.from(map.values()).sort(
      (a, b) => new Date(a.date).getTime() - new Date(b.date).getTime(),
    );

    // Bridge: connect actual line to forecast line at the boundary
    const lastHistorical = data.historical.length > 0
      ? data.historical[data.historical.length - 1]
      : null;
    const today = lastHistorical?.date ?? data.period.start;

    if (lastHistorical) {
      const bridgeRow = sorted.find((r) => r.date === lastHistorical.date);
      if (bridgeRow && data.predicted.length > 0) {
        bridgeRow.predicted = lastHistorical.actual;
        bridgeRow.upper = lastHistorical.actual;
        bridgeRow.lower = lastHistorical.actual;
        bridgeRow.confidenceBand = [lastHistorical.actual, lastHistorical.actual];
      }
    }

    return { rows: sorted, todayDate: today };
  }, [data]);

  const chartTitle = `${data.brand} Demand Forecast — ${data.region}`;

  return (
    <Card className={styles.wrapper} appearance="subtle" data-testid="forecast-chart">
      <div className={styles.titleRow}>
        <span className={styles.title}>{chartTitle}</span>
        <span className={styles.badge}>
          {formatDate(data.period.start)} → {formatDate(data.period.end)}
        </span>
      </div>

      <ForecastSummary data={data} />

      <ResponsiveContainer width="100%" height={360}>
        <ComposedChart data={rows} margin={{ top: 10, right: 20, bottom: 24, left: 10 }}>
          <defs>
            <linearGradient id="confidenceGradient" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="#8b5cf6" stopOpacity={0.18} />
              <stop offset="100%" stopColor="#8b5cf6" stopOpacity={0.04} />
            </linearGradient>
          </defs>

          <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />

          <XAxis
            dataKey="date"
            tick={AXIS_TICK}
            tickFormatter={formatDate}
            label={{ value: 'Date', fill: '#94a3b8', position: 'insideBottom', offset: 0 }}
          />
          <YAxis
            tick={AXIS_TICK}
            label={{ value: 'Volume', fill: '#94a3b8', angle: -90, position: 'insideLeft' }}
          />

          <Tooltip
            contentStyle={tooltipContentStyle}
            labelStyle={{ color: '#a5b4fc' }}
            labelFormatter={(label) => formatDate(String(label))}
            formatter={(value, name) => [
              typeof value === 'number' ? value.toLocaleString() : String(value ?? ''),
              name === 'confidenceBand' ? 'Confidence Range' : String(name),
            ]}
          />

          <Legend
            wrapperStyle={{ color: '#94a3b8', fontSize: 12, paddingTop: 12 }}
          />

          {/* Seasonal annotation bands */}
          {data.seasonality.map((s) => (
            <ReferenceArea
              key={`season-${s.factor}`}
              x1={s.startDate}
              x2={s.endDate}
              fill={SEASONAL_COLORS[s.factor.toLowerCase()] ?? SEASONAL_COLORS.default}
              label={{
                value: `${s.factor}`,
                position: 'insideTopLeft',
                fill: '#94a3b8',
                fontSize: 10,
              }}
            />
          ))}

          {/* Today divider */}
          <ReferenceLine
            x={todayDate}
            stroke={FORECAST_COLORS.todayLine}
            strokeDasharray="6 4"
            strokeWidth={1.5}
            label={{
              value: 'Today',
              position: 'insideTopRight',
              fill: '#94a3b8',
              fontSize: 11,
            }}
          />

          {/* Confidence band */}
          <Area
            type="monotone"
            dataKey="confidenceBand"
            fill="url(#confidenceGradient)"
            stroke="none"
            name="Confidence Band"
            legendType="rect"
            isAnimationActive={true}
            animationDuration={1200}
          />

          {/* Actual line */}
          <Line
            type="monotone"
            dataKey="actual"
            stroke={FORECAST_COLORS.actualLine}
            strokeWidth={2.5}
            dot={{ r: 3, fill: FORECAST_COLORS.actualLine }}
            activeDot={{ r: 5 }}
            name="Actual"
            isAnimationActive={true}
            animationDuration={1000}
            connectNulls={false}
          />

          {/* Predicted line */}
          <Line
            type="monotone"
            dataKey="predicted"
            stroke={FORECAST_COLORS.forecastLine}
            strokeWidth={2.5}
            strokeDasharray="8 4"
            dot={{ r: 3, fill: FORECAST_COLORS.forecastLine }}
            activeDot={{ r: 5 }}
            name="Predicted"
            isAnimationActive={true}
            animationDuration={1400}
            animationBegin={400}
            connectNulls={false}
          />
        </ComposedChart>
      </ResponsiveContainer>

      <DemandRiskCards risks={data.risks} />
    </Card>
  );
}
