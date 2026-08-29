import { useState, useEffect, useMemo, useCallback } from 'react';
import { makeStyles, Card, Text, Button, Spinner, tokens } from '@fluentui/react-components';
import type { GuardrailsStats, GuardrailDetectionType, BlockedRequest, ContentSafetyConfigData } from '../../types';
import { fetchGuardrailsStats, fetchGuardrailsLog, fetchGuardrailsConfig } from '../../services/guardrailsApi';
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid } from 'recharts';
import {
  aggregateByCategory,
  aggregateByFamily,
  aggregateBySeverity,
  classifyBlockFamily,
  describeCategory,
  describeSeverity,
} from '../../utils/safetyDisplay';
import { ContentSafetyStatusBadge } from './ContentSafetyStatusBadge';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    padding: '24px',
    height: '100%',
    overflowY: 'auto',
  },
  title: {
    fontSize: '22px',
    fontWeight: '700',
    color: tokens.colorNeutralForeground1,
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  statsGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
    gap: '12px',
  },
  statCard: {
    padding: '16px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  statValue: {
    fontSize: '28px',
    fontWeight: '700',
    fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
    color: tokens.colorNeutralForeground1,
  },
  statLabel: {
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  filterRow: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
  },
  filterChip: {
    padding: '6px 14px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '12px',
    fontWeight: '500',
    cursor: 'pointer',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground2,
    transition: 'all 0.2s ease',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground3,
    },
    ':focus-visible': {
      outline: `2px solid ${tokens.colorStrokeFocus2}`,
      outlineOffset: '2px',
    },
  },
  filterChipActive: {
    backgroundColor: tokens.colorBrandBackground2,
    borderTopColor: tokens.colorBrandStroke1,
    borderRightColor: tokens.colorBrandStroke1,
    borderBottomColor: tokens.colorBrandStroke1,
    borderLeftColor: tokens.colorBrandStroke1,
    color: tokens.colorBrandForeground1,
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  entry: {
    display: 'grid',
    gridTemplateColumns: 'max-content minmax(220px, 1fr) minmax(220px, 320px) max-content',
    gap: '12px',
    alignItems: 'center',
    padding: '10px 14px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    fontSize: '13px',
    '@media (max-width: 900px)': {
      gridTemplateColumns: '1fr',
      gap: '8px',
    },
  },
  timestamp: {
    color: tokens.colorNeutralForeground3,
    fontSize: '12px',
    fontFamily: "'Courier New', monospace",
    whiteSpace: 'nowrap',
  },
  entrySummary: {
    display: 'flex',
    flexDirection: 'column',
    gap: '3px',
    minWidth: 0,
  },
  entryPrimary: {
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  entryDetail: {
    color: tokens.colorNeutralForeground2,
    fontSize: '12px',
    lineHeight: '1.4',
  },
  typeBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    justifySelf: 'end',
    maxWidth: '100%',
    minWidth: 0,
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '11px',
    fontWeight: '600',
    textTransform: 'uppercase',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 900px)': {
      justifySelf: 'start',
    },
  },
  typeBadgeText: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  actionPill: {
    justifySelf: 'end',
    color: tokens.colorNeutralForeground3,
    fontSize: '12px',
    whiteSpace: 'nowrap',
    '@media (max-width: 900px)': {
      justifySelf: 'start',
    },
  },
  chartSection: {
    borderRadius: tokens.borderRadiusLarge,
    padding: '16px',
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  chartTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: tokens.colorNeutralForeground1,
    marginBottom: '12px',
  },
  emptyState: {
    textAlign: 'center',
    padding: '40px 20px',
    color: tokens.colorNeutralForeground3,
    fontSize: '14px',
  },
});

const TYPE_ICONS: Record<GuardrailDetectionType, string> = {
  jailbreak: '🚫',
  injection: '💉',
  pii: '🔐',
  access: '🔒',
  'content-safety-hate': '⚠️',
  'content-safety-sexual': '⚠️',
  'content-safety-violence': '⚠️',
  'content-safety-selfharm': '⚠️',
  'content-safety-prompt-shield': '🛡️',
  'content-safety-indirect-injection': '🎯',
  'content-safety-unavailable': '⏱️',
  'content-safety-block': '⚠️',
  'agent-definition-structural': '⚙️',
  'agent-definition-policy': '⚙️',
  'agent-definition-jailbreak': '🚫',
  'agent-definition-content-safety': '⚠️',
  'agent-definition-privileged-grant': '🔒',
  'agent-definition-content-safety-unavailable': '⏱️',
};

