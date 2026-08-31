import { useState, useEffect, useCallback } from 'react';
import { makeStyles, Button, Text, Spinner, Switch, tokens } from '@fluentui/react-components';
import type { GuardrailsConfigData } from '../../types';
import { fetchGuardrailsConfig, updateGuardrailsConfig, resetGuardrailsConfig } from '../../services/guardrailsApi';
import { ContentSafetyStatusBadge } from './ContentSafetyStatusBadge';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    padding: '24px',
  },
  title: {
    fontSize: '22px',
    fontWeight: '700',
    color: 'var(--color-text, #e2e8f0)',
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: '12px',
    backgroundColor: 'var(--color-surface, #1e293b)',
    border: '1px solid var(--color-border, #334155)',
  },
  sectionTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text, #e2e8f0)',
  },
  sectionSubtitle: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
  },
  layerCallout: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  layerCalloutTitle: {
    fontSize: '13px',
    fontWeight: 600,
    color: 'var(--color-text, #e2e8f0)',
  },
  layerRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    fontSize: '12px',
    color: tokens.colorNeutralForeground2,
    // Break any unbroken token in the explainer sentences (added by #264) so
    // it wraps inside the callout instead of widening it at narrow widths.
    overflowWrap: 'anywhere',
  },
  layerRowHead: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
    minWidth: 0,
  },
  layerRowName: {
    fontSize: '12px',
    fontWeight: 600,
    color: 'var(--color-text, #e2e8f0)',
    overflowWrap: 'anywhere',
  },
  layerNoteStrong: {
    fontSize: '12px',
    fontWeight: 600,
    color: 'var(--color-text, #e2e8f0)',
  },
  toggleRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '12px',
    padding: '8px 0',
    borderBottom: '1px solid var(--color-border, #334155)',
    ':last-child': {
      borderBottom: 'none',
    },
  },
  toggleLabel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    // Let the label column shrink so the longer #264 descriptions wrap here
    // rather than pushing the switch off the row at narrow widths.
    minWidth: 0,
  },
  toggleDescription: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
    overflowWrap: 'anywhere',
  },
  textarea: {
    width: '100%',
    boxSizing: 'border-box',
    minWidth: 0,
    minHeight: '120px',
    padding: '12px',
    borderRadius: '8px',
    backgroundColor: 'var(--color-bg, #0f172a)',
    color: 'var(--color-text, #e2e8f0)',
    border: '1px solid var(--color-border, #334155)',
    fontFamily: "'Courier New', monospace",
    fontSize: '13px',
    resize: 'vertical' as unknown as undefined,
    outline: 'none',
  },
  actions: {
    display: 'flex',
    gap: '12px',
    justifyContent: 'flex-end',
  },
  statusMessage: {
    fontSize: '13px',
    padding: '8px 14px',
    borderRadius: '8px',
  },
  contentSafetyBanner: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  contentSafetyBannerText: {
    fontSize: '13px',
    color: tokens.colorNeutralForeground2,
  },
  readOnlyRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
    padding: '6px 0',
    fontSize: '13px',
    color: tokens.colorNeutralForeground2,
  },
  readOnlyValue: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    maxWidth: '100%',
    // The read-only badges carry long labels such as
    // "MODEL · PROMPT-SHIELD SAFETY"; wrap them within the pill instead of
    // letting them overflow a non-wrapping row.
    overflowWrap: 'anywhere',
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '11px',
    fontWeight: 600,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

