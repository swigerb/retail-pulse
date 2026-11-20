import type { ReactElement } from 'react';
import type { PiiRedactionType } from '../../types';
import { PiiRedactionBadge } from './PiiRedactionBadge';

export function renderWithRedactions(content: string): (string | ReactElement)[] {
  const regex = /\[REDACTED:(\w+)\]/g;
  const parts: (string | ReactElement)[] = [];
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
