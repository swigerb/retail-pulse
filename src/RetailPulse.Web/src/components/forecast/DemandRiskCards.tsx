import { useState } from 'react';
import { makeStyles } from '@fluentui/react-components';
import type { ForecastData } from '../../types';

const SEVERITY_ORDER: Record<string, number> = { high: 0, medium: 1, low: 2 };
const SEVERITY_ICONS: Record<string, string> = { high: '🔴', medium: '🟡', low: '🟢' };

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    marginTop: '16px',
  },
  heading: {
    fontSize: '13px',
    fontWeight: '600',
    color: '#94a3b8',
    textTransform: 'uppercase' as const,
    letterSpacing: '0.5px',
    marginBottom: '4px',
  },
  card: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '10px',
    padding: '10px 14px',
    borderRadius: '8px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    border: '1px solid rgba(255,255,255,0.06)',
    cursor: 'pointer',
    transition: 'background-color 0.15s',
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.06)',
    },
  },
  icon: {
    fontSize: '16px',
    flexShrink: 0,
    lineHeight: '1.4',
  },
  content: {
    flex: 1,
    minWidth: 0,
  },
  summary: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '13px',
    color: '#e2e8f0',
  },
  riskType: {
    fontWeight: '600',
    color: '#f1f5f9',
  },
  period: {
    fontSize: '11px',
    color: '#64748b',
    marginLeft: 'auto',
    flexShrink: 0,
  },
  detail: {
    fontSize: '12px',
    color: '#94a3b8',
    marginTop: '6px',
    lineHeight: '1.5',
  },
  empty: {
    fontSize: '13px',
    color: '#64748b',
    fontStyle: 'italic',
    padding: '8px 0',
  },
});

type Risk = ForecastData['risks'][number];

function RiskCard({ risk }: { risk: Risk }) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);

  return (
    <div
      className={styles.card}
      onClick={() => setExpanded(!expanded)}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') setExpanded(!expanded); }}
      aria-expanded={expanded}
      data-testid={`risk-card-${risk.severity}`}
    >
      <span className={styles.icon}>{SEVERITY_ICONS[risk.severity] ?? '⚪'}</span>
      <div className={styles.content}>
        <div className={styles.summary}>
          <span className={styles.riskType}>{risk.type}</span>
          <span className={styles.period}>{risk.affectedPeriod}</span>
        </div>
        {expanded && <div className={styles.detail}>{risk.description}</div>}
      </div>
    </div>
  );
}

export default function DemandRiskCards({ risks }: { risks: ForecastData['risks'] }) {
  const styles = useStyles();

  const sorted = [...risks].sort(
    (a, b) => (SEVERITY_ORDER[a.severity] ?? 3) - (SEVERITY_ORDER[b.severity] ?? 3),
  );

  return (
    <div className={styles.container} data-testid="demand-risk-cards">
      <div className={styles.heading}>Identified Risks</div>
      {sorted.length === 0 && <div className={styles.empty}>No risks identified</div>}
      {sorted.map((risk, i) => (
        <RiskCard key={`${risk.type}-${i}`} risk={risk} />
      ))}
    </div>
  );
}
