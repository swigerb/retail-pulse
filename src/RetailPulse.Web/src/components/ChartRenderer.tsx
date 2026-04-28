import {
  ResponsiveContainer,
  LineChart, Line,
  BarChart, Bar,
  PieChart, Pie, Cell,
  XAxis, YAxis, CartesianGrid, Tooltip, Legend,
} from 'recharts';
import { Card, makeStyles } from '@fluentui/react-components';
import type { ChartSpec, ChartSeries } from '../types';

const BRAND_COLORS = ['#8B4513', '#DAA520', '#4682B4', '#2E8B57', '#CD853F', '#B8860B', '#5F9EA0', '#D2691E'];

const useStyles = makeStyles({
  chartCard: {
    marginTop: '12px',
    padding: '20px',
    backgroundColor: '#111111',
    border: '1px solid rgba(255, 255, 255, 0.08)',
    borderRadius: '12px',
  },
  chartTitle: {
    color: '#C8A951',
    fontSize: '15px',
    fontWeight: '600',
    marginBottom: '16px',
  },
  tableWrapper: {
    overflowX: 'auto',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '13px',
    color: '#F5F5F0',
  },
  th: {
    textAlign: 'left',
    padding: '8px 12px',
    borderBottom: '1px solid rgba(255,255,255,0.15)',
    color: '#C8A951',
    fontWeight: '600',
  },
  td: {
    padding: '6px 12px',
    borderBottom: '1px solid rgba(255,255,255,0.06)',
  },
  gaugeContainer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '20px',
  },
});

const tooltipStyle = {
  contentStyle: { backgroundColor: '#1A1A1A', border: '1px solid rgba(200,169,81,0.3)', borderRadius: 8, color: '#F5F5F0', fontSize: 13 },
  labelStyle: { color: '#C8A951' },
  itemStyle: { color: '#F5F5F0' },
};

function toRowData(seriesList: ChartSeries[]) {
  const map = new Map<string, Record<string, string | number>>();
  for (const s of seriesList) {
    for (const v of s.values) {
      if (!map.has(v.x)) map.set(v.x, { x: v.x });
      map.get(v.x)![s.legend] = v.y;
    }
  }
  return Array.from(map.values());
}

function seriesColor(s: ChartSeries, i: number) {
  return s.color || BRAND_COLORS[i % BRAND_COLORS.length];
}

function RenderLineChart({ spec }: { spec: ChartSpec }) {
  const rows = toRowData(spec.data);
  return (
    <ResponsiveContainer width="100%" height={300}>
      <LineChart data={rows}>
        <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.1)" />
        <XAxis dataKey="x" tick={{ fill: '#A0A0A0', fontSize: 12 }} label={spec.xAxisTitle ? { value: spec.xAxisTitle, fill: '#A0A0A0', position: 'insideBottom', offset: -5 } : undefined} />
        <YAxis tick={{ fill: '#A0A0A0', fontSize: 12 }} label={spec.yAxisTitle ? { value: spec.yAxisTitle, fill: '#A0A0A0', angle: -90, position: 'insideLeft' } : undefined} />
        <Tooltip {...tooltipStyle} />
        {spec.data.length > 1 && <Legend wrapperStyle={{ color: '#A0A0A0', fontSize: 12 }} />}
        {spec.data.map((s, i) => (
          <Line key={s.legend} type="monotone" dataKey={s.legend} stroke={seriesColor(s, i)} strokeWidth={2} dot={{ r: 3 }} activeDot={{ r: 5 }} />
        ))}
      </LineChart>
    </ResponsiveContainer>
  );
}

