import { useState } from 'react';
import { Button, Text, Textarea, Badge, makeStyles } from '@fluentui/react-components';
import type { PlanClarificationPrompt } from '../../types';

export interface PlanClarificationCardProps {
  planId: string;
  requestId: string;
  prompt: PlanClarificationPrompt | null;
  submitting?: boolean;
  onAnswer: (answer: string) => void;
}

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: '12px',
    backgroundColor: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent-border)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  question: {
    fontSize: '14px',
    lineHeight: 1.5,
    color: 'var(--color-text)',
  },
  actions: {
    display: 'flex',
    gap: '8px',
  },
});

export function PlanClarificationCard({
  planId,
  requestId,
  prompt,
  submitting,
  onAnswer,
}: PlanClarificationCardProps) {
  const styles = useStyles();
  const [answer, setAnswer] = useState('');

  return (
    <div className={styles.card} data-testid="plan-clarification-card" data-plan-id={planId} data-request-id={requestId}>
      <div className={styles.header}>
        <Badge appearance="filled" color="informative">❓ Clarification</Badge>
        {prompt?.specialistKey && (
          <Text style={{ fontSize: '12px', color: 'var(--color-text-muted)' }}>
            {prompt.specialistKey} · step {prompt.stepIndex + 1}
          </Text>
        )}
      </div>
      <Text className={styles.question}>
        {prompt?.question ?? 'The specialist needs more information to continue.'}
      </Text>
      <Textarea
        value={answer}
        onChange={(_e, data) => setAnswer(data.value)}
        placeholder="Answer the question so the plan can continue…"
        aria-label="Clarification answer"
        resize="vertical"
        data-testid="plan-clarification-answer"
      />
      <div className={styles.actions}>
        <Button
          appearance="primary"
          onClick={() => onAnswer(answer.trim())}
          disabled={submitting || answer.trim().length === 0}
          data-testid="plan-clarification-submit"
          aria-label="Submit clarification answer"
        >
          {submitting ? 'Sending…' : 'Send answer'}
        </Button>
      </div>
    </div>
  );
}
