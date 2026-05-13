import { useState, useEffect } from 'react';
import { makeStyles } from '@fluentui/react-components';
import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  CartesianGrid,
  Area,
  AreaChart,
} from 'recharts';
import { OBSERVABILITY_COLORS } from '../../constants/agentRouting';
import { fetchCostDashboard } from '../../services/observabilityApi';
import type { ObservabilityPeriod, CostDashboardData } from '../../types';

const PERIODS: { key: ObservabilityPeriod; label: string }[] = [
  { key: 'today', label: 'Today' },
  { key: 'week', label: 'This Week' },
  { key: 'month', label: 'This Month' },
];

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
  },
  periodTabs: {
    display: 'flex',
    gap: '4px',
    background: 'rgba(255,255,255,0.04)',
    borderRadius: '10px',
    padding: '4px',
    width: 'fit-content',
  },
  periodTab: {
    padding: '8px 20px',
    borderRadius: '8px',
    border: 'none',
    cursor: 'pointer',
    fontSize: '13px',
    fontWeight: '600',
    transition: 'all 0.25s ease',
    color: OBSERVABILITY_COLORS.tabInactive,
    background: 'transparent',
  },
  periodTabActive: {
    padding: '8px 20px',
    borderRadius: '8px',
    border: 'none',
    cursor: 'pointer',
    fontSize: '13px',
    fontWeight: '600',
    transition: 'all 0.25s ease',
    color: '#fff',
    background: `${OBSERVABILITY_COLORS.tabActive}22`,
    boxShadow: `0 0 12px ${OBSERVABILITY_COLORS.tabActive}30`,
  },
  summaryStrip: {
    display: 'flex',
    gap: '16px',
    flexWrap: 'wrap',
  },
  metricCard: {
    flex: '1',
    minWidth: '180px',
    background: OBSERVABILITY_COLORS.cardBg,
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    borderRadius: '12px',
    padding: '18px',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    transition: 'all 0.3s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.05)',
    },
  },
  metricIcon:{
    fontSize: '22px',
  },
  metricLabel: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    fontWeight: '500',
  },
  metricValue: {
    fontSize: '26px',
    fontWeight: '800',
    letterSpacing: '-0.5px',
  },
  chartSection: {
    background: OBSERVABILITY_COLORS.cardBg,
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    borderRadius: '12px',
    padding: '20px',
  },
  chartTitle: {
    fontSize: '14px',
    fontWeight: '700',
    color: 'var(--color-text)',
    marginBottom: '16px',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  toolsTable: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '13px',
  },
  tableHead: {
    textAlign: 'left',
    padding: '10px 14px',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.8px',
    borderBottom: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    fontWeight: '600',
  },
  tableCell: {
    padding: '10px 14px',
    color: 'var(--color-text)',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
  },
  tableCellMuted: {
    padding: '10px 14px',
    color: 'var(--color-text-muted)',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
  },
  skeleton: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  skeletonBar: {
    height: '60px',
    borderRadius: '12px',
    background: 'rgba(255,255,255,0.04)',
    animationName: {
      '0%, 100%': { opacity: 0.4 },
      '50%': { opacity: 0.8 },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
  },
  skeletonChart: {
    height: '220px',
    borderRadius: '12px',
    background: 'rgba(255,255,255,0.04)',
    animationName: {
      '0%, 100%': { opacity: 0.4 },
      '50%': { opacity: 0.8 },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
    animationDelay: '0.3s',
  },
  error: {
    padding: '16px',
    borderRadius: '8px',
    backgroundColor: 'rgba(211,47,47,0.1)',
    border: '1px solid rgba(211,47,47,0.3)',
    color: '#fca5a5',
    fontSize: '13px',
  },
  chartRows: {
    display: 'flex',
    gap: '16px',
    flexWrap: 'wrap',
  },
  chartHalf: {
    flex: '1',
    minWidth: '320px',
    background: OBSERVABILITY_COLORS.cardBg,
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    borderRadius: '12px',
    padding: '20px',
  },
});