export function GuardrailsConfig() {
  const styles = useStyles();
  const [config, setConfig] = useState<GuardrailsConfigData | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);

  useEffect(() => {
    let cancelled = false;
    fetchGuardrailsConfig()
      .then(data => { if (!cancelled) { setConfig(data); setError(null); } })
      .catch(err => { if (!cancelled) setError(err instanceof Error ? err.message : 'Failed to load config'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const handleSave = useCallback(async () => {
    if (!config) return;
    setSaving(true);
    setSuccess(false);
    setError(null);
    try {
      const saved = await updateGuardrailsConfig(config);
      setConfig(saved);
      setSuccess(true);
      setTimeout(() => setSuccess(false), 3000);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save config');
    } finally {
      setSaving(false);
    }
  }, [config]);

  const handleReset = useCallback(async () => {
    setSaving(true);
    setSuccess(false);
    setError(null);
    try {
      const defaults = await resetGuardrailsConfig();
      setConfig(defaults);
      setSuccess(true);
      setTimeout(() => setSuccess(false), 3000);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to reset config');
    } finally {
      setSaving(false);
    }
  }, []);

  if (loading) {
    return (
      <div className={styles.container}>
        <Spinner size="medium" label="Loading configuration..." />
      </div>
    );
  }

  if (!config) {
    return (
      <div className={styles.container}>
        <Text>⚠️ {error || 'Could not load guardrails configuration'}</Text>
      </div>
    );
  }

  return (
    <div className={styles.container} data-testid="guardrails-config">
      <span className={styles.title}>⚙️ Guardrails Configuration</span>

      {config.contentSafety && (
        <div className={styles.section} data-testid="content-safety-runtime-panel">
          <span className={styles.sectionTitle}>🛡️ Content Safety (model-based)</span>
          <span className={styles.sectionSubtitle} data-testid="deployment-managed-note">
            Runtime protections managed by the deployment. You cannot change these here.
          </span>
          <div className={styles.contentSafetyBanner}>
            <ContentSafetyStatusBadge
              enabled={config.contentSafety.enabled}
              failPolicy={config.contentSafety.failPolicy}
              detail={
                config.contentSafety.enabled
                  ? undefined
                  : 'Only pattern-based guardrails are running for this deployment.'
              }
            />
            <span className={styles.contentSafetyBannerText}>
              {config.contentSafety.enabled
                ? 'Model-based classification is active. Runtime settings are managed by the deployment.'
                : 'Model-based classification is off. Pattern-based guardrails still apply.'}
            </span>
          </div>
          {config.contentSafety.enabled && (
            <div>
              <div className={styles.readOnlyRow}>
                <span>Input checks</span>
                <span className={styles.readOnlyValue} data-testid="cs-check-input">
                  {config.contentSafety.checkInput ? 'On' : 'Off'}
                </span>
              </div>
              <div className={styles.readOnlyRow}>
                <span>Output checks</span>
                <span className={styles.readOnlyValue} data-testid="cs-check-output">
                  {config.contentSafety.checkOutput ? 'On' : 'Off'}
                </span>
              </div>
              <div className={styles.readOnlyRow}>
                <span>Retrieved knowledge checks</span>
                <span className={styles.readOnlyValue} data-testid="cs-check-retrieved-knowledge">
                  {config.contentSafety.checkRetrievedKnowledge ? 'On' : 'Off'}
                </span>
              </div>
              <div className={styles.readOnlyRow}>
                <span>Tool-result checks</span>
                <span className={styles.readOnlyValue} data-testid="cs-check-tool-results">
                  {config.contentSafety.checkToolResults ? 'On' : 'Off'}
                </span>
              </div>
              <div className={styles.readOnlyRow}>
                <span>Prompt shields</span>
                <span className={styles.readOnlyValue} data-testid="cs-prompt-shields">
                  {config.contentSafety.promptShieldsEnabled ? 'On' : 'Off'}
                </span>
              </div>
            </div>
          )}
        </div>
      )}

      <div className={styles.section} data-testid="user-configurable-settings">
        <span className={styles.sectionTitle}>Settings you control</span>
        <span className={styles.sectionSubtitle}>
          These toggles change guardrail behaviour for this deployment.
        </span>

        <div className={styles.layerCallout} data-testid="injection-defense-explainer">
          <span className={styles.layerCalloutTitle}>Two layers block prompt injection</span>
          <div className={styles.layerRow}>
            <span className={styles.layerRowHead}>
              <span className={styles.layerRowName}>Pattern detection (this setting)</span>
              <span className={styles.readOnlyValue} data-testid="explainer-pattern-label">PATTERN</span>
            </span>
            <span>
              Matches known injection phrasings, such as "ignore previous instructions". You control it with the toggle below. In the audit trail these blocks are labelled PATTERN.
            </span>
          </div>
          <div className={styles.layerRow}>
            <span className={styles.layerRowHead}>
              <span className={styles.layerRowName}>Prompt Shields (managed by the deployment)</span>
              <span className={styles.readOnlyValue} data-testid="explainer-model-label">MODEL · PROMPT-SHIELD SAFETY</span>
            </span>
            <span>
              An AI model that spots injection attempts. You cannot turn it off on this page. In the audit trail these blocks are labelled MODEL · PROMPT-SHIELD SAFETY.
            </span>
          </div>
          <span className={styles.layerNoteStrong} data-testid="pattern-off-still-shielded-note">
            Turning pattern detection off does not turn off Prompt Shields. A request can still be blocked by that layer.
          </span>
        </div>

        <div className={styles.toggleRow}>
          <div className={styles.toggleLabel}>
            <Text weight="semibold">🚫 Pattern-based jailbreak detection</Text>
            <span className={styles.toggleDescription}>Blocks messages that contain known injection phrasings. This is only the pattern layer, not the whole injection defence.</span>
          </div>
          <Switch
            checked={config.jailbreakDetectionEnabled}
            onChange={(_e, data) => setConfig(prev => prev ? { ...prev, jailbreakDetectionEnabled: data.checked } : prev)}
            aria-label="Toggle jailbreak detection"
          />
        </div>

        <div className={styles.toggleRow}>
          <div className={styles.toggleLabel}>
            <Text weight="semibold">🔐 PII Detection</Text>
            <span className={styles.toggleDescription}>Automatically redact personal identifiable information</span>
          </div>
          <Switch
            checked={config.piiDetectionEnabled}
            onChange={(_e, data) => setConfig(prev => prev ? { ...prev, piiDetectionEnabled: data.checked } : prev)}
            aria-label="Toggle PII detection"
          />
        </div>

        <div className={styles.toggleRow}>
          <div className={styles.toggleLabel}>
            <Text weight="semibold">🧹 Auto-redact PII</Text>
            <span className={styles.toggleDescription}>Redact detected PII in responses instead of leaving it unchanged</span>
          </div>
          <Switch
            checked={config.autoRedactPii}
            onChange={(_e, data) => setConfig(prev => prev ? { ...prev, autoRedactPii: data.checked } : prev)}
            aria-label="Toggle auto-redact PII"
          />
        </div>
      </div>

      <div className={styles.section}>
        <span className={styles.sectionTitle}>Input Limits</span>
        <Text size={200} style={{ color: 'var(--color-text-muted)' }}>
          Requests longer than this value are rejected by the guardrails middleware.
        </Text>
        <input
          type="number"
          min={1}
          className={styles.textarea}
          value={config.maxInputLength}
          onChange={e => setConfig(prev => prev ? { ...prev, maxInputLength: Number(e.target.value) } : prev)}
          aria-label="Maximum input length"
        />
      </div>

      <div className={styles.section}>
        <span className={styles.sectionTitle}>Pattern Catalog</span>
        <Text size={200} style={{ color: 'var(--color-text-muted)' }}>
          Custom blocked patterns are not runtime-configurable. The API exposes the active built-in pattern families as read-only metadata.
        </Text>
        <Text size={200}>PII: {config.piiPatterns.join(', ') || 'None'}</Text>
        <Text size={200}>Jailbreak: {config.jailbreakPatterns.join(', ') || 'None'}</Text>
      </div>

      {error && (
        <div className={styles.statusMessage} style={{ backgroundColor: 'rgba(239, 68, 68, 0.1)', color: '#ef4444', border: '1px solid rgba(239, 68, 68, 0.3)' }}>
          ⚠️ {error}
        </div>
      )}
      {success && (
        <div className={styles.statusMessage} style={{ backgroundColor: 'rgba(34, 197, 94, 0.1)', color: '#22c55e', border: '1px solid rgba(34, 197, 94, 0.3)' }}>
          ✅ Configuration saved successfully
        </div>
      )}

      <div className={styles.actions}>
        <Button appearance="subtle" onClick={handleReset} disabled={saving}>
          Reset to Defaults
        </Button>
        {/*
          The Security page's amber accent (#f59e0b) measures 2.15:1 under white
          button text, well below the 4.5:1 WCAG AA minimum (issue #272). Amber-700
          keeps the page identity and measures 5.02:1, so the accent survives and
          the text becomes legible.
        */}
        <Button
          appearance="primary"
          onClick={handleSave}
          disabled={saving}
          data-testid="guardrails-save-button"
          style={{ backgroundColor: '#b45309', borderColor: '#b45309' }}
        >
          {saving ? <Spinner size="tiny" /> : 'Save Configuration'}
        </Button>
      </div>
    </div>
  );
}
