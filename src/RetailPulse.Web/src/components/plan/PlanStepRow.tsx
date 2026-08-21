import { useState, lazy, Suspense } from 'react';
import { Badge, Spinner, Text, makeStyles } from '@fluentui/react-components';
import { ChevronRight16Regular } from '@fluentui/react-icons';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { PlanStep } from '../../types';
import { PLAN_STEP_STATUS_META } from './statusMeta';
import { ErrorBoundary } from '../ErrorBoundary';
import { sanitizeMessage } from '../../utils';

const ChartRenderer = lazy(() => import('../ChartRenderer'));

export interface PlanStepRowProps {
  step: PlanStep;
}

const useStyles = makeStyles({
  row: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '10px 12px',
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '10px',
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    flexWrap: 'wrap',
  },
  index: {
    fontFamily: "'Courier New', monospace",
    fontSize: '12px',
    color: 'var(--color-text-muted)',
  },
  specialist: {
    fontWeight: 600,
    fontSize: '13px',
    color: 'var(--color-text)',
  },
  action: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    lineHeight: 1.4,
    flexBasis: '100%',
  },
  statusPill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: '999px',
    fontSize: '11px',
    fontWeight: 600,
    textTransform: 'uppercase',
    letterSpacing: '0.4px',
    border: '1px solid var(--color-border)',
  },
  toggle: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: '11px',
    color: 'var(--brand-accent)',
    background: 'var(--brand-accent-soft)',
    padding: '3px 10px',
    borderRadius: '999px',
    border: '1px solid var(--brand-accent-border)',
    cursor: 'pointer',
  },
  detail: {
    marginTop: '4px',
    background: 'var(--color-bg-elevated)',
    border: '1px solid var(--color-border)',
    borderRadius: '8px',
    padding: '10px 12px',
    fontSize: '12px',
    color: 'var(--color-text)',
  },
  meta: {
    fontSize: '11px',
    color: 'var(--color-text-subtle)',
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
  },
  loading: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '12px',
    color: 'var(--color-text-muted)',
  },
  error: {
    color: 'var(--color-danger, var(--brand-primary))',
  },
});

function formatMs(ms?: number | null): string | null {
  if (ms == null || !Number.isFinite(ms)) return null;
  if (ms < 1000) return `${Math.max(0, Math.round(ms))} ms`;
  return `${(ms / 1000).toFixed(1)} s`;
}

export function PlanStepRow({ step }: PlanStepRowProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const meta = PLAN_STEP_STATUS_META[step.status];
  const hasResult = Boolean(step.result && step.result.trim().length > 0);
  const hasError = Boolean(step.error && step.error.trim().length > 0);
  const hasCharts = Array.isArray(step.charts) && step.charts.length > 0;
  const inspectable = hasResult || hasError || hasCharts;

  const durationText = formatMs(step.durationMs);
  const tokensText = step.totalTokens != null ? `🪙 ${step.totalTokens.toLocaleString()}` : null;

  return (
    <div
      className={styles.row}
      data-testid="plan-step-row"
      data-step-index={step.stepIndex}
      data-step-status={step.status}
    >
      <div className={styles.head}>
        <span className={styles.index}>{step.stepIndex + 1}.</span>
        <Badge appearance="tint" data-testid="plan-step-specialist" title={step.specialistKey}>
          {step.specialistKey}
        </Badge>
        <span
          className={styles.statusPill}
          style={{ color: meta.fg, background: meta.bg, borderColor: meta.border }}
          role="status"
          aria-label={`Step status ${meta.label}`}
          data-testid="plan-step-status-pill"
        >
          <span aria-hidden="true">
            {step.status === 'running' ? <Spinner size="tiny" /> : meta.icon}
          </span>
          <span>{meta.label}</span>
        </span>
        {step.status === 'running' && (
          <span className={styles.loading} aria-live="polite">
            <Text>Working…</Text>
          </span>
        )}
        {(durationText || tokensText) && (
          <span className={styles.meta}>
            {durationText && <span>⏱ {durationText}</span>}
            {tokensText && <span>{tokensText}</span>}
          </span>
        )}
        {inspectable && (
          <button
            type="button"
            className={styles.toggle}
            onClick={() => setExpanded(v => !v)}
            aria-expanded={expanded}
            aria-label={expanded ? `Hide result for step ${step.stepIndex + 1}` : `Show result for step ${step.stepIndex + 1}`}
            data-testid="plan-step-toggle"
          >
            {expanded ? 'Hide result' : 'View result'}
            <ChevronRight16Regular
              style={{
                transform: expanded ? 'rotate(90deg)' : 'rotate(0deg)',
                transition: 'transform 0.15s ease',
              }}
            />
          </button>
        )}
      </div>
      {step.action && <Text className={styles.action}>{step.action}</Text>}
      {expanded && (
        <div className={styles.detail} data-testid="plan-step-detail">
          {hasError && (
            <div className={styles.error} data-testid="plan-step-error">
              <strong>Error:</strong> {step.error}
            </div>
          )}
          {hasResult && (
            <div className="markdown-body" data-testid="plan-step-result">
              <ReactMarkdown remarkPlugins={[remarkGfm]}>
                {sanitizeMessage(step.result ?? '')}
              </ReactMarkdown>
            </div>
          )}
          {hasCharts && (
            <ErrorBoundary fallback={<div>Chart failed to render.</div>}>
              <Suspense
                fallback={
                  <div className={styles.loading}>
                    <Spinner size="tiny" />
                    <Text>Loading chart…</Text>
                  </div>
                }
              >
                <ChartRenderer charts={step.charts ?? []} />
              </Suspense>
            </ErrorBoundary>
          )}
        </div>
      )}
    </div>
  );
}

export const __test__ = { formatMs };
