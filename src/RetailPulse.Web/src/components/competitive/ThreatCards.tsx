import { useState, useCallback } from 'react';
import { makeStyles, Badge, Button } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronUp16Regular } from '@fluentui/react-icons';
import { COMPETITIVE_COLORS } from '../../constants/agentRouting';
import { generateResponsePlan } from '../../services/competitiveApi';
import type { CompetitiveThreat, ThreatSeverity, ThreatRecommendation } from '../../types';

interface ThreatCardsProps {
  threats: CompetitiveThreat[];
  compact?: boolean;
  onViewCompetitor?: (name: string) => void;
}

const SEVERITY_EMOJIS: Record<ThreatSeverity, string> = { high: '🔴', medium: '🟡', low: '🟢' };
const SEVERITY_LABELS: Record<ThreatSeverity, string> = { high: 'High', medium: 'Medium', low: 'Low' };
const SEVERITY_COLORS: Record<ThreatSeverity, string> = {
  high: COMPETITIVE_COLORS.threatHigh,
  medium: COMPETITIVE_COLORS.threatMedium,
  low: COMPETITIVE_COLORS.threatLow,
};
const SEVERITY_ORDER: Record<ThreatSeverity, number> = { high: 0, medium: 1, low: 2 };

const REC_COLORS: Record<ThreatRecommendation, string> = {
  MATCH: COMPETITIVE_COLORS.match,
  DIFFERENTIATE: COMPETITIVE_COLORS.differentiate,
  IGNORE: COMPETITIVE_COLORS.ignore,
  PREEMPT: COMPETITIVE_COLORS.preempt,
};

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '8px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: '#ef4444',
  },
  card: {
    background: 'var(--color-surface, rgba(255,255,255,0.03))',
    border: '1px solid rgba(255,255,255,0.06)',
    borderLeftWidth: '4px',
    borderRadius: '8px',
    padding: '14px 16px',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'var(--color-surface-hover, rgba(255,255,255,0.06))',
    },
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginBottom: '8px',
    flexWrap: 'wrap',
  },
  threatTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
    flex: 1,
  },
  badges: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
  },
  severityBadge: {
    fontSize: '11px',
    fontWeight: '700',
    padding: '2px 8px',
    borderRadius: '4px',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  recBadge: {
    fontSize: '10px',
    fontWeight: '700',
    padding: '2px 8px',
    borderRadius: '4px',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  description: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
    marginBottom: '8px',
  },
  meta: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    marginBottom: '8px',
  },
  metaTag: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    background: 'rgba(255,255,255,0.06)',
    padding: '2px 8px',
    borderRadius: '4px',
  },
  details: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
    padding: '12px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '6px',
    marginBottom: '8px',
    borderTop: '1px solid var(--color-border)',
  },
  detailSection: {
    marginBottom: '10px',
  },
  detailLabel: {
    fontWeight: '600',
    color: 'var(--color-text)',
    marginBottom: '4px',
    display: 'block',
    fontSize: '12px',
  },
  actions: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
  },
});

function ThreatCard({
  threat,
  onViewCompetitor,
}: {
  threat: CompetitiveThreat;
  onViewCompetitor?: (name: string) => void;
}) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const [generatingPlan, setGeneratingPlan] = useState(false);
  const [responsePlan, setResponsePlan] = useState<string | null>(null);

  const handleGeneratePlan = useCallback(async () => {
    setGeneratingPlan(true);
    try {
      const result = await generateResponsePlan(threat.id);
      setResponsePlan(result?.plan ?? 'No response plan available.');
    } catch {
      setResponsePlan('Failed to generate response plan. Please try again.');
    } finally {
      setGeneratingPlan(false);
    }
  }, [threat.id]);

  return (
    <div
      className={styles.card}
      style={{ borderLeftColor: SEVERITY_COLORS[threat.severity] }}
      data-testid="threat-card"
      role="article"
      aria-label={`${threat.severity} threat: ${threat.title}`}
    >
      <div className={styles.header}>
        <span>{SEVERITY_EMOJIS[threat.severity]}</span>
        <div className={styles.badges}>
          <Badge
            data-testid="severity-badge"
            appearance="filled"
            className={styles.severityBadge}
            style={{ background: SEVERITY_COLORS[threat.severity], color: '#fff' }}
          >
            {SEVERITY_LABELS[threat.severity]}
          </Badge>
          <Badge
            data-testid="recommendation-badge"
            appearance="filled"
            className={styles.recBadge}
            style={{ background: `${REC_COLORS[threat.recommendation]}30`, color: REC_COLORS[threat.recommendation] }}
          >
            {threat.recommendation}
          </Badge>
        </div>
        <span className={styles.threatTitle}>{threat.title}</span>
      </div>

      <div className={styles.description}>{threat.description}</div>

      <div className={styles.meta}>
        <span className={styles.metaTag}>⚔️ {threat.competitor}</span>
        <span className={styles.metaTag}>🏷️ {threat.category}</span>
        <span className={styles.metaTag}>📅 {new Date(threat.detectedAt).toLocaleDateString()}</span>
      </div>

      {expanded && (
        <div className={styles.details} data-testid="threat-details">
          <div className={styles.detailSection}>
            <span className={styles.detailLabel}>💡 Reasoning</span>
            <span>{threat.reasoning}</span>
          </div>
          <div className={styles.detailSection}>
            <span className={styles.detailLabel}>📜 Historical Context</span>
            <span>{threat.historicalContext}</span>
          </div>
          {responsePlan && (
            <div className={styles.detailSection}>
              <span className={styles.detailLabel}>📋 Response Plan</span>
              <span>{responsePlan}</span>
            </div>
          )}
        </div>
      )}

      <div className={styles.actions}>
        <Button
          appearance="subtle"
          size="small"
          icon={expanded ? <ChevronUp16Regular /> : <ChevronDown16Regular />}
          onClick={() => setExpanded(prev => !prev)}
        >
          {expanded ? 'Hide Details' : 'View Details'}
        </Button>
        <Button
          appearance="subtle"
          size="small"
          onClick={handleGeneratePlan}
          disabled={generatingPlan}
        >
          {generatingPlan ? '⏳ Generating...' : '📋 Generate Response Plan'}
        </Button>
        {onViewCompetitor && (
          <Button
            appearance="subtle"
            size="small"
            onClick={() => onViewCompetitor(threat.competitor)}
          >
            👤 View Competitor
          </Button>
        )}
      </div>
    </div>
  );
}

export default function ThreatCards({ threats, compact, onViewCompetitor }: ThreatCardsProps) {
  const styles = useStyles();

  const sortedThreats = [...threats].sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]);

  if (threats.length === 0) {
    return (
      <div>
        <div className={styles.titleRow}>
          <span className={styles.title}>🚨 Competitive Threats</span>
        </div>
        <div className={styles.empty} data-testid="threats-empty">No active threats detected</div>
      </div>
    );
  }

  return (
    <div data-testid="threat-cards">
      {!compact && (
        <div className={styles.titleRow}>
          <span className={styles.title}>🚨 Competitive Threats</span>
          <Badge appearance="filled" style={{ background: 'rgba(239,68,68,0.15)', color: '#fca5a5' }}>
            {threats.length} active
          </Badge>
        </div>
      )}
      <div className={styles.container}>
        {sortedThreats.map(threat => (
          <ThreatCard
            key={threat.id}
            threat={threat}
            onViewCompetitor={onViewCompetitor}
          />
        ))}
      </div>
    </div>
  );
}
