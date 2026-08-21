import { makeStyles, tokens } from '@fluentui/react-components';
import type { SafetyBlockDisplayModel } from '../../types';
import { buildSafetyBlockDisplay } from '../../utils/safetyDisplay';

/**
 * Renders in the Knowledge panel when an ingestion request is rejected /
 * quarantined by the safety layer. Never displays the document body or any
 * upload internals — only the plain-language reason from the display model
 * and the document title (which the user chose).
 */
export interface KnowledgeIngestionBlockProps {
  documentTitle?: string;
  display?: SafetyBlockDisplayModel;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    gap: '12px',
    padding: '14px 18px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorStatusWarningBackground1,
    border: `1px solid ${tokens.colorStatusWarningBorder1}`,
    color: tokens.colorNeutralForeground1,
    fontSize: '14px',
    lineHeight: '1.5',
  },
  icon: {
    fontSize: '20px',
    flexShrink: 0,
    lineHeight: '1.5',
  },
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    minWidth: 0,
  },
  title: {
    color: tokens.colorNeutralForeground1,
    fontWeight: 600,
  },
  reason: {
    color: tokens.colorNeutralForeground2,
    fontSize: '13px',
  },
  docTitle: {
    color: tokens.colorNeutralForeground3,
    fontSize: '12px',
    fontStyle: 'italic',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  metaRow: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
  },
  metaChip: {
    display: 'inline-flex',
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '11px',
    fontWeight: 600,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

export function KnowledgeIngestionBlock({ documentTitle, display }: KnowledgeIngestionBlockProps) {
  const styles = useStyles();
  const model = display ?? buildSafetyBlockDisplay({ stage: 'ingestion', decision: 'Blocked' });

  return (
    <div
      className={styles.container}
      data-testid="knowledge-ingestion-block"
      data-safety-stage="ingestion"
      data-safety-family={model.family}
      role="alert"
    >
      <span className={styles.icon} aria-hidden="true">🛡️</span>
      <div className={styles.content}>
        <span className={styles.title}>Document quarantined by content safety</span>
        <span className={styles.reason}>{model.reason}</span>
        {documentTitle && (
          <span className={styles.docTitle} data-testid="knowledge-ingestion-title" title={documentTitle}>
            {documentTitle}
          </span>
        )}
        {(model.categoryLabel || model.severityLabel) && (
          <div className={styles.metaRow} data-testid="knowledge-ingestion-meta">
            {model.categoryLabel && (
              <span className={styles.metaChip} data-testid="knowledge-ingestion-category">
                Category: {model.categoryLabel}
              </span>
            )}
            {model.severityLabel && (
              <span className={styles.metaChip} data-testid="knowledge-ingestion-severity">
                Severity: {model.severityLabel}
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