const METRIC_CARDS = [
  { key: 'totalTokens', label: 'Total Tokens', icon: '🔤', color: OBSERVABILITY_COLORS.tokens, fmt: (v: number) => v.toLocaleString() },
  { key: 'totalCost', label: 'Total Cost', icon: '💰', color: OBSERVABILITY_COLORS.cost, fmt: (v: number) => `$${v.toFixed(2)}` },
  { key: 'requestCount', label: 'Requests', icon: '📡', color: OBSERVABILITY_COLORS.requests, fmt: (v: number) => v.toLocaleString() },
  { key: 'avgCostPerRequest', label: 'Avg Cost / Req', icon: '📊', color: OBSERVABILITY_COLORS.avgCost, fmt: (v: number) => `$${v.toFixed(4)}` },
] as const;

function CustomTooltip({ active, payload, label }: { active?: boolean; payload?: Array<{ value: number }>; label?: React.ReactNode }) {
  if (!active || !payload?.length) return null;
  return (
    <div style={{
      background: 'var(--color-bg-elevated)',
      border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
      borderRadius: '8px',
      padding: '10px 14px',
      fontSize: '12px',
      color: 'var(--color-text)',
    }}>
      <div style={{ color: 'var(--color-text-muted)', marginBottom: '4px' }}>{String(label)}</div>
      <div style={{ fontWeight: 700, color: OBSERVABILITY_COLORS.trendLine }}>
        ${payload[0].value.toFixed(4)}
      </div>
    </div>
  );
}

