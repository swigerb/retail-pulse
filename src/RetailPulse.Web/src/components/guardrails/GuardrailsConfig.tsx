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
  toggleRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
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
  },
  toggleDescription: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
  },
  textarea: {
    width: '100%',
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
    padding: '6px 0',
    fontSize: '13px',
    color: tokens.colorNeutralForeground2,
  },
  readOnlyValue: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
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
      await updateGuardrailsConfig(config);
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

      <div className={styles.section}>
        <span className={styles.sectionTitle}>Protection Toggles</span>

        <div className={styles.toggleRow}>
          <div className={styles.toggleLabel}>
            <Text weight="semibold">🚫 Jailbreak Detection</Text>
            <span className={styles.toggleDescription}>Block prompt injection and jailbreak attempts</span>
          </div>
          <Switch
            checked={config.jailbreakEnabled}
            onChange={(_e, data) => setConfig(prev => prev ? { ...prev, jailbreakEnabled: data.checked } : prev)}
            aria-label="Toggle jailbreak detection"
          />
        </div>

        <div className={styles.toggleRow}>
          <div className={styles.toggleLabel}>
            <Text weight="semibold">🔐 PII Detection</Text>
            <span className={styles.toggleDescription}>Automatically redact personal identifiable information</span>
          </div>
          <Switch
            checked={config.piiEnabled}
            onChange={(_e, data) => setConfig(prev => prev ? { ...prev, piiEnabled: data.checked } : prev)}
            aria-label="Toggle PII detection"
          />
        </div>

        <div className={styles.toggleRow}>
          <div className={styles.toggleLabel}>
            <Text weight="semibold">🔒 Access Control</Text>
            <span className={styles.toggleDescription}>Enforce role-based access restrictions on data queries</span>
          </div>
          <Switch
            checked={config.accessControlEnabled}
            onChange={(_e, data) => setConfig(prev => prev ? { ...prev, accessControlEnabled: data.checked } : prev)}
            aria-label="Toggle access control"
          />
        </div>
      </div>

      <div className={styles.section}>
        <span className={styles.sectionTitle}>Blocked Patterns</span>
        <Text size={200} style={{ color: 'var(--color-text-muted)' }}>
          One pattern per line. Regex supported.
        </Text>
        <textarea
          className={styles.textarea}
          value={config.blockedPatterns}
          onChange={e => setConfig(prev => prev ? { ...prev, blockedPatterns: e.target.value } : prev)}
          placeholder="Enter blocked patterns, one per line..."
          aria-label="Blocked patterns"
        />
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
        <Button
          appearance="primary"
          onClick={handleSave}
          disabled={saving}
          style={{ backgroundColor: '#f59e0b', borderColor: '#f59e0b' }}
        >
          {saving ? <Spinner size="tiny" /> : 'Save Configuration'}
        </Button>
      </div>
    </div>
  );
}
