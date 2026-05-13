import { makeStyles } from '@fluentui/react-components';
import type { ExplanationData } from '../../types';
import { SCORECARD_COLORS } from '../../constants/agentRouting';

interface ExplanationPanelProps {
  explanation: ExplanationData | null;
  open: boolean;
  onClose: () => void;
}

const useStyles = makeStyles({
  backdrop: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(0,0,0,0.55)',
    zIndex: 9998,
    transitionProperty: 'opacity',
    transitionDuration: '0.3s',
    transitionTimingFunction: 'ease',
  },
  backdropHidden: {
    opacity: 0,
    pointerEvents: 'none',
  },
  backdropVisible: {
    opacity: 1,
  },
  panel: {
    position: 'fixed',
    top: 0,
    right: 0,
    width: '480px',
    maxWidth: '100vw',
    height: '100vh',
    backgroundColor: SCORECARD_COLORS.explainPanel,
    borderLeft: `1px solid ${SCORECARD_COLORS.explainBorder}`,
    zIndex: 9999,
    display: 'flex',
    flexDirection: 'column',
    transitionProperty: 'transform',
    transitionDuration: '0.35s',
    transitionTimingFunction: 'cubic-bezier(0.4, 0, 0.2, 1)',
    boxShadow: '-8px 0 32px rgba(0,0,0,0.5)',
  },
  panelOpen: {
    transform: 'translateX(0)',
  },
  panelClosed: {
    transform: 'translateX(100%)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '20px 24px 16px',
    borderBottom: `1px solid ${SCORECARD_COLORS.explainBorder}`,
  },
  headerTitle: {
    fontSize: '16px',
    fontWeight: '700',
    color: '#f1f5f9',
    letterSpacing: '-0.3px',
  },
  closeBtn: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '32px',
    height: '32px',
    borderRadius: '8px',
    border: 'none',
    backgroundColor: 'rgba(255,255,255,0.06)',
    color: '#94a3b8',
    fontSize: '18px',
    cursor: 'pointer',
    transitionProperty: 'background-color, color',
    transitionDuration: '0.15s',
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.12)',
      color: '#f1f5f9',
    },
  },
  body: {
    flex: 1,
    overflowY: 'auto',
    padding: '24px',
  },
  question: {
    fontStyle: 'italic',
    color: '#94a3b8',
    borderLeft: `3px solid ${SCORECARD_COLORS.ring}`,
    paddingLeft: '14px',
    marginBottom: '16px',
    fontSize: '14px',
    lineHeight: '1.5',
  },
  answer: {
    color: '#e2e8f0',
    fontSize: '14px',
    lineHeight: '1.6',
    marginBottom: '28px',
    paddingBottom: '20px',
    borderBottom: `1px solid ${SCORECARD_COLORS.explainBorder}`,
  },
  stepsTitle: {
    fontSize: '12px',
    fontWeight: '600',
    color: '#64748b',
    textTransform: 'uppercase',
    letterSpacing: '1.2px',
    marginBottom: '16px',
  },
  step: {
    marginBottom: '20px',
    paddingLeft: '16px',
    borderLeft: `2px solid ${SCORECARD_COLORS.stepBadge}`,
  },
  stepBadge: {
    display: 'inline-block',
    fontSize: '11px',
    fontWeight: '600',
    padding: '2px 10px',
    borderRadius: '6px',
    backgroundColor: SCORECARD_COLORS.stepBadge,
    color: SCORECARD_COLORS.stepBadgeText,
    marginBottom: '8px',
  },
  stepInput: {
    fontSize: '12px',
    color: '#64748b',
    marginBottom: '4px',
  },
  stepOutput: {
    fontSize: '12px',
    color: '#94a3b8',
    marginBottom: '6px',
  },
  stepReasoning: {
    fontSize: '13px',
    color: '#cbd5e1',
    lineHeight: '1.5',
  },
  footer: {
    padding: '16px 24px 20px',
    borderTop: `1px solid ${SCORECARD_COLORS.explainBorder}`,
  },
  confidenceRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '14px',
  },
  confidenceLabel: {
    fontSize: '12px',
    fontWeight: '600',
    color: '#94a3b8',
    minWidth: '80px',
  },
  confidenceTrack: {
    flex: 1,
    height: '6px',
    borderRadius: '3px',
    backgroundColor: 'rgba(255,255,255,0.08)',
    overflow: 'hidden',
  },
  confidenceValue: {
    fontSize: '13px',
    fontWeight: '700',
    minWidth: '36px',
    textAlign: 'right',
  },
  sourcesTitle: {
    fontSize: '12px',
    fontWeight: '600',
    color: '#64748b',
    textTransform: 'uppercase',
    letterSpacing: '1.2px',
    marginBottom: '8px',
  },
  sourceLink: {
    fontSize: '13px',
    color: SCORECARD_COLORS.ring,
    textDecoration: 'none',
    ':hover': {
      textDecoration: 'underline',
    },
  },
  sourceItem: {
    fontSize: '13px',
    color: '#94a3b8',
    marginBottom: '4px',
  },
  // Skeleton styles
  skeletonBlock: {
    borderRadius: '6px',
    backgroundColor: SCORECARD_COLORS.skeletonBg,
    marginBottom: '12px',
  },
});