/** Plain-language labels used in the filter chip row. */
const TYPE_LABELS: Record<GuardrailDetectionType, string> = {
  jailbreak: 'Jailbreak',
  injection: 'Injection payload',
  pii: 'PII',
  access: 'Access',
  'content-safety-hate': 'Hate',
  'content-safety-sexual': 'Sexual',
  'content-safety-violence': 'Violence',
  'content-safety-selfharm': 'Self-harm',
  'content-safety-prompt-shield': 'Prompt shield',
  'content-safety-indirect-injection': 'Indirect injection',
  'content-safety-unavailable': 'Unavailable',
  'content-safety-block': 'Content Safety',
  'agent-definition-structural': 'Definition structure',
  'agent-definition-policy': 'Definition policy',
  'agent-definition-jailbreak': 'Definition jailbreak',
  'agent-definition-content-safety': 'Definition safety',
  'agent-definition-privileged-grant': 'Privileged grant',
  'agent-definition-content-safety-unavailable': 'Definition safety unavailable',
};

/** Bar-chart accents. Uses semantic Fluent tokens so they follow tenant theme. */
function familyStrokes() {
  return {
    pattern: tokens.colorBrandBackground,
    model: tokens.colorPaletteRedBackground3,
  };
}

type AuditStage = 'input' | 'output' | 'tool-result' | 'retrieved-knowledge' | 'agent-definition' | 'unknown';

const STAGE_LABELS: Record<AuditStage, string> = {
  input: 'Input',
  output: 'Output',
  'tool-result': 'Tool result',
  'retrieved-knowledge': 'Retrieved knowledge',
  'agent-definition': 'Agent definition',
  unknown: 'Content',
};

function normaliseStage(entry: BlockedRequest): AuditStage {
  const stage = entry.stage?.trim().toLowerCase();
  if (stage === 'input') return 'input';
  if (stage === 'output') return 'output';
  if (stage === 'toolresult' || stage === 'tool-result' || stage === 'tool result') return 'tool-result';
  if (stage === 'retrievedknowledge' || stage === 'retrieved-knowledge' || stage === 'retrieved knowledge') return 'retrieved-knowledge';
  if (stage === 'agentdefinition' || stage === 'agent-definition' || stage === 'agent definition') return 'agent-definition';

  // Every API log site sets Stage. This only catches rows written by an older
  // build still sitting in the in-memory ring buffer, and keys off the
  // detection type rather than the preview prose, which is not a contract.
  if (entry.detectionType.startsWith('agent-definition-')) return 'agent-definition';
  return 'unknown';
}

/**
 * The row's headline. `subject` is the API's structured name for the thing that
 * was checked; when it is absent the preview is itself the evidence and is
 * shown verbatim. Nothing here parses the preview.
 */
function summarizeAuditEvent(entry: BlockedRequest, stage: AuditStage): string {
  if (entry.subject?.trim()) return entry.subject.trim();
  if (entry.requestPreview.trim().length > 0) return entry.requestPreview;
  return `${STAGE_LABELS[stage]} guardrail event`;
}

function describeSystemAction(entry: BlockedRequest, stage: AuditStage): string {
  const action = entry.actionTaken.toLowerCase();
  if (action === 'failopen-passed') return 'allowed it because fail-open policy is active';
  if (action === 'failclosed-blocked') return 'blocked it because fail-closed policy is active';
  if (action === 'dropped') return 'dropped the retrieved knowledge before it reached the model';
  if (action === 'flagged') return 'allowed it and recorded a flag for review';
  if (action === 'redacted') return 'redacted sensitive values before continuing';
  if (action === 'quarantined') return 'quarantined the affected agent definition';
  if (action === 'blocked') {
    if (stage === 'output') return 'withheld the model output';
    if (stage === 'tool-result') return 'withheld the tool result from the model';
    if (stage === 'retrieved-knowledge') return 'withheld the retrieved knowledge';
    if (stage === 'agent-definition') return 'blocked the affected agent definition';
    return 'blocked the request';
  }
  return `recorded action ${entry.actionTaken}`;
}

