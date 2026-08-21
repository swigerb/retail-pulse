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
    borderColor: tokens.colorBrandStroke1,
    color: tokens.colorBrandForeground1,
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  entry: {
    display: 'grid',
    gridTemplateColumns: '140px 1fr 100px 120px',
    gap: '12px',
    alignItems: 'center',
    padding: '10px 14px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    fontSize: '13px',
    '@media (max-width: 640px)': {
      gridTemplateColumns: '1fr',
      gap: '4px',
    },
  },
  timestamp: {
    color: tokens.colorNeutralForeground3,
    fontSize: '12px',
    fontFamily: "'Courier New', monospace",
  },
  preview: {
    color: tokens.colorNeutralForeground1,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  typeBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    fontSize: '11px',
    fontWeight: '600',
    textTransform: 'uppercase',
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
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
  pii: '🔐',
  access: '🔒',
  'content-safety-hate': '⚠️',
  'content-safety-sexual': '⚠️',
  'content-safety-violence': '⚠️',
  'content-safety-selfharm': '⚠️',
  'content-safety-prompt-shield': '🛡️',
  'content-safety-indirect-injection': '🎯',
  'content-safety-unavailable': '⏱️',
};

/** Plain-language labels used in the filter chip row. */
const TYPE_LABELS: Record<GuardrailDetectionType, string> = {
  jailbreak: 'Jailbreak',
  pii: 'PII',
  access: 'Access',
  'content-safety-hate': 'Hate',
  'content-safety-sexual': 'Sexual',
  'content-safety-violence': 'Violence',
  'content-safety-selfharm': 'Self-harm',
  'content-safety-prompt-shield': 'Prompt shield',
  'content-safety-indirect-injection': 'Indirect injection',
  'content-safety-unavailable': 'Unavailable',
};

/** Bar-chart accents. Uses semantic Fluent tokens so they follow tenant theme. */
function familyStrokes() {
  return {
    pattern: tokens.colorBrandBackground,
    model: tokens.colorPaletteRedBackground3,
  };
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
            const categoryLabel = describeCategory(req.category, req.detectionType);
            const severityLabel = describeSeverity(req.severity);
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
                <span className={styles.preview} title={req.requestPreview}>
                  {req.requestPreview}
                </span>
                <span
                  className={styles.typeBadge}
                  data-testid="log-entry-type"
                  data-safety-family={family}
                >
                  {TYPE_ICONS[req.detectionType] ?? '⚠️'}{' '}
                  {family === 'model' ? 'Model' : family === 'pattern' ? 'Pattern' : 'Other'}
                  {' · '}
                  {categoryLabel ?? TYPE_LABELS[req.detectionType] ?? req.detectionType}
                  {severityLabel ? ` · ${severityLabel}` : ''}
                </span>
                <span className={styles.timestamp}>{req.actionTaken}</span>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
