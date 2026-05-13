import { useState, useCallback } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { MARGIN_COLORS } from '../../constants/agentRouting';
import type { EscalationStep, EscalationLevel } from '../../types';

interface EscalationPathProps {
  steps: EscalationStep[];
  defaultExpanded?: boolean;
}

const LEVEL_COLORS: Record<EscalationLevel, string> = {
  L1: MARGIN_COLORS.escalationL1,
  L2: MARGIN_COLORS.escalationL2,
  L3: MARGIN_COLORS.escalationL3,
};

const useStyles = makeStyles({
  wrapper: {
    padding: '20px',
    backgroundColor: MARGIN_COLORS.cardBg,
    border: `1px solid ${MARGIN_COLORS.cardBorder}`,
    borderRadius: '12px',
    cursor: 'pointer',
    userSelect: 'none',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: '12px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: '#6366f1',
  },
  expandHint: {
    fontSize: '12px',
    color: '#94a3b8',
  },
  timeline: {
    display: 'flex',
    flexDirection: 'column',
    gap: '0px',
    paddingLeft: '12px',
  },
  stepRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '16px',
    position: 'relative',
  },
  nodeCol: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    width: '32px',
    flexShrink: 0,
  },
  circle: {
    width: '32px',
    height: '32px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '11px',
    fontWeight: '700',
    color: '#fff',
    flexShrink: 0,
    position: 'relative',
    zIndex: 1,
  },
  connector: {
    width: '2px',
    flex: 1,
    minHeight: '24px',
    backgroundColor: MARGIN_COLORS.escalationLine,
  },
  content: {
    flex: 1,
    paddingBottom: '20px',
  },
  agentName: {
    fontSize: '14px',
    fontWeight: '600',
    color: '#e2e8f0',
    marginBottom: '4px',
  },
  context: {
    fontSize: '13px',
    color: '#94a3b8',
    lineHeight: '1.5',
    marginBottom: '4px',
  },
  meta: {
    display: 'flex',
    gap: '12px',
    fontSize: '12px',
    color: '#64748b',
  },
  collapsedRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  collapsedBadge: {
    fontSize: '11px',
    fontWeight: '700',
    padding: '4px 10px',
    borderRadius: '12px',
    color: '#fff',
  },
  collapsedArrow: {
    color: 'rgba(255,255,255,0.2)',
    fontSize: '14px',
  },
  empty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '120px',
    color: '#94a3b8',
    fontSize: '14px',
  },
});

function formatTime(ms: number): string {
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${ms}ms`;
}

function formatTimestamp(ts: string): string {
  try {
    return new Date(ts).toLocaleTimeString('en-US', {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  } catch {
    return ts;
  }
}

// Inline pulse animation for the current step
const pulseKeyframes = `
@keyframes escalation-pulse {
  0% { box-shadow: 0 0 0 0 rgba(139,92,246,0.5); }
  70% { box-shadow: 0 0 0 10px rgba(139,92,246,0); }
  100% { box-shadow: 0 0 0 0 rgba(139,92,246,0); }
}`;

let styleInjected = false;
function injectPulseStyle() {
  if (styleInjected || typeof document === 'undefined') return;
  const sheet = document.createElement('style');
  sheet.textContent = pulseKeyframes;
  document.head.appendChild(sheet);
  styleInjected = true;
}

export function EscalationPath({ steps, defaultExpanded = false }: EscalationPathProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(defaultExpanded);
  const toggle = useCallback(() => setExpanded((v) => !v), []);

  injectPulseStyle();

  if (!steps.length) {
    return (
      <div className={styles.wrapper}>
        <div className={styles.title}>Escalation Path</div>
        <div className={styles.empty}>No escalation data available</div>
      </div>
    );
  }

  return (
    <div
      className={styles.wrapper}
      onClick={toggle}
      data-testid="escalation-path"
      role="button"
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') toggle(); }}
    >
      <div className={styles.titleRow}>
        <span className={styles.title}>Escalation Path</span>
        <span className={styles.expandHint}>
          {expanded ? '▾ Click to collapse' : '▸ Click to expand'}
        </span>
      </div>

      {!expanded ? (
        <div className={styles.collapsedRow}>
          {steps.map((s, i) => (
            <span key={s.level + i} style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <span
                className={styles.collapsedBadge}
                style={{
                  backgroundColor: s.isCurrent
                    ? MARGIN_COLORS.escalationCurrent
                    : LEVEL_COLORS[s.level],
                  ...(s.isCurrent
                    ? { animation: 'escalation-pulse 2s infinite' }
                    : {}),
                }}
              >
                {s.level}
              </span>
              {i < steps.length - 1 && (
                <span className={styles.collapsedArrow}>→</span>
              )}
            </span>
          ))}
        </div>
      ) : (
        <div className={styles.timeline}>
          {steps.map((s, i) => {
            const isLast = i === steps.length - 1;
            const circleColor = s.isCurrent
              ? MARGIN_COLORS.escalationCurrent
              : LEVEL_COLORS[s.level];

            return (
              <div key={s.level + i} className={styles.stepRow}>
                <div className={styles.nodeCol}>
                  <div
                    className={styles.circle}
                    style={{
                      backgroundColor: circleColor,
                      ...(s.isCurrent
                        ? { animation: 'escalation-pulse 2s infinite' }
                        : {}),
                    }}
                  >
                    {s.level}
                  </div>
                  {!isLast && <div className={styles.connector} />}
                </div>
                <div className={styles.content}>
                  <div className={styles.agentName}>
                    {s.agentName}
                    {s.isCurrent && (
                      <span style={{
                        marginLeft: '8px',
                        fontSize: '11px',
                        padding: '2px 8px',
                        borderRadius: '4px',
                        backgroundColor: 'rgba(139,92,246,0.15)',
                        color: MARGIN_COLORS.escalationCurrent,
                        fontWeight: 500,
                      }}>
                        Active
                      </span>
                    )}
                  </div>
                  <div className={styles.context}>{s.contextAdded}</div>
                  <div className={styles.meta}>
                    <span>⏱ {formatTime(s.timeSpentMs)}</span>
                    <span>{formatTimestamp(s.timestamp)}</span>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