function formatActionLabel(entry: BlockedRequest, stage: AuditStage): string {
  const action = entry.actionTaken.toLowerCase();
  if (action === 'failopen-passed') return 'Allowed through';
  if (action === 'failclosed-blocked') return 'Fail-closed block';
  if (action === 'flagged') return 'Flagged';
  if (action === 'dropped') return 'Dropped';
  if (action === 'redacted') return 'Redacted';
  if (action === 'quarantined') return 'Quarantined';
  if (action === 'blocked') {
    if (stage === 'output') return 'Output withheld';
    if (stage === 'tool-result') return 'Tool result withheld';
    if (stage === 'retrieved-knowledge') return 'Knowledge withheld';
    return 'Blocked';
  }
  return entry.actionTaken;
}

/**
 * Renders the audit row's meaning. The CAUSE clause is authored once, on the
 * server, by GuardrailAuditFields: only that layer holds the evaluation, the
 * configured threshold, and the stage together. It is rendered verbatim here.
 * The CONSEQUENCE clause is authored once, here, by describeSystemAction,
 * because it is presentation and the action pill needs it standalone.
 *
 * Do not reintroduce a client-side reason ladder. The previous one silently
 * shadowed the server string on every Content Safety row, so the two wordings
 * could drift with nothing to catch it.
 */
function explainAuditDecision(entry: BlockedRequest, stage: AuditStage): string {
  const cause = entry.reason?.trim()
    || `${STAGE_LABELS[stage]} triggered a configured guardrail.`;
  const consequence = `The system ${describeSystemAction(entry, stage)}.`;

  return entry.actionTaken.toLowerCase() === 'failopen-passed'
    ? `${cause} ${consequence} Review Content Safety availability.`
    : `${cause} ${consequence}`;
}

function buildBadgeText(entry: BlockedRequest): string {
  const family = classifyBlockFamily(entry.detectionType);
  const categoryLabel = describeCategory(entry.category, entry.detectionType)
    ?? TYPE_LABELS[entry.detectionType]
    ?? 'Guardrail';
  const severityLabel = describeSeverity(entry.severity);
  const familyLabel = family === 'model' ? 'Model' : family === 'pattern' ? 'Pattern' : 'Other';
  return [familyLabel, categoryLabel, severityLabel].filter(Boolean).join(' · ');
}

