import { makeStyles, Badge } from '@fluentui/react-components';
import type { PromoEvaluation, PromoRecommendationLevel, PromoRisk } from '../../types';
import { PROMO_COLORS } from '../../constants/agentRouting';
import { useState } from 'react';

interface PromoRecommendationProps {
  evaluation: PromoEvaluation;
  budget: number;
  onSubmitForApproval?: () => void;
}

const RECOMMENDATION_CONFIG: Record<PromoRecommendationLevel, { emoji: string; label: string; color: string; bgColor: string; badge: 'success' | 'warning' | 'danger' }> = {
  recommended: { emoji: '✅', label: 'Recommended', color: '#22c55e', bgColor: 'rgba(34,197,94,0.08)', badge: 'success' },
  cautious: { emoji: '⚠️', label: 'Cautious', color: '#eab308', bgColor: 'rgba(234,179,8,0.08)', badge: 'warning' },
  not_recommended: { emoji: '❌', label: 'Not Recommended', color: '#ef4444', bgColor: 'rgba(239,68,68,0.08)', badge: 'danger' },
};

const SEVERITY_ICONS: Record<string, string> = { high: '🔴', medium: '🟡', low: '🟢' };
const SEVERITY_ORDER: Record<string, number> = { high: 0, medium: 1, low: 2 };

const HIGH_SPEND_THRESHOLD = 50_000;

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '20px',
    borderRadius: '12px',
    animation: 'messageIn 0.3s ease',
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  roiDisplay: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  roiValue: {
    fontSize: '28px',
    fontWeight: '700',
    letterSpacing: '-0.5px',
  },
  roiRange: {
    fontSize: '12px',
    color: '#94a3b8',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  sectionLabel: {
    fontSize: '11px',
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    color: '#64748b',
  },
  sectionText: {
    fontSize: '13px',
    lineHeight: '1.5',
    color: '#e2e8f0',
  },
  riskCard: {
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
  riskIcon: {
    fontSize: '16px',
    flexShrink: 0,
    lineHeight: '1.4',
  },
  riskContent: {
    flex: 1,
    minWidth: 0,
  },
  riskType: {
    fontSize: '13px',
    fontWeight: '600',
    color: '#f1f5f9',
  },
  riskDetail: {
    fontSize: '12px',
    color: '#94a3b8',
    marginTop: '4px',
    lineHeight: '1.5',
  },
  credibility: {
    fontSize: '12px',
    color: '#64748b',
    fontStyle: 'italic',
    textAlign: 'center',
    paddingTop: '8px',
    borderTop: '1px solid rgba(255,255,255,0.06)',
  },
  approvalButton: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '10px 20px',
    borderRadius: '8px',
    border: 'none',
    fontSize: '13px',
    fontWeight: '600',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    color: '#fff',
    backgroundColor: '#3b82f6',
    ':hover': {
      backgroundColor: '#2563eb',
    },
  },
  timingRow: {
    display: 'flex',
    gap: '12px',
    flexWrap: 'wrap',
  },
  timingChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '4px 10px',
    borderRadius: '6px',
    fontSize: '12px',
    backgroundColor: 'rgba(255,255,255,0.04)',
    border: '1px solid rgba(255,255,255,0.08)',
    color: '#cbd5e1',
  },
  conflictChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '4px 10px',
    borderRadius: '6px',
    fontSize: '12px',
    backgroundColor: 'rgba(239,68,68,0.08)',
    border: '1px solid rgba(239,68,68,0.2)',
    color: '#fca5a5',
  },
});

function RiskCard({ risk }: { risk: PromoRisk }) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  return (
    <div
      className={styles.riskCard}
      onClick={() => setExpanded(!expanded)}
      role="button"
      tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') setExpanded(!expanded); }}
      aria-expanded={expanded}
      data-testid={`promo-risk-${risk.severity}`}
    >
      <span className={styles.riskIcon}>{SEVERITY_ICONS[risk.severity] ?? '⚪'}</span>
      <div className={styles.riskContent}>
        <span className={styles.riskType}>{risk.type}</span>
        {expanded && <div className={styles.riskDetail}>{risk.detail}</div>}
      </div>
    </div>
  );
}

export default function PromoRecommendation({ evaluation, budget, onSubmitForApproval }: PromoRecommendationProps) {
  const styles = useStyles();
  const config = RECOMMENDATION_CONFIG[evaluation.recommendation];
  const isHighSpend = budget >= HIGH_SPEND_THRESHOLD;
  const sortedRisks = [...evaluation.risks].sort(
    (a, b) => (SEVERITY_ORDER[a.severity] ?? 3) - (SEVERITY_ORDER[b.severity] ?? 3),
  );
  const roiColor = evaluation.roi >= 1 ? PROMO_COLORS.recommended : PROMO_COLORS.notRecommended;

  return (
    <div
      className={styles.container}
      style={{ backgroundColor: config.bgColor, border: `1px solid ${config.color}30` }}
      data-testid="promo-recommendation"
    >
      <div className={styles.headerRow}>
        <Badge appearance="filled" color={config.badge} data-testid="recommendation-badge">
          {config.emoji} {config.label}
        </Badge>
        <div className={styles.roiDisplay}>
          <span className={styles.roiValue} style={{ color: roiColor }} data-testid="roi-value">
            {evaluation.roi.toFixed(1)}x ROI
          </span>
          <span className={styles.roiRange} data-testid="roi-range">
            ({evaluation.roiLower.toFixed(1)}x — {evaluation.roiUpper.toFixed(1)}x)
          </span>
        </div>
      </div>

      <div className={styles.section}>
        <span className={styles.sectionLabel}>Analysis</span>
        <span className={styles.sectionText}>{evaluation.reasoning}</span>
      </div>

      <div className={styles.section}>
        <span className={styles.sectionLabel}>Timing Assessment</span>
        <div className={styles.timingRow}>
          <span className={styles.timingChip}>🗓️ {evaluation.seasonalityFit}</span>
          <span className={styles.timingChip}>⏱️ Break-even: {evaluation.breakEvenDays} days</span>
          <span className={styles.timingChip}>📊 Hist. Avg: {evaluation.historicalAvgRoi.toFixed(1)}x</span>
        </div>
        {evaluation.conflicts.length > 0 && (
          <div className={styles.timingRow} style={{ marginTop: '6px' }}>
            {evaluation.conflicts.map((c, i) => (
              <span key={i} className={styles.conflictChip}>⚠️ {c}</span>
            ))}
          </div>
        )}
        <span className={styles.sectionText}>{evaluation.timingAssessment}</span>
      </div>

      {sortedRisks.length > 0 && (
        <div className={styles.section}>
          <span className={styles.sectionLabel}>Risks</span>
          {sortedRisks.map((risk, i) => (
            <RiskCard key={`${risk.type}-${i}`} risk={risk} />
          ))}
        </div>
      )}

      {isHighSpend && onSubmitForApproval && (
        <button
          className={styles.approvalButton}
          onClick={onSubmitForApproval}
          data-testid="submit-approval-button"
        >
          🔒 Submit for Approval (${budget.toLocaleString()} budget)
        </button>
      )}

      <div className={styles.credibility} data-testid="credibility-note">
        Based on {evaluation.similarCampaigns} similar campaigns
      </div>
    </div>
  );
}
