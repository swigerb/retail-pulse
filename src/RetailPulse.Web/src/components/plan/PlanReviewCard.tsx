import { useState, useCallback, useMemo } from 'react';
import {
  Button,
  Text,
  Textarea,
  Badge,
  Input,
  makeStyles,
} from '@fluentui/react-components';
import {
  Dismiss20Regular,
  Add20Regular,
  Edit20Regular,
  CheckmarkCircle20Regular,
  ArrowUndo20Regular,
} from '@fluentui/react-icons';
import type { PlanReviewStep } from '../../types';

/**
 * Review card for a pending plan proposal. Approve / Reject-with-feedback /
 * Edit are all first-class actions. Reuses the `ApprovalCard` visual language
 * (soft-tint background, section labels, action row) so plan decisions feel
 * like the same interaction pattern as tool approvals — the issue requires
 * consistency and the two flows should not diverge visually.
 */

export interface PlanReviewCardProps {
  planId: string;
  requestId: string;
  round: number;
  request: string;
  revisionReason?: string | null;
  steps: PlanReviewStep[];
  decisionInFlight?: 'approve' | 'reject' | 'edit';
  resolvedKind?: 'approve' | 'reject' | 'edit';
  onApprove: (comment?: string) => void;
  onReject: (feedback: string) => void;
  onEdit: (editedSteps: PlanReviewStep[]) => void;
}

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: '12px',
    animation: 'messageIn 0.3s ease',
    backgroundColor: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent-border)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    flexWrap: 'wrap',
  },
  headerLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flex: 1,
    minWidth: 0,
  },
  title: {
    fontSize: '14px',
    fontWeight: 600,
    color: 'var(--color-text)',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  sectionLabel: {
    fontSize: '11px',
    fontWeight: 600,
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    color: 'var(--color-text-subtle)',
  },
  sectionText: {
    fontSize: '13px',
    lineHeight: 1.5,
    color: 'var(--color-text)',
  },
  stepList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  stepRow: {
    display: 'flex',
    gap: '8px',
    alignItems: 'flex-start',
    padding: '8px 12px',
    borderRadius: '8px',
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
  },
  stepIndex: {
    fontFamily: "'Courier New', monospace",
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    minWidth: '24px',
  },
  stepBody: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  stepSpecialist: {
    fontWeight: 600,
    fontSize: '13px',
    color: 'var(--color-text)',
  },
  stepAction: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    lineHeight: 1.4,
  },
  stepActions: {
    display: 'flex',
    flexDirection: 'row',
    gap: '4px',
  },
  actions: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    marginTop: '4px',
  },
  feedbackArea: {
    marginTop: '4px',
  },
  editHint: {
    fontSize: '11px',
    color: 'var(--color-text-subtle)',
  },
});

