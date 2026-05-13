import {
  ResponsiveContainer,
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
} from 'recharts';
import { makeStyles, Button, Badge } from '@fluentui/react-components';
import { Dismiss16Regular } from '@fluentui/react-icons';
import { COMPETITIVE_COLORS } from '../../constants/agentRouting';
import type { CompetitorOverview } from '../../types';

interface CompetitorProfileProps {
  competitor: CompetitorOverview;
  onClose: () => void;
}

const AXIS_TICK = { fill: '#94a3b8', fontSize: 11 } as const;

const tooltipContentStyle = {
  backgroundColor: '#1e1b2e',
  border: '1px solid rgba(107,114,128,0.3)',
  borderRadius: 8,
  color: '#f1f5f9',
  fontSize: 12,
} as const;

const useStyles = makeStyles({
  overlay: {
    position: 'fixed',
    inset: '0',
    backgroundColor: 'rgba(0,0,0,0.6)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    zIndex: 100,
  },
  panel: {
    width: 'min(680px, 90vw)',
    maxHeight: '85vh',
    overflow: 'auto',
    backgroundColor: 'var(--color-bg-elevated)',
    border: '1px solid var(--color-border)',
    borderRadius: '12px',
    padding: '24px',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: '20px',
  },
  name: {
    fontSize: '20px',
    fontWeight: '700',
    color: 'var(--color-text)',
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  section: {
    marginBottom: '20px',
  },
  sectionTitle: {
    fontSize: '13px',
    fontWeight: '600',
    color: '#94a3b8',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    marginBottom: '10px',
  },
  tags: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
  },
  tag: {
    fontSize: '11px',
    padding: '3px 10px',
    borderRadius: '4px',
    backgroundColor: 'rgba(255,255,255,0.06)',
    color: 'var(--color-text-muted)',
  },
  timeline: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  move: {
    display: 'flex',
    gap: '10px',
    fontSize: '13px',
    padding: '8px 12px',
    borderRadius: '6px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    border: '1px solid rgba(255,255,255,0.04)',
  },
  moveDate: {
    color: '#94a3b8',
    fontSize: '11px',
    flexShrink: 0,
    width: '80px',
  },
  moveAction: {
    color: 'var(--color-text)',
    flex: 1,
  },
  statRow: {
    display: 'flex',
    gap: '16px',
    marginBottom: '16px',
  },
  stat: {
    flex: 1,
    padding: '12px',
    borderRadius: '8px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    border: '1px solid rgba(255,255,255,0.06)',
    textAlign: 'center',
  },
  statValue: {
    fontSize: '20px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  statLabel: {
    fontSize: '11px',
    color: '#94a3b8',
    marginTop: '4px',
  },
});

export default function CompetitorProfile({ competitor, onClose }: CompetitorProfileProps) {
  const styles = useStyles();

  return (
    <div className={styles.overlay} onClick={onClose} data-testid="competitor-profile">
      <div className={styles.panel} onClick={e => e.stopPropagation()}>
        <div className={styles.header}>
          <div className={styles.name}>
            ⚔️ {competitor.name}
            <Badge appearance="filled" style={{ background: 'rgba(107,114,128,0.2)', color: '#d1d5db' }}>
              {competitor.marketShare.toFixed(1)}% share
            </Badge>
          </div>
          <Button
            appearance="subtle"
            icon={<Dismiss16Regular />}
            onClick={onClose}
            aria-label="Close profile"
          />
        </div>

        <div className={styles.statRow}>
          <div className={styles.stat}>
            <div className={styles.statValue}>{competitor.categories.length}</div>
            <div className={styles.statLabel}>Categories</div>
          </div>
          <div className={styles.stat}>
            <div className={styles.statValue}>{competitor.regions.length}</div>
            <div className={styles.statLabel}>Regions</div>
          </div>
          <div className={styles.stat}>
            <div className={styles.statValue}>{competitor.recentMoves.length}</div>
            <div className={styles.statLabel}>Recent Moves</div>
          </div>
        </div>

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Categories</div>
          <div className={styles.tags}>
            {competitor.categories.map(c => (
              <span key={c} className={styles.tag}>{c}</span>
            ))}
          </div>
        </div>

        <div className={styles.section}>
          <div className={styles.sectionTitle}>Regions</div>
          <div className={styles.tags}>
            {competitor.regions.map(r => (
              <span key={r} className={styles.tag}>{r}</span>
            ))}
          </div>
        </div>

        {competitor.pricingHistory.length > 0 && (
          <div className={styles.section}>
            <div className={styles.sectionTitle}>Pricing History</div>
            <ResponsiveContainer width="100%" height={200}>
              <LineChart data={competitor.pricingHistory} margin={{ top: 10, right: 20, bottom: 10, left: 10 }}>
                <CartesianGrid strokeDasharray="3 3" stroke={COMPETITIVE_COLORS.gridLine} />
                <XAxis dataKey="month" tick={AXIS_TICK} />
                <YAxis tick={AXIS_TICK} />
                <Tooltip contentStyle={tooltipContentStyle} />
                <Line
                  type="monotone"
                  dataKey="avgPrice"
                  stroke={COMPETITIVE_COLORS.competitor}
                  strokeWidth={2}
                  dot={{ fill: COMPETITIVE_COLORS.competitor, r: 3 }}
                />
              </LineChart>
            </ResponsiveContainer>
          </div>
        )}

        {competitor.recentMoves.length > 0 && (
          <div className={styles.section}>
            <div className={styles.sectionTitle}>Activity Timeline</div>
            <div className={styles.timeline}>
              {competitor.recentMoves.map((move, idx) => (
                <div key={idx} className={styles.move}>
                  <span className={styles.moveDate}>{new Date(move.date).toLocaleDateString()}</span>
                  <span className={styles.moveAction}>{move.action}</span>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