export function GuardrailsDashboard() {
  const styles = useStyles();
  const [stats, setStats] = useState<GuardrailsStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<GuardrailDetectionType | 'all'>('all');
  const [logEntries, setLogEntries] = useState<BlockedRequest[]>([]);
  const [contentSafetyEnabled, setContentSafetyEnabled] = useState<boolean | null>(null);
  const [contentSafety, setContentSafety] = useState<ContentSafetyConfigData | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([
      fetchGuardrailsStats(),
      fetchGuardrailsLog(50).catch(() => [] as BlockedRequest[]),
      fetchGuardrailsConfig().catch(() => null),
    ])
      .then(([statsData, log, config]) => {
        if (cancelled) return;
        setStats(statsData);
        // When the log endpoint returns entries, use those over any inline
        // `recentBlocked` array so category/severity/decision are populated.
        setLogEntries(log.length > 0 ? log : statsData.recentBlocked ?? []);
        setContentSafety(config?.contentSafety ?? null);
        setContentSafetyEnabled(config?.contentSafety?.enabled ?? null);
        setError(null);
      })
      .catch(err => { if (!cancelled) setError(err instanceof Error ? err.message : 'Failed to load'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const handleRefresh = useCallback(() => {
    setLoading(true);
    Promise.all([
      fetchGuardrailsStats(),
      fetchGuardrailsLog(50).catch(() => [] as BlockedRequest[]),
      fetchGuardrailsConfig().catch(() => null),
    ])
      .then(([statsData, log, config]) => {
        setStats(statsData);
        setLogEntries(log.length > 0 ? log : statsData.recentBlocked ?? []);
        setContentSafety(config?.contentSafety ?? null);
        setContentSafetyEnabled(config?.contentSafety?.enabled ?? null);
        setError(null);
      })
      .catch(err => setError(err instanceof Error ? err.message : 'Failed to load'))
      .finally(() => setLoading(false));
  }, []);

  const familyAggregate = useMemo(() => aggregateByFamily(logEntries), [logEntries]);
  const categoryAggregate = useMemo(() => aggregateByCategory(logEntries), [logEntries]);
  const severityAggregate = useMemo(() => aggregateBySeverity(logEntries), [logEntries]);

  const familyChartData = useMemo(
    () => [
      { label: 'Pattern', count: familyAggregate.pattern },
      { label: 'Model', count: familyAggregate.model },
    ],
    [familyAggregate.pattern, familyAggregate.model],
  );

  const categoryChartData = useMemo(
    () => categoryAggregate.map(a => ({ label: a.label, count: a.count })),
    [categoryAggregate],
  );

  const severityChartData = useMemo(
    () => severityAggregate.map(a => ({ label: a.label, count: a.count })),
    [severityAggregate],
  );

  const filteredRequests = useMemo(() => {
    if (!stats) return [];
    const list = logEntries.slice(0, 50);
    if (filter === 'all') return list;
    return list.filter(r => r.detectionType === filter);
  }, [stats, logEntries, filter]);

  if (loading) {
    return (
      <div className={styles.container}>
        <div className={styles.emptyState}><Spinner size="medium" label="Loading guardrails data..." /></div>
      </div>
    );
  }

  if (error || !stats) {
    return (
      <div className={styles.container}>
        <div className={styles.emptyState}>
          <Text>⚠️ {error || 'No guardrails data available'}</Text>
          <br />
          <Button appearance="subtle" onClick={handleRefresh} style={{ marginTop: '12px' }}>Retry</Button>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.container} data-testid="guardrails-dashboard">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: '12px' }}>
        <span className={styles.title}>🛡️ Guardrails Security</span>
        <div style={{ display: 'flex', gap: '10px', alignItems: 'center' }}>
          {contentSafetyEnabled !== null && (
            <ContentSafetyStatusBadge
              enabled={contentSafetyEnabled}
              failPolicy={contentSafety?.failPolicy}
              detail={
                contentSafetyEnabled
                  ? undefined
                  : 'Pattern guardrails remain active.'
              }
            />
          )}
          <Button appearance="subtle" onClick={handleRefresh}>Refresh</Button>
        </div>
      </div>

      {/* Stats Cards */}
      <div className={styles.statsGrid}>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue}>{stats.totalBlocked}</span>
          <span className={styles.statLabel}>Total Blocked</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle" data-testid="stat-pattern-total">
          <span className={styles.statValue}>
            {stats.jailbreakAttempts + stats.piiDetections + stats.accessDenials}
          </span>
          <span className={styles.statLabel}>Pattern-based Blocks</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle" data-testid="stat-model-total">
          <span className={styles.statValue}>
            {(stats.contentSafetyBlocks ?? 0) + (stats.contentSafetyFlags ?? 0)}
          </span>
          <span className={styles.statLabel}>Model-based Blocks</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue}>{stats.jailbreakAttempts}</span>
          <span className={styles.statLabel}>Jailbreak Attempts</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue}>{stats.piiDetections}</span>
          <span className={styles.statLabel}>PII Detections</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue}>{stats.accessDenials}</span>
          <span className={styles.statLabel}>Access Denials</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue}>{stats.contentSafetyBlocks ?? 0}</span>
          <span className={styles.statLabel}>Content Safety Blocks</span>
        </Card>
        <Card className={styles.statCard} appearance="subtle">
          <span className={styles.statValue}>{stats.contentSafetyFlags ?? 0}</span>
          <span className={styles.statLabel}>Content Safety Flags</span>
        </Card>
      </div>

      {/* Pattern vs Model breakdown */}
      <div className={styles.chartSection} data-testid="chart-family-split">
        <div className={styles.chartTitle}>Pattern-based vs Model-based blocks</div>
        <ResponsiveContainer width="100%" height={180}>
          <BarChart data={familyChartData}>
            <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
            <XAxis dataKey="label" tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }} />
            <YAxis allowDecimals={false} tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }} />
            <Tooltip />
            <Bar dataKey="count" fill={familyStrokes().pattern} radius={[4, 4, 0, 0]} />
          </BarChart>
        </ResponsiveContainer>
      </div>

      {/* Category distribution */}
      <div className={styles.chartSection} data-testid="chart-category-distribution">
        <div className={styles.chartTitle}>Model-based blocks by category</div>
        <ResponsiveContainer width="100%" height={180}>
          <BarChart data={categoryChartData}>
            <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
            <XAxis dataKey="label" tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }} />
            <YAxis allowDecimals={false} tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }} />
            <Tooltip />
            <Bar dataKey="count" fill={familyStrokes().model} radius={[4, 4, 0, 0]} />
          </BarChart>
        </ResponsiveContainer>
      </div>

      {/* Severity distribution */}
      <div className={styles.chartSection} data-testid="chart-severity-distribution">
        <div className={styles.chartTitle}>Model-based blocks by severity</div>
        <ResponsiveContainer width="100%" height={180}>
          <BarChart data={severityChartData}>
            <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
            <XAxis dataKey="label" tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }} />
            <YAxis allowDecimals={false} tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }} />
            <Tooltip />
            <Bar dataKey="count" fill={familyStrokes().model} radius={[4, 4, 0, 0]} />
          </BarChart>
        </ResponsiveContainer>
      </div>

      {/* Trend Chart */}
      {stats.blocksPerHour.length > 0 && (
        <div className={styles.chartSection}>
          <div className={styles.chartTitle}>Blocks Per Hour (Last 24h)</div>
          <ResponsiveContainer width="100%" height={200}>
            <BarChart data={stats.blocksPerHour}>
              <CartesianGrid strokeDasharray="3 3" stroke={tokens.colorNeutralStroke2} />
              <XAxis
                dataKey="hour"
                tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }}
                tickFormatter={(v: string) => {
                  const d = new Date(v);
                  return isNaN(d.getTime()) ? String(v) : `${d.getHours()}:00`;
                }}
              />
              <YAxis tick={{ fill: tokens.colorNeutralForeground2, fontSize: 11 }} allowDecimals={false} />
              <Tooltip
                contentStyle={{
                  backgroundColor: tokens.colorNeutralBackground2,
                  border: `1px solid ${tokens.colorNeutralStroke2}`,
                  borderRadius: '8px',
                }}
                labelStyle={{ color: tokens.colorNeutralForeground1 }}
              />
              <Bar dataKey="count" fill={tokens.colorBrandBackground} radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* Filter */}
      <div className={styles.filterRow}>
        {([
          'all', 'jailbreak', 'pii', 'access',
          'content-safety-hate', 'content-safety-sexual', 'content-safety-violence',
          'content-safety-selfharm', 'content-safety-prompt-shield',
          'content-safety-indirect-injection', 'content-safety-unavailable',
          'content-safety-block', 'agent-definition-content-safety-unavailable',
        ] as const).map(type => (
          <button
            key={type}
            className={`${styles.filterChip} ${filter === type ? styles.filterChipActive : ''}`}
            onClick={() => setFilter(type)}
            data-testid={`filter-chip-${type}`}
          >
            {type === 'all' ? '🔍 All' : `${TYPE_ICONS[type]} ${TYPE_LABELS[type]}`}
          </button>
        ))}
      </div>

      {/* Recent Blocked Requests */}
      <div className={styles.list}>
        {filteredRequests.length === 0 ? (
          <div className={styles.emptyState}>No blocked requests found for this filter.</div>
        ) : (
          filteredRequests.map((req: BlockedRequest) => {
            const family = classifyBlockFamily(req.detectionType);
            const stage = normaliseStage(req);
            const summary = summarizeAuditEvent(req, stage);
            const explanation = explainAuditDecision(req, stage);
            const badgeText = buildBadgeText(req);
            const actionLabel = formatActionLabel(req, stage);
            return (
              <div
                key={req.id}
                className={styles.entry}
                data-testid="guardrails-log-entry"
                data-safety-family={family}
              >
                <span className={styles.timestamp}>
                  {new Date(req.timestamp).toLocaleString()}
                </span>
                <span className={styles.entrySummary}>
                  <span className={styles.entryPrimary} title={summary}>
                    {summary}
                  </span>
                  <span className={styles.entryDetail}>
                    {explanation}
                  </span>
                </span>
                <span
                  className={styles.typeBadge}
                  data-testid="log-entry-type"
                  data-safety-family={family}
                  title={badgeText}
                >
                  {TYPE_ICONS[req.detectionType] ?? '⚠️'}{' '}
                  <span className={styles.typeBadgeText}>{badgeText}</span>
                </span>
                <span className={styles.actionPill} title={`System action: ${describeSystemAction(req, stage)}`}>
                  {actionLabel}
                </span>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
