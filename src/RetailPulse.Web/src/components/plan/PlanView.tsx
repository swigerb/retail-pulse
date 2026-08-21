import { lazy, Suspense, useMemo } from 'react';
import { Badge, Button, ProgressBar, Text, makeStyles } from '@fluentui/react-components';
import { Dismiss20Regular } from '@fluentui/react-icons';
import type { ActivePlanState } from '../../state/planReducer';
import type { PlanReviewStep } from '../../types';
import { PLAN_STATUS_META, formatElapsed, progressCounts } from './statusMeta';
import { PlanStepRow } from './PlanStepRow';
import { PlanReviewCard } from './PlanReviewCard';
import { PlanClarificationCard } from './PlanClarificationCard';
import { ErrorBoundary } from '../ErrorBoundary';

// Lazy-load `ChartRenderer` on the same chunk boundary the fast-path chat
// bubble uses (see `ChatPanel.tsx`). Sharing the module keeps chart
// rendering behavior (accepts, unavailable diagnostic, all 9 canonical
// types) identical across chat, plan-first immediate, and plan-review
// resume — the "one chart contract on both paths" invariant from issues
// #137 and #141. Matches how ChatPanel and PlanStepRow load it.
const ChartRenderer = lazy(() => import('../ChartRenderer'));

/**
 * Top-level plan surface (issue #96). Renders a summary strip (progress,
 * elapsed, status), the ordered step list, any pending review or
 * clarification interaction, the terminal reply if any, and connection-loss
 * banner. The layout collapses to a single column at the 768px breakpoint
 * defined in `Dashboard.tsx`.
 */

export interface PlanViewProps {
  active: ActivePlanState;
  connected: boolean;
  onApprove: (comment?: string) => void;
  onReject: (feedback: string) => void;
  onEdit: (editedSteps: PlanReviewStep[]) => void;
  onClarify: (answer: string) => void;
  onClose?: () => void;
}

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: '14px',
    background: 'var(--color-surface-alt, var(--color-surface))',
    border: '1px solid var(--color-border)',
    boxShadow: '0 4px 24px rgba(0,0,0,0.15)',
  },
  headerRow: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '8px',
    flexWrap: 'wrap',
  },
  title: {
    fontSize: '15px',
    fontWeight: 600,
    color: 'var(--color-text)',
  },
  request: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: 1.5,
  },
  statusPill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 10px',
    borderRadius: '999px',
    fontSize: '12px',
    fontWeight: 600,
    letterSpacing: '0.4px',
    border: '1px solid var(--color-border)',
  },
  summaryRow: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
    gap: '8px',
  },
  stat: {
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '10px',
    padding: '8px 12px',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  statLabel: {
    fontSize: '11px',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    color: 'var(--color-text-subtle)',
  },
  statValue: {
    fontSize: '15px',
    fontWeight: 700,
    color: 'var(--brand-accent)',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  sectionLabel: {
    fontSize: '11px',
    fontWeight: 600,
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    color: 'var(--color-text-subtle)',
  },
  stepList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  banner: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '10px 12px',
    borderRadius: '8px',
    fontSize: '12px',
    background: 'var(--color-surface-hover)',
    border: '1px solid var(--color-border)',
  },
  finalReply: {
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '10px',
    padding: '12px',
    fontSize: '13px',
    lineHeight: 1.6,
    color: 'var(--color-text)',
    whiteSpace: 'pre-wrap',
  },
  '@media (max-width: 768px)': {
    panel: {
      padding: '12px',
      borderRadius: '10px',
    },
  },
});

function tokensText(active: ActivePlanState): string {
  const total =
    active.totalTokens ??
    active.steps.reduce((sum, s) => sum + (s.totalTokens ?? 0), 0);
  return total.toLocaleString();
}

