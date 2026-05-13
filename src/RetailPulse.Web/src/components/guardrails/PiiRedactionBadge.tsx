import { makeStyles, Tooltip } from '@fluentui/react-components';
import type { PiiRedactionType } from '../../types';
import React from 'react';

export interface PiiRedactionBadgeProps {
  redactionType: PiiRedactionType;
}

const useStyles = makeStyles({
  badge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '1px 8px',
    borderRadius: '10px',
    fontSize: '12px',
    fontWeight: '500',
    backgroundColor: 'rgba(168, 85, 247, 0.12)',
    color: '#c084fc',
    border: '1px solid rgba(168, 85, 247, 0.25)',
    cursor: 'default',
    verticalAlign: 'middle',
    lineHeight: '1.6',
  },
  icon: {
    fontSize: '11px',
  },
});

const TYPE_LABELS: Record<PiiRedactionType, { label: string; icon: string }> = {
  email: { label: 'Email', icon: '📧' },
  phone: { label: 'Phone', icon: '📱' },
  ssn: { label: 'SSN', icon: '🔢' },
  address: { label: 'Address', icon: '📍' },
  name: { label: 'Name', icon: '👤' },
  credit_card: { label: 'Card', icon: '💳' },
  unknown: { label: 'PII', icon: '🔐' },
};

export function PiiRedactionBadge({ redactionType }: PiiRedactionBadgeProps) {
  const styles = useStyles();
  const config = TYPE_LABELS[redactionType] || TYPE_LABELS.unknown;

  return (
    <Tooltip
      content="Sensitive information was automatically redacted for your protection"
      relationship="description"
    >
      <span className={styles.badge} data-testid="pii-redaction-badge">
        <span className={styles.icon}>{config.icon}</span>
        <span>[REDACTED:{config.label}]</span>
      </span>
    </Tooltip>
  );
}

/**
 * Parses message content and replaces [REDACTED:type] markers with PiiRedactionBadge components.
 * Returns an array of React nodes.
 */
export function renderWithRedactions(content: string): (string | React.ReactElement)[] {
  const regex = /\[REDACTED:(\w+)\]/g;
  const parts: (string | React.ReactElement)[] = [];
  let lastIndex = 0;
  let match: RegExpExecArray | null;

  while ((match = regex.exec(content)) !== null) {
    if (match.index > lastIndex) {
      parts.push(content.slice(lastIndex, match.index));
    }
    const type = match[1].toLowerCase() as PiiRedactionType;
    parts.push(<PiiRedactionBadge key={`redact-${match.index}`} redactionType={type} />);
    lastIndex = match.index + match[0].length;
  }

  if (lastIndex < content.length) {
    parts.push(content.slice(lastIndex));
  }

  return parts;
}
