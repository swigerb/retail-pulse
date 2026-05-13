import { useMemo } from 'react';
import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Cell,
  ReferenceLine,
} from 'recharts';
import { makeStyles } from '@fluentui/react-components';
import { MARGIN_COLORS } from '../../constants/agentRouting';
import type { MarginWaterfallStep } from '../../types';

interface MarginWaterfallProps {
  steps: MarginWaterfallStep[];
  title?: string;
  comparisonSteps?: MarginWaterfallStep[];
}

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
    backgroundColor: MARGIN_COLORS.cardBg,
    border: `1px solid ${MARGIN_COLORS.cardBorder}`,
    borderRadius: '12px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: '#6366f1',
    marginBottom: '16px',
  },
  empty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '200px',
    color: '#94a3b8',
    fontSize: '14px',
  },
});

interface WaterfallRow {
  label: string;
  base: number;
  value: number;
  raw: number;
  pct: string;
  color: string;
  compBase?: number;
  compValue?: number;
}

function formatCurrency(v: number): string {
  const abs = Math.abs(v);
  if (abs >= 1_000_000) return `$${(v / 1_000_000).toFixed(1)}M`;
  if (abs >= 1_000) return `$${(v / 1_000).toFixed(0)}k`;
  return `$${v.toFixed(0)}`;
}

function computeRows(
  steps: MarginWaterfallStep[],
  revenueBase: number,
): { base: number; value: number; raw: number; pct: string; color: string }[] {
  let running = 0;
  return steps.map((s) => {
    if (s.isSubtotal) {
      const row = {
        base: 0,
        value: running,
        raw: running,
        pct: revenueBase ? `${((running / revenueBase) * 100).toFixed(1)}%` : '',
        color: MARGIN_COLORS.waterfallSubtotal,
      };
      return row;
    }
    const start = running;
    running += s.value;
    const barValue = s.value;
    return {
      base: barValue >= 0 ? start : start + barValue,
      value: Math.abs(barValue),
      raw: barValue,
      pct: revenueBase ? `${((barValue / revenueBase) * 100).toFixed(1)}%` : '',
      color: barValue >= 0 ? MARGIN_COLORS.waterfallPositive : MARGIN_COLORS.waterfallNegative,
    };
  });
}

export function MarginWaterfall({ steps, title, comparisonSteps }: MarginWaterfallProps) {
  const styles = useStyles();

  const data: WaterfallRow[] = useMemo(() => {
    if (!steps.length) return [];
    const revenueBase = steps[0]?.value ?? 1;
    const primary = computeRows(steps, revenueBase);

    let comparison: ReturnType<typeof computeRows> | null = null;
    if (comparisonSteps?.length) {
      comparison = computeRows(comparisonSteps, comparisonSteps[0]?.value ?? 1);
    }

    return steps.map((s, i) => ({
      label: s.label,
      ...primary[i],
      ...(comparison?.[i]
        ? { compBase: comparison[i].base, compValue: comparison[i].value }
        : {}),
    }));
  }, [steps, comparisonSteps]);

  if (!steps.length) {
    return (
      <div className={styles.wrapper}>
        {title && <div className={styles.title}>{title}</div>}
        <div className={styles.empty}>No margin data available</div>
      </div>
    );
  }

  return (
    <div className={styles.wrapper} data-testid="margin-waterfall">
      {title && <div className={styles.title}>{title}</div>}
      <ResponsiveContainer width="100%" height={380}>
        <BarChart data={data} margin={{ top: 20, right: 20, bottom: 24, left: 10 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" vertical={false} />
          <XAxis
            dataKey="label"
            tick={AXIS_TICK}
            axisLine={{ stroke: 'rgba(255,255,255,0.1)' }}
            tickLine={false}
          />
          <YAxis
            tick={AXIS_TICK}
            tickFormatter={(v: number) => formatCurrency(v)}
            axisLine={false}
            tickLine={false}
          />
          <Tooltip
            contentStyle={tooltipContentStyle}
            labelStyle={{ color: '#a5b4fc' }}
            labelFormatter={(label) => String(label)}
            formatter={(value, name) => {
              if (name === 'base' || name === 'compBase') return [null, null];
              return [formatCurrency(value as number), name === 'compValue' ? 'Comparison' : 'Value'];
            }}
          />
          <ReferenceLine y={0} stroke="rgba(255,255,255,0.15)" />

          {/* Transparent base */}
          <Bar dataKey="base" stackId="primary" fill="transparent" isAnimationActive={false} />
          {/* Colored value bar */}
          <Bar
            dataKey="value"
            stackId="primary"
            isAnimationActive={true}
            animationDuration={800}
            label={{
              position: 'top',
              fill: '#94a3b8',
              fontSize: 11,
              formatter: ((...args: unknown[]) => {
                const props = args[2] as { index?: number } | undefined;
                const idx = props?.index;
                return idx != null && data[idx] ? data[idx].pct : '';
              }) as (value: unknown) => string,
            }}
          >
            {data.map((row, i) => (
              <Cell key={`cell-${i}`} fill={row.color} />
            ))}
          </Bar>

          {/* Comparison overlay bars */}
          {comparisonSteps?.length && (
            <>
              <Bar
                dataKey="compBase"
                stackId="comparison"
                fill="transparent"
                isAnimationActive={false}
              />
              <Bar
                dataKey="compValue"
                stackId="comparison"
                opacity={0.4}
                isAnimationActive={true}
                animationDuration={800}
              >
                {data.map((row, i) => (
                  <Cell key={`comp-${i}`} fill={row.color} />
                ))}
              </Bar>
            </>
          )}
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}