export function PlanView({
  active,
  connected,
  onApprove,
  onReject,
  onEdit,
  onClarify,
  onClose,
}: PlanViewProps) {
  const styles = useStyles();
  const statusMeta = PLAN_STATUS_META[active.status] ?? PLAN_STATUS_META.running;
  const counts = useMemo(() => progressCounts(active.steps), [active.steps]);
  const elapsed = formatElapsed(active.elapsedMs);

  return (
    <div
      className={styles.panel}
      data-testid="plan-view"
      data-plan-id={active.planId}
      data-plan-status={active.status}
      aria-live="polite"
    >
      <div className={styles.headerRow}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: '4px', flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' }}>
            <Text className={styles.title}>Plan</Text>
            <span
              className={styles.statusPill}
              style={{ color: statusMeta.fg, background: statusMeta.bg, borderColor: statusMeta.border }}
              role="status"
              aria-label={`Plan status ${statusMeta.label}`}
              data-testid="plan-status-pill"
            >
              <span aria-hidden="true">{statusMeta.icon}</span>
              <span>{statusMeta.label}</span>
            </span>
            {active.detectedIntents.length > 0 && (
              <Badge appearance="tint" color="informative" data-testid="plan-intents">
                {active.detectedIntents.join(' · ')}
              </Badge>
            )}
          </div>
          <Text className={styles.request}>{active.request}</Text>
        </div>
        {onClose && (
          <Button
            appearance="subtle"
            icon={<Dismiss20Regular />}
            onClick={onClose}
            aria-label="Close plan view"
            data-testid="plan-close-button"
          />
        )}
      </div>

      {!connected && (
        <div className={styles.banner} role="alert" data-testid="plan-connection-warning">
          <span aria-hidden="true">📡</span>
          <Text>
            Real-time telemetry disconnected. Step statuses will resume updating when the connection restores.
          </Text>
        </div>
      )}

      {active.hydrateError && (
        <div className={styles.banner} role="alert" data-testid="plan-hydrate-error">
          <span aria-hidden="true">⚠︎</span>
          <Text>Couldn’t load the latest plan snapshot: {active.hydrateError}</Text>
        </div>
      )}

      <div className={styles.summaryRow} data-testid="plan-summary">
        <div className={styles.stat}>
          <span className={styles.statLabel}>Progress</span>
          <span className={styles.statValue} data-testid="plan-progress-value">
            {counts.completed + counts.failed} / {counts.total}
          </span>
          <ProgressBar
            aria-label={`Plan progress: ${counts.percent}% complete`}
            value={counts.total === 0 ? undefined : counts.percent / 100}
            data-testid="plan-progress-bar"
          />
        </div>
        <div className={styles.stat}>
          <span className={styles.statLabel}>Elapsed</span>
          <span className={styles.statValue} data-testid="plan-elapsed">{elapsed}</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statLabel}>Steps</span>
          <span className={styles.statValue}>
            {counts.running > 0 ? `▶︎ ${counts.running} running` : `${counts.pending} pending`}
          </span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statLabel}>Tokens</span>
          <span className={styles.statValue}>🪙 {tokensText(active)}</span>
        </div>
      </div>

      {active.clarification && (
        <PlanClarificationCard
          planId={active.planId}
          requestId={active.clarification.requestId}
          prompt={active.clarification.prompt}
          submitting={active.clarification.submitting}
          onAnswer={onClarify}
        />
      )}

      {active.review && (
        <PlanReviewCard
          planId={active.planId}
          requestId={active.review.requestId}
          round={active.review.round}
          request={active.request}
          revisionReason={active.review.revisionReason}
          steps={
            active.review.proposal?.steps ??
            active.steps.map(s => ({
              specialistKey: s.specialistKey,
              intent: s.intent,
              action: s.action,
            }))
          }
          decisionInFlight={active.review.decisionInFlight}
          resolvedKind={active.review.resolvedKind}
          onApprove={onApprove}
          onReject={onReject}
          onEdit={onEdit}
        />
      )}

      <div className={styles.section}>
        <span className={styles.sectionLabel}>Steps</span>
        <div className={styles.stepList} data-testid="plan-steps">
          {active.steps.length === 0 ? (
            <div className={styles.banner} data-testid="plan-no-steps-yet">
              <span>Planner is drafting the step list…</span>
            </div>
          ) : (
            active.steps
              .slice()
              .sort((a, b) => a.stepIndex - b.stepIndex)
              .map(step => <PlanStepRow key={step.stepId} step={step} />)
          )}
        </div>
      </div>

      {active.finalReply && (
        <div className={styles.section}>
          <span className={styles.sectionLabel}>Final answer</span>
          <div className={styles.finalReply} data-testid="plan-final-reply">
            {active.finalReply}
          </div>
          {active.terminalReason && (
            <Text style={{ fontSize: '11px', color: 'var(--color-text-subtle)' }}>
              Terminal reason: {active.terminalReason}
            </Text>
          )}
        </div>
      )}

      {active.finalCharts && active.finalCharts.length > 0 && (
        <div
          className={styles.section}
          data-testid="plan-final-charts"
          data-chart-count={active.finalCharts.length}
        >
          <span className={styles.sectionLabel}>Charts</span>
          <ErrorBoundary fallback={<div>Chart failed to render.</div>}>
            <Suspense fallback={<div>Loading charts…</div>}>
              <ChartRenderer charts={active.finalCharts} />
            </Suspense>
          </ErrorBoundary>
        </div>
      )}
    </div>
  );
}
