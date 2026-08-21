import { makeStyles, tokens } from '@fluentui/react-components';

/**
 * Accessible status indicator for the model-based Content Safety layer.
 * Renders enabled vs disabled state with text, an icon, AND semantics
 * (`role="status"`, `aria-label`) so users can't distinguish states by
 * colour alone.
 *
 * The component intentionally exposes NO endpoint, credential, or
 * threshold information — the surrounding `GuardrailsConfig` page owns any
 * safe runtime toggle rendering.
 */
export interface ContentSafetyStatusBadgeProps {
  enabled: boolean;
  /** When `enabled`, the deployment's fail policy (`FailOpen` / `FailClosed`). */
  failPolicy?: 'FailOpen' | 'FailClosed';
  /** Optional extra qualifier text (e.g. "output check off"). */
  detail?: string;
}

const useStyles = makeStyles({
  container: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '10px',
    padding: '8px 14px',
    borderRadius: tokens.borderRadiusLarge,
    fontSize: '13px',
    fontWeight: 500,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
  },
  enabled: {
    borderTopColor: tokens.colorPaletteGreenBorder2,
    borderRightColor: tokens.colorPaletteGreenBorder2,
    borderBottomColor: tokens.colorPaletteGreenBorder2,
    borderLeftColor: tokens.colorPaletteGreenBorder2,
    backgroundColor: tokens.colorPaletteGreenBackground1,
    color: tokens.colorPaletteGreenForeground1,
  },
  disabled: {
    borderTopColor: tokens.colorPaletteRedBorder1,
    borderRightColor: tokens.colorPaletteRedBorder1,
    borderBottomColor: tokens.colorPaletteRedBorder1,
    borderLeftColor: tokens.colorPaletteRedBorder1,
    backgroundColor: tokens.colorPaletteRedBackground1,
    color: tokens.colorPaletteRedForeground1,
  },
  icon: {
    fontSize: '16px',
    lineHeight: 1,
  },
  labelBlock: {
    display: 'flex',
    flexDirection: 'column',
    lineHeight: 1.2,
  },
  detail: {
    fontSize: '11px',
    fontWeight: 400,
    color: tokens.colorNeutralForeground3,
  },
});

export function ContentSafetyStatusBadge({
  enabled,
  failPolicy,
  detail,
}: ContentSafetyStatusBadgeProps) {
  const styles = useStyles();
  const cls = enabled ? styles.enabled : styles.disabled;
  const label = enabled ? 'Content safety enabled' : 'Content safety disabled';
  const iconGlyph = enabled ? '✓' : '⚠';
  const ariaLabel = enabled
    ? `Content safety is enabled${failPolicy ? `, fail policy ${failPolicy}` : ''}${detail ? `, ${detail}` : ''}.`
    : `Content safety is disabled. Model-based content safety checks are off.${detail ? ` ${detail}` : ''}`;

  return (
    <span
      className={`${styles.container} ${cls}`}
      data-testid="content-safety-status-badge"
      data-safety-enabled={enabled ? 'true' : 'false'}
      role="status"
      aria-label={ariaLabel}
    >
      <span className={styles.icon} aria-hidden="true">{iconGlyph}</span>
      <span className={styles.labelBlock}>
        <span>{label}</span>
        {(enabled && failPolicy) || detail ? (
          <span className={styles.detail}>
            {enabled && failPolicy ? `Fail policy: ${failPolicy}` : null}
            {enabled && failPolicy && detail ? ' · ' : null}
            {detail}
          </span>
        ) : null}
      </span>
    </span>
  );
}
