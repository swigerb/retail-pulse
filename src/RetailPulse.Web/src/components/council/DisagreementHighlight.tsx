import { makeStyles } from '@fluentui/react-components';
import { COUNCIL_COLORS, COUNCIL_DOMAIN_CONFIG } from '../../constants/agentRouting';
import type { CouncilDisagreement } from '../../types';

interface DisagreementHighlightProps {
  disagreements: CouncilDisagreement[];
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  card: {
    background: COUNCIL_COLORS.disagreementBg,
    border: `1px solid ${COUNCIL_COLORS.disagreementBorder}`,
    borderRadius: '10px',
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  topic: {
    fontSize: '14px',
    fontWeight: '700',
    color: COUNCIL_COLORS.yellow,
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  positionsRow: {
    display: 'flex',
    gap: '12px',
    flexWrap: 'wrap',
  },
  positionCard: {
    flex: 1,
    minWidth: '180px',
    padding: '12px',
    borderRadius: '8px',
    background: 'rgba(255,255,255,0.04)',
    border: '1px solid rgba(255,255,255,0.06)',
  },
  positionAgent: {
    fontSize: '12px',
    fontWeight: '700',
    marginBottom: '6px',
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  positionText: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
  },
  connector: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '18px',
    color: COUNCIL_COLORS.yellow,
    flexShrink: 0,
    alignSelf: 'center',
  },
  resolution: {
    fontSize: '13px',
    lineHeight: '1.5',
    padding: '10px 12px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '6px',
    borderLeft: `3px solid ${COUNCIL_COLORS.yellow}`,
  },
  resolutionLabel: {
    fontWeight: '700',
    color: 'var(--color-text)',
    marginBottom: '4px',
    display: 'block',
    fontSize: '12px',
  },
  resolutionText: {
    color: 'var(--color-text-muted)',
  },
  dominant: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '6px 10px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '6px',
  },
  dominantLabel: {
    fontWeight: '700',
    color: 'var(--color-text)',
  },
});

function getAgentEmoji(agentName: string): string {
  const lower = agentName.toLowerCase();
  for (const [domain, config] of Object.entries(COUNCIL_DOMAIN_CONFIG)) {
    if (lower.includes(domain)) return config.emoji;
  }
  return '🤖';
}

export default function DisagreementHighlight({ disagreements }: DisagreementHighlightProps) {
  const styles = useStyles();

  if (disagreements.length === 0) return null;

  return (
    <div className={styles.container} data-testid="disagreement-highlight">
      {disagreements.map((d, i) => (
        <div key={i} className={styles.card} data-testid="disagreement-card">
          <div className={styles.topic}>
            <span>⚡</span>
            <span>{d.topic}</span>
          </div>

          <div className={styles.positionsRow}>
            {d.agents.map((agent, j) => (
              <div key={j}>
                {j > 0 && <div className={styles.connector}>⇄</div>}
                <div className={styles.positionCard}>
                  <div className={styles.positionAgent}>
                    <span>{getAgentEmoji(agent.agentName)}</span>
                    <span style={{ color: 'var(--color-text)' }}>{agent.agentName}</span>
                  </div>
                  <div className={styles.positionText}>{agent.position}</div>
                </div>
              </div>
            ))}
          </div>

          <div className={styles.resolution}>
            <span className={styles.resolutionLabel}>🔑 Resolution</span>
            <span className={styles.resolutionText}>{d.resolution}</span>
          </div>

          <div className={styles.dominant}>
            <span className={styles.dominantLabel}>Weight:</span>
            <span>{d.dominantAgent} — {d.dominantReason}</span>
          </div>
        </div>
      ))}
    </div>
  );
}