function RenderBarChart({ spec, stacked }: { spec: ChartSpec; stacked?: boolean }) {
  const rows = toRowData(spec.data);
  return (
    <ResponsiveContainer width="100%" height={300}>
      <BarChart data={rows}>
        <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.1)" />
        <XAxis dataKey="x" tick={{ fill: '#A0A0A0', fontSize: 12 }} label={spec.xAxisTitle ? { value: spec.xAxisTitle, fill: '#A0A0A0', position: 'insideBottom', offset: -5 } : undefined} />
        <YAxis tick={{ fill: '#A0A0A0', fontSize: 12 }} label={spec.yAxisTitle ? { value: spec.yAxisTitle, fill: '#A0A0A0', angle: -90, position: 'insideLeft' } : undefined} />
        <Tooltip {...tooltipStyle} />
        {spec.data.length > 1 && <Legend wrapperStyle={{ color: '#A0A0A0', fontSize: 12 }} />}
        {spec.data.map((s, i) => (
          <Bar key={s.legend} dataKey={s.legend} fill={seriesColor(s, i)} stackId={stacked ? 'stack' : undefined} />
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}

function RenderHorizontalBarChart({ spec }: { spec: ChartSpec }) {
  const rows = toRowData(spec.data);
  return (
    <ResponsiveContainer width="100%" height={Math.max(300, rows.length * 40)}>
      <BarChart data={rows} layout="vertical">
        <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.1)" />
        <XAxis type="number" tick={{ fill: '#A0A0A0', fontSize: 12 }} />
        <YAxis type="category" dataKey="x" tick={{ fill: '#A0A0A0', fontSize: 12 }} width={120} />
        <Tooltip {...tooltipStyle} />
        {spec.data.length > 1 && <Legend wrapperStyle={{ color: '#A0A0A0', fontSize: 12 }} />}
        {spec.data.map((s, i) => (
          <Bar key={s.legend} dataKey={s.legend} fill={seriesColor(s, i)} />
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}

function RenderPieChart({ spec, donut }: { spec: ChartSpec; donut?: boolean }) {
  const entries = spec.data[0]?.values.map((v, i) => ({
    name: v.x,
    value: v.y,
    fill: spec.data[0].color || BRAND_COLORS[i % BRAND_COLORS.length],
  })) ?? [];
  return (
    <ResponsiveContainer width="100%" height={300}>
      <PieChart>
        <Pie
          data={entries}
          dataKey="value"
          nameKey="name"
          cx="50%"
          cy="50%"
          innerRadius={donut ? 60 : 0}
          outerRadius={110}
          label={({ name, percent }) => `${name} ${((percent ?? 0) * 100).toFixed(0)}%`}
          labelLine={{ stroke: '#A0A0A0' }}
        >
          {entries.map((e, i) => (
            <Cell key={i} fill={e.fill || BRAND_COLORS[i % BRAND_COLORS.length]} />
          ))}
        </Pie>
        <Tooltip {...tooltipStyle} />
        <Legend wrapperStyle={{ color: '#A0A0A0', fontSize: 12 }} />
      </PieChart>
    </ResponsiveContainer>
  );
}

function RenderGauge({ spec }: { spec: ChartSpec }) {
  const styles = useStyles();
  const value = spec.data[0]?.values[0]?.y ?? 0;
  const label = spec.data[0]?.values[0]?.x ?? '';
  const pct = Math.min(100, Math.max(0, value));
  const angle = (pct / 100) * 180;
  return (
    <div className={styles.gaugeContainer}>
      <svg width={220} height={130} viewBox="0 0 220 130">
        <path d="M 20 120 A 90 90 0 0 1 200 120" fill="none" stroke="rgba(255,255,255,0.1)" strokeWidth={16} strokeLinecap="round" />
        <path d="M 20 120 A 90 90 0 0 1 200 120" fill="none" stroke="#C8A951" strokeWidth={16} strokeLinecap="round"
          strokeDasharray={`${(angle / 180) * 283} 283`} />
        <text x="110" y="100" textAnchor="middle" fill="#F5F5F0" fontSize="28" fontWeight="bold">{value}%</text>
        <text x="110" y="122" textAnchor="middle" fill="#A0A0A0" fontSize="12">{label}</text>
      </svg>
    </div>
  );
}

function RenderTable({ spec }: { spec: ChartSpec }) {
  const styles = useStyles();
  const headers = spec.data.map(s => s.legend);
  const xValues = [...new Set(spec.data.flatMap(s => s.values.map(v => v.x)))];
  const lookup = new Map<string, Map<string, number>>();
  for (const s of spec.data) {
    const m = new Map<string, number>();
    for (const v of s.values) m.set(v.x, v.y);
    lookup.set(s.legend, m);
  }
  return (
    <div className={styles.tableWrapper}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th className={styles.th}>{spec.xAxisTitle || ''}</th>
            {headers.map(h => <th key={h} className={styles.th}>{h}</th>)}
          </tr>
        </thead>
        <tbody>
          {xValues.map(x => (
            <tr key={x}>
              <td className={styles.td}>{x}</td>
              {headers.map(h => <td key={h} className={styles.td}>{lookup.get(h)?.get(x) ?? '—'}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function SingleChart({ spec }: { spec: ChartSpec }) {
  const styles = useStyles();
  const t = spec.type.toLowerCase();
  let content: React.ReactNode;
  switch (t) {
    case 'line':
      content = <RenderLineChart spec={spec} />;
      break;
    case 'bar':
    case 'groupedbar':
      content = <RenderBarChart spec={spec} />;
      break;
    case 'stackedbar':
      content = <RenderBarChart spec={spec} stacked />;
      break;
    case 'horizontalbar':
      content = <RenderHorizontalBarChart spec={spec} />;
      break;
    case 'pie':
      content = <RenderPieChart spec={spec} />;
      break;
    case 'donut':
      content = <RenderPieChart spec={spec} donut />;
      break;
    case 'gauge':
      content = <RenderGauge spec={spec} />;
      break;
    case 'table':
      content = <RenderTable spec={spec} />;
      break;
    default:
      content = <RenderTable spec={spec} />;
      break;
  }
  return (
    <Card className={styles.chartCard} appearance="subtle">
      <div className={styles.chartTitle}>{spec.title}</div>
      {content}
    </Card>
  );
}

export function ChartRenderer({ charts }: { charts: ChartSpec[] }) {
  return (
    <>
      {charts.map((spec, i) => (
        <SingleChart key={i} spec={spec} />
      ))}
    </>
  );
}