export function PlanReviewCard({
  planId,
  requestId,
  round,
  request,
  revisionReason,
  steps,
  decisionInFlight,
  resolvedKind,
  onApprove,
  onReject,
  onEdit,
}: PlanReviewCardProps) {
  const styles = useStyles();
  const [mode, setMode] = useState<'view' | 'reject' | 'edit'>('view');
  const [comment, setComment] = useState('');
  const [feedback, setFeedback] = useState('');
  const [draft, setDraft] = useState<PlanReviewStep[]>(() =>
    steps.map(s => ({ ...s })),
  );

  const disabled = Boolean(decisionInFlight) || Boolean(resolvedKind);

  const startEdit = useCallback(() => {
    setDraft(steps.map(s => ({ ...s })));
    setMode('edit');
  }, [steps]);

  const cancelEdit = useCallback(() => {
    setDraft(steps.map(s => ({ ...s })));
    setMode('view');
  }, [steps]);

  const updateDraftStep = useCallback((idx: number, patch: Partial<PlanReviewStep>) => {
    setDraft(prev => prev.map((s, i) => (i === idx ? { ...s, ...patch } : s)));
  }, []);

  const removeDraftStep = useCallback((idx: number) => {
    setDraft(prev => prev.filter((_, i) => i !== idx));
  }, []);

  const insertDraftStep = useCallback((afterIdx: number) => {
    setDraft(prev => {
      const template = prev[afterIdx] ??
        prev[prev.length - 1] ?? { specialistKey: '', intent: '', action: '' };
      const inserted: PlanReviewStep = {
        specialistKey: template.specialistKey,
        intent: template.intent,
        action: '',
      };
      const next = [...prev];
      next.splice(afterIdx + 1, 0, inserted);
      return next;
    });
  }, []);

  const editSummary = useMemo(() => {
    const removed = steps.length - draft.length;
    return removed === 0 ? 'Amend a step or remove any that shouldn’t run.' : `${removed} step(s) removed. Save to confirm.`;
  }, [draft.length, steps.length]);

  const handleSubmitEdit = () => {
    // Trim actions; drop empty specialist keys.
    const cleaned = draft
      .map(s => ({
        specialistKey: s.specialistKey.trim(),
        intent: (s.intent ?? '').trim() || s.specialistKey.trim(),
        action: s.action.trim(),
      }))
      .filter(s => s.specialistKey.length > 0);
    onEdit(cleaned);
  };

  return (
    <div className={styles.card} data-testid="plan-review-card" data-plan-id={planId} data-request-id={requestId}>
      <div className={styles.header}>
        <div className={styles.headerLeft}>
          <Badge appearance="filled" color="informative" data-testid="plan-review-round">
            Review · Round {round + 1}
          </Badge>
          <Text className={styles.title} truncate>{request}</Text>
        </div>
      </div>

      {revisionReason && (
        <div className={styles.section}>
          <span className={styles.sectionLabel}>Why we replanned</span>
          <span className={styles.sectionText}>{revisionReason}</span>
        </div>
      )}

      <div className={styles.section}>
        <span className={styles.sectionLabel}>
          {mode === 'edit' ? 'Edit proposed steps' : 'Proposed steps'}
        </span>
        {mode === 'edit' && <span className={styles.editHint}>{editSummary}</span>}
        <div className={styles.stepList} data-testid="plan-review-steps">
          {(mode === 'edit' ? draft : steps).map((step, idx) => (
            <div key={`review-step-${idx}`} className={styles.stepRow} data-testid={`plan-review-step-${idx}`}>
              <span className={styles.stepIndex}>{idx + 1}.</span>
              <div className={styles.stepBody}>
                <span className={styles.stepSpecialist}>{step.specialistKey || '—'}</span>
                {mode === 'edit' ? (
                  <>
                    <Input
                      value={step.specialistKey}
                      size="small"
                      onChange={(_e, data) =>
                        updateDraftStep(idx, { specialistKey: data.value })
                      }
                      aria-label={`Specialist key for step ${idx + 1}`}
                      data-testid={`plan-review-specialist-${idx}`}
                    />
                    <Textarea
                      value={step.action}
                      size="small"
                      resize="vertical"
                      onChange={(_e, data) =>
                        updateDraftStep(idx, { action: data.value })
                      }
                      aria-label={`Action for step ${idx + 1}`}
                      data-testid={`plan-review-action-${idx}`}
                    />
                  </>
                ) : (
                  <span className={styles.stepAction}>{step.action || '(no action recorded)'}</span>
                )}
              </div>
              {mode === 'edit' && (
                <div className={styles.stepActions}>
                  <Button
                    appearance="subtle"
                    icon={<Add20Regular />}
                    aria-label={`Insert step after step ${idx + 1}`}
                    onClick={() => insertDraftStep(idx)}
                    data-testid={`plan-review-insert-${idx}`}
                  />
                  <Button
                    appearance="subtle"
                    icon={<Dismiss20Regular />}
                    aria-label={`Remove step ${idx + 1}`}
                    onClick={() => removeDraftStep(idx)}
                    data-testid={`plan-review-remove-${idx}`}
                  />
                </div>
              )}
            </div>
          ))}
          {mode === 'edit' && draft.length === 0 && (
            <Text style={{ fontSize: '12px', color: 'var(--color-text-subtle)' }}>
              No steps remain. Saving will terminate the plan without executing anything.
            </Text>
          )}
        </div>
      </div>

      {mode === 'reject' && (
        <div className={styles.section}>
          <span className={styles.sectionLabel}>Why is this plan wrong?</span>
          <Textarea
            className={styles.feedbackArea}
            value={feedback}
            onChange={(_e, data) => setFeedback(data.value)}
            placeholder="Tell the planner what to change (required)…"
            size="small"
            resize="vertical"
            aria-label="Rejection feedback for the planner"
            data-testid="plan-review-feedback"
          />
        </div>
      )}

      {mode === 'view' && !resolvedKind && (
        <Textarea
          className={styles.feedbackArea}
          value={comment}
          onChange={(_e, data) => setComment(data.value)}
          placeholder="Optional note for the audit log…"
          size="small"
          resize="vertical"
          aria-label="Optional approval comment"
          data-testid="plan-review-comment"
        />
      )}

      {resolvedKind ? (
        <div className={styles.actions} data-testid="plan-review-resolved">
          <Badge appearance="filled" color={resolvedKind === 'reject' ? 'warning' : 'success'}>
            {resolvedKind === 'approve' && '✓ Plan approved'}
            {resolvedKind === 'edit' && '✏️ Plan edited'}
            {resolvedKind === 'reject' && '↺ Replan requested'}
          </Badge>
        </div>
      ) : mode === 'view' ? (
        <div className={styles.actions}>
          <Button
            appearance="primary"
            icon={<CheckmarkCircle20Regular />}
            onClick={() => onApprove(comment.trim() || undefined)}
            disabled={disabled}
            data-testid="plan-review-approve"
            aria-label="Approve plan and run it"
          >
            {decisionInFlight === 'approve' ? 'Approving…' : 'Approve'}
          </Button>
          <Button
            appearance="outline"
            icon={<ArrowUndo20Regular />}
            onClick={() => setMode('reject')}
            disabled={disabled}
            data-testid="plan-review-reject"
            aria-label="Reject plan and ask the planner to try again"
          >
            Reject
          </Button>
          <Button
            appearance="outline"
            icon={<Edit20Regular />}
            onClick={startEdit}
            disabled={disabled}
            data-testid="plan-review-edit"
            aria-label="Edit plan steps before running"
          >
            Edit
          </Button>
        </div>
      ) : mode === 'reject' ? (
        <div className={styles.actions}>
          <Button
            appearance="primary"
            onClick={() => onReject(feedback.trim())}
            disabled={disabled || feedback.trim().length === 0}
            data-testid="plan-review-submit-reject"
            aria-label="Send feedback and request a replan"
          >
            {decisionInFlight === 'reject' ? 'Submitting…' : 'Send feedback & replan'}
          </Button>
          <Button
            appearance="subtle"
            onClick={() => setMode('view')}
            disabled={disabled}
            data-testid="plan-review-cancel-reject"
          >
            Cancel
          </Button>
        </div>
      ) : (
        <div className={styles.actions}>
          <Button
            appearance="primary"
            onClick={handleSubmitEdit}
            disabled={disabled}
            data-testid="plan-review-submit-edit"
            aria-label="Save edited steps and run the plan"
          >
            {decisionInFlight === 'edit' ? 'Saving…' : 'Save & run edited plan'}
          </Button>
          <Button
            appearance="subtle"
            onClick={cancelEdit}
            disabled={disabled}
            data-testid="plan-review-cancel-edit"
          >
            Cancel
          </Button>
        </div>
      )}
    </div>
  );
}
