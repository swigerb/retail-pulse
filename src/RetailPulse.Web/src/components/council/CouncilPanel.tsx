import { useState, useCallback } from 'react';
import { makeStyles, Button } from '@fluentui/react-components';
import { COUNCIL_COLORS } from '../../constants/agentRouting';
import type { CouncilAgentVote, CouncilVerdict as CouncilVerdictType } from '../../types';
import { conveneCouncil } from '../../services/councilApi';
import CouncilVoting from './CouncilVoting';
import CouncilVerdictView from './CouncilVerdict';
import CouncilHistory from './CouncilHistory';

const BRANDS = ['Apex Grill', 'SmokeHouse Pro', 'BlazeMaster', 'FlameKing', 'CharPro'];
const REGIONS = ['All Regions', 'Northeast', 'Southeast', 'Midwest', 'Southwest', 'West'];

type Phase = 'idle' | 'loading' | 'voting' | 'verdict';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'auto',
    padding: '24px',
    backgroundColor: 'var(--color-bg)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    marginBottom: '24px',
    flexWrap: 'wrap',
  },
  titleArea: {
    flex: 1,
  },
  title: {
    fontSize: '22px',
    fontWeight: '700',
    color: COUNCIL_COLORS.green,
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    letterSpacing: '-0.5px',
  },
  subtitle: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    fontWeight: '500',
    marginTop: '4px',
  },
  controls: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  filterSelect: {
    padding: '8px 14px',
    borderRadius: '8px',
    border: '1px solid var(--color-border)',
    backgroundColor: 'var(--color-surface)',
    color: 'var(--color-text)',
    fontSize: '14px',
    cursor: 'pointer',
    outline: 'none',
    fontWeight: '500',
  },
  conveneBtn: {
    fontWeight: '700',
    fontSize: '14px',
    padding: '8px 24px',
  },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
  },
  error: {
    padding: '16px',
    borderRadius: '8px',
    backgroundColor: 'rgba(211,47,47,0.1)',
    border: '1px solid rgba(211,47,47,0.3)',
    color: '#fca5a5',
    fontSize: '13px',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '80px 40px',
    textAlign: 'center',
    gap: '16px',
  },
  emptyIcon: {
    fontSize: '48px',
  },
  emptyTitle: {
    fontSize: '18px',
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  emptyDescription: {
    fontSize: '14px',
    color: 'var(--color-text-muted)',
    maxWidth: '400px',
    lineHeight: '1.6',
  },
});

export default function CouncilPanel() {
  const styles = useStyles();
  const [brand, setBrand] = useState(BRANDS[0]);
  const [region, setRegion] = useState('All Regions');
  const [phase, setPhase] = useState<Phase>('idle');
  const [votes, setVotes] = useState<CouncilAgentVote[]>([]);
  const [verdict, setVerdict] = useState<CouncilVerdictType | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handleConvene = useCallback(async () => {
    setPhase('loading');
    setVotes([]);
    setVerdict(null);
    setError(null);

    try {
      const regionParam = region === 'All Regions' ? undefined : region;
      const response = await conveneCouncil(brand, regionParam);

      // Simulate staggered vote arrival for drama
      setPhase('voting');
      for (let i = 0; i < response.votes.length; i++) {
        await new Promise(resolve => setTimeout(resolve, 400));
        setVotes(prev => [...prev, response.votes[i]]);
      }

      // Brief pause before verdict reveal
      await new Promise(resolve => setTimeout(resolve, 600));
      setVerdict(response.verdict);
      setPhase('verdict');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Council convene failed');
      setPhase('idle');
    }
  }, [brand, region]);

  return (
    <div className={styles.container} data-testid="council-panel">
      <div className={styles.header}>
        <div className={styles.titleArea}>
          <div className={styles.title}>🏛️ Portfolio Health Council</div>
          <div className={styles.subtitle}>Multi-Agent Brand Assessment</div>
        </div>
        <div className={styles.controls}>
          <select
            data-testid="brand-selector"
            className={styles.filterSelect}
            value={brand}
            onChange={e => setBrand(e.target.value)}
            disabled={phase === 'loading' || phase === 'voting'}
          >
            {BRANDS.map(b => <option key={b} value={b}>{b}</option>)}
          </select>
          <select
            data-testid="region-selector"
            className={styles.filterSelect}
            value={region}
            onChange={e => setRegion(e.target.value)}
            disabled={phase === 'loading' || phase === 'voting'}
          >
            {REGIONS.map(r => <option key={r} value={r}>{r}</option>)}
          </select>
          <Button
            data-testid="convene-button"
            appearance="primary"
            className={styles.conveneBtn}
            onClick={handleConvene}
            disabled={phase === 'loading' || phase === 'voting'}
            style={{
              backgroundColor: COUNCIL_COLORS.green,
              borderColor: COUNCIL_COLORS.green,
            }}
          >
            {phase === 'loading' || phase === 'voting' ? '⏳ Convening...' : '🏛️ Convene Council'}
          </Button>
        </div>
      </div>

      {error && (
        <div className={styles.error} data-testid="council-error">
          ⚠️ {error}
        </div>
      )}

      <div className={styles.content}>
        {phase === 'idle' && votes.length === 0 && !error && (
          <div className={styles.emptyState}>
            <div className={styles.emptyIcon}>🏛️</div>
            <div className={styles.emptyTitle}>Portfolio Health Council</div>
            <div className={styles.emptyDescription}>
              Select a brand and click "Convene Council" to assemble 3 specialist agents —
              Demand, Supply, and Competitive — for a comprehensive health assessment.
              Each agent deliberates independently, then votes are synthesized into an executive verdict.
            </div>
          </div>
        )}

        {(phase === 'loading' || phase === 'voting' || votes.length > 0) && (
          <CouncilVoting
            votes={votes}
            loading={phase === 'loading' || phase === 'voting'}
          />
        )}

        {verdict && <CouncilVerdictView verdict={verdict} />}

        <CouncilHistory />
      </div>
    </div>
  );
}