function getConfidenceColor(confidence: number) {
  if (confidence >= 80) return SCORECARD_COLORS.confidenceHigh;
  if (confidence >= 50) return SCORECARD_COLORS.confidenceMedium;
  return SCORECARD_COLORS.confidenceLow;
}

function LoadingSkeleton() {
  const styles = useStyles();
  const shimmerKeyframes = `
    @keyframes explainShimmer {
      0% { background-position: -200% 0; }
      100% { background-position: 200% 0; }
    }
  `;
  const shimmerStyle: React.CSSProperties = {
    backgroundImage: `linear-gradient(90deg, ${SCORECARD_COLORS.skeletonBg} 25%, ${SCORECARD_COLORS.skeletonShimmer} 50%, ${SCORECARD_COLORS.skeletonBg} 75%)`,
    backgroundSize: '200% 100%',
    animation: 'explainShimmer 1.8s ease-in-out infinite',
  };

  return (
    <>
      <style>{shimmerKeyframes}</style>
      <div className={styles.skeletonBlock} style={{ ...shimmerStyle, height: '40px' }} />
      <div className={styles.skeletonBlock} style={{ ...shimmerStyle, height: '60px' }} />
      {[1, 2, 3].map((i) => (
        <div key={i} className={styles.skeletonBlock} style={{ ...shimmerStyle, height: '80px' }} />
      ))}
    </>
  );
}

export function ExplanationPanel({ explanation, open, onClose }: ExplanationPanelProps) {
  const styles = useStyles();

  const stepRevealKeyframes = `
    @keyframes stepReveal {
      from { opacity: 0; transform: translateY(12px); }
      to   { opacity: 1; transform: translateY(0); }
    }
  `;

  return (
    <>
      <style>{stepRevealKeyframes}</style>

      {/* Backdrop */}
      <div
        className={`${styles.backdrop} ${open ? styles.backdropVisible : styles.backdropHidden}`}
        onClick={onClose}
      />

      {/* Panel */}
      <div className={`${styles.panel} ${open ? styles.panelOpen : styles.panelClosed}`}>
        <div className={styles.header}>
          <span className={styles.headerTitle}>How did we get this answer?</span>
          <button className={styles.closeBtn} onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        <div className={styles.body}>
          {!explanation ? (
            <LoadingSkeleton />
          ) : (
            <>
              <div className={styles.question}>{explanation.question}</div>
              <div className={styles.answer}>{explanation.answer}</div>
              <div className={styles.stepsTitle}>Reasoning Steps</div>
              {explanation.steps.map((step, i) => (
                <div
                  key={i}
                  className={styles.step}
                  style={{
                    opacity: 0,
                    animation: `stepReveal 0.4s ease forwards`,
                    animationDelay: `${i * 0.15}s`,
                  }}
                >
                  <span className={styles.stepBadge}>{step.toolName}</span>
                  <div className={styles.stepInput}>↳ {step.inputSummary}</div>
                  <div className={styles.stepOutput}>→ {step.outputSummary}</div>
                  <div className={styles.stepReasoning}>{step.reasoning}</div>
                </div>
              ))}
            </>
          )}
        </div>

        {explanation && (
          <div className={styles.footer}>
            <div className={styles.confidenceRow}>
              <span className={styles.confidenceLabel}>Confidence</span>
              <div className={styles.confidenceTrack}>
                <div
                  style={{
                    width: `${explanation.confidence}%`,
                    height: '100%',
                    borderRadius: '3px',
                    backgroundColor: getConfidenceColor(explanation.confidence),
                    transition: 'width 0.6s ease',
                  }}
                />
              </div>
              <span
                className={styles.confidenceValue}
                style={{ color: getConfidenceColor(explanation.confidence) }}
              >
                {explanation.confidence}%
              </span>
            </div>

            {explanation.dataSources.length > 0 && (
              <>
                <div className={styles.sourcesTitle}>Data Sources</div>
                {explanation.dataSources.map((src, i) => (
                  <div key={i} className={styles.sourceItem}>
                    {src.url ? (
                      <a
                        href={src.url}
                        target="_blank"
                        rel="noopener noreferrer"
                        className={styles.sourceLink}
                      >
                        {src.name}
                      </a>
                    ) : (
                      src.name
                    )}
                  </div>
                ))}
              </>
            )}
          </div>
        )}
      </div>
    </>
  );
}