export default function CostDashboard() {
  const styles = useStyles();
  const [period, setPeriod] = useState<ObservabilityPeriod>('week');
  const [data, setData] = useState<CostDashboardData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetchCostDashboard(period)
      .then(result => {
        if (cancelled) return;
        setData(result);
        setError(null);
        setLoading(false);
      })
      .catch(e => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : 'Failed to load cost data');
        setLoading(false);
      });
    return () => { cancelled = true; };
  }, [period]);

  const handlePeriodChange = (p: ObservabilityPeriod) => {
    setPeriod(p);
    setLoading(true);
    setError(null);
  };

  return (
    <div className={styles.container} data-testid="cost-dashboard">
      {/* Period Tabs */}
      <div className={styles.periodTabs} data-testid="period-tabs">
        {PERIODS.map(p => (
          <button
            key={p.key}
            className={period === p.key ? styles.periodTabActive : styles.periodTab}
            onClick={() => handlePeriodChange(p.key)}
            data-testid={`period-tab-${p.key}`}
          >
            {p.label}
          </button>
        ))}
      </div>

      {error && (
        <div className={styles.error} data-testid="cost-error">⚠️ {error}</div>
      )}

      {loading && (
        <div className={styles.skeleton} data-testid="cost-skeleton">
          <div className={styles.skeletonBar} />
          <div className={styles.skeletonChart} />
          <div className={styles.skeletonBar} />
        </div>
      )}

      {!loading && data && (
        <>
          {/* Summary Strip */}
          <div className={styles.summaryStrip} data-testid="summary-strip">
            {METRIC_CARDS.map(m => {
              const value = data.summary[m.key];
              return (
                <div key={m.key} className={styles.metricCard} data-testid={`metric-${m.key}`}>
                  <span className={styles.metricIcon}>{m.icon}</span>
                  <span className={styles.metricLabel}>{m.label}</span>
                  <span className={styles.metricValue} style={{ color: m.color }}>
                    {m.fmt(value)}
                  </span>
                </div>
              );
            })}
          </div>

          {/* Charts row */}
          <div className={styles.chartRows}>
            {/* Trend Chart */}
            <div className={styles.chartHalf}>
              <div className={styles.chartTitle}>📈 Cost Trend</div>
              <ResponsiveContainer width="100%" height={220}>
                <AreaChart data={data.trend}>
                  <defs>
                    <linearGradient id="costGradient" x1="0" y1="0" x2="0" y2="1">
                      <stop offset="5%" stopColor={OBSERVABILITY_COLORS.trendLine} stopOpacity={0.3} />
                      <stop offset="95%" stopColor={OBSERVABILITY_COLORS.trendLine} stopOpacity={0} />
                    </linearGradient>
                  </defs>
                  <CartesianGrid strokeDasharray="3 3" stroke={OBSERVABILITY_COLORS.gridLine} />
                  <XAxis
                    dataKey="date"
                    tick={{ fill: 'var(--color-text-muted)', fontSize: 11 }}
                    axisLine={{ stroke: OBSERVABILITY_COLORS.gridLine }}
                    tickLine={false}
                  />
                  <YAxis
                    tick={{ fill: 'var(--color-text-muted)', fontSize: 11 }}
                    axisLine={{ stroke: OBSERVABILITY_COLORS.gridLine }}
                    tickLine={false}
                    tickFormatter={(v: number) => `$${v.toFixed(2)}`}
                  />
                  <Tooltip content={<CustomTooltip />} />
                  <Area
                    type="monotone"
                    dataKey="cost"
                    stroke={OBSERVABILITY_COLORS.trendLine}
                    strokeWidth={2}
                    fill="url(#costGradient)"
                  />
                </AreaChart>
              </ResponsiveContainer>
            </div>

            {/* Agent Breakdown */}
            <div className={styles.chartHalf}>
              <div className={styles.chartTitle}>🤖 Agent Cost Breakdown</div>
              <ResponsiveContainer width="100%" height={220}>
                <BarChart data={data.agentBreakdown} layout="vertical">
                  <CartesianGrid strokeDasharray="3 3" stroke={OBSERVABILITY_COLORS.gridLine} horizontal={false} />
                  <XAxis
                    type="number"
                    tick={{ fill: 'var(--color-text-muted)', fontSize: 11 }}
                    axisLine={{ stroke: OBSERVABILITY_COLORS.gridLine }}
                    tickLine={false}
                    tickFormatter={(v: number) => `$${v.toFixed(2)}`}
                  />
                  <YAxis
                    dataKey="agentName"
                    type="category"
                    tick={{ fill: 'var(--color-text-muted)', fontSize: 11 }}
                    axisLine={{ stroke: OBSERVABILITY_COLORS.gridLine }}
                    tickLine={false}
                    width={110}
                  />
                  <Tooltip
                    labelFormatter={(label: React.ReactNode) => String(label)}
                    formatter={(value) => [`$${Number(value).toFixed(4)}`, 'Cost']}
                    contentStyle={{
                      background: 'var(--color-bg-elevated)',
                      border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
                      borderRadius: '8px',
                      fontSize: '12px',
                      color: 'var(--color-text)',
                    }}
                  />
                  <Bar
                    dataKey="totalCost"
                    fill={OBSERVABILITY_COLORS.barFill}
                    radius={[0, 6, 6, 0]}
                    maxBarSize={28}
                  />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </div>

          {/* Top Tools Table */}
          <div className={styles.chartSection}>
            <div className={styles.chartTitle}>🔧 Top Tools</div>
            <table className={styles.toolsTable} data-testid="tools-table">
              <thead>
                <tr>
                  <th className={styles.tableHead}>Tool</th>
                  <th className={styles.tableHead}>Calls</th>
                  <th className={styles.tableHead}>Total Tokens</th>
                  <th className={styles.tableHead}>Avg Duration</th>
                </tr>
              </thead>
              <tbody>
                {data.topTools.map(tool => (
                  <tr key={tool.toolName}>
                    <td className={styles.tableCell} style={{ fontWeight: 600 }}>{tool.toolName}</td>
                    <td className={styles.tableCellMuted}>{tool.callCount.toLocaleString()}</td>
                    <td className={styles.tableCellMuted}>{tool.totalTokens.toLocaleString()}</td>
                    <td className={styles.tableCellMuted}>{tool.avgDurationMs.toFixed(0)}ms</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
