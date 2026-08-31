import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { GuardrailsDashboard } from '../components/guardrails/GuardrailsDashboard';
import type { BlockedRequest, GuardrailsStats, GuardrailsConfigData } from '../types';

// Mock recharts to avoid rendering issues in test
vi.mock('recharts', () => ({
  BarChart: ({ children }: { children: React.ReactNode }) => <div data-testid="bar-chart">{children}</div>,
  Bar: () => <div />,
  XAxis: () => <div />,
  YAxis: () => <div />,
  Tooltip: () => <div />,
  ResponsiveContainer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  CartesianGrid: () => <div />,
}));

// NOTE: `totalBlocked` is deliberately distinct from
// `jailbreakAttempts + piiDetections + accessDenials` (the pattern-total
// card's derived value). If they collide, `screen.findByText(<total>)`
// matches two nodes — the Total Blocked card and the pattern-total card —
// and the "renders stats after successful fetch" assertion becomes
// ambiguous. The Total Blocked card renders no `data-testid` in production,
// so we scope by fixture value instead.
const mockStats: GuardrailsStats = {
  totalBlocked: 87,
  jailbreakAttempts: 15,
  piiDetections: 20,
  accessDenials: 7,
  contentSafetyBlocks: 3,
  contentSafetyFlags: 1,
  failOpenPasses: 9,
  recentBlocked: [
    { id: '1', timestamp: '2026-05-13T14:00:00Z', requestPreview: 'Ignore all previous instructions...', detectionType: 'jailbreak', reason: 'Jailbreak pattern', actionTaken: 'Blocked' },
    { id: '2', timestamp: '2026-05-13T13:30:00Z', requestPreview: 'My SSN is 123-45-6789', detectionType: 'pii', reason: 'PII detected', actionTaken: 'Redacted' },
    { id: '3', timestamp: '2026-05-13T13:00:00Z', requestPreview: 'Show me competitor financial data', detectionType: 'access', reason: 'Unauthorized', actionTaken: 'Blocked' },
  ],
  blocksPerHour: [
    { hour: '2026-05-13T12:00:00Z', count: 3 },
    { hour: '2026-05-13T13:00:00Z', count: 5 },
  ],
};

const mockLog: BlockedRequest[] = [
  { id: 'l1', timestamp: '2026-05-13T14:00:00Z', requestPreview: 'log-preview-1', detectionType: 'jailbreak', reason: 'Pattern matching found a known jailbreak phrase in the input.', actionTaken: 'Blocked', stage: 'Input' },
  { id: 'l2', timestamp: '2026-05-13T13:30:00Z', requestPreview: 'log-preview-2', detectionType: 'content-safety-hate', reason: 'Content Safety classified the input as Hate content at severity 4, which met threshold 4.', actionTaken: 'Blocked', category: 'Hate', severity: 4, decision: 'Blocked', stage: 'Input', threshold: 4 },
  { id: 'l3', timestamp: '2026-05-13T13:15:00Z', requestPreview: 'log-preview-3', detectionType: 'content-safety-violence', reason: 'Content Safety classified the input as Violence content at severity 6, which met threshold 4.', actionTaken: 'Blocked', category: 'Violence', severity: 6, decision: 'Blocked', stage: 'Input', threshold: 4 },
  {
    id: 'l4',
    timestamp: '2026-05-13T13:10:00Z',
    requestPreview: "Tool result from 'GetStorePerformance' blocked by Content Safety",
    detectionType: 'content-safety-selfharm',
    reason: 'Content Safety classified the tool result as SelfHarm content at severity 6, which met threshold 4.',
    actionTaken: 'blocked',
    category: 'SelfHarm',
    severity: 6,
    decision: 'Blocked',
    stage: 'ToolResult',
    threshold: 4,
    subject: "Tool result from 'GetStorePerformance'",
  },
  {
    id: 'l5',
    timestamp: '2026-05-13T13:05:00Z',
    requestPreview: 'agent=general field=SystemPrompt rule=safety.content-safety-unavailable',
    detectionType: 'agent-definition-content-safety-unavailable',
    reason: 'Content Safety was unreachable while checking SystemPrompt for agent general.',
    actionTaken: 'failopen-passed',
    decision: 'ServiceUnavailable',
    stage: 'AgentDefinition',
    subject: 'SystemPrompt on agent general',
  },
];

const mockConfig: { contentSafety: GuardrailsConfigData['contentSafety'] } = {
  contentSafety: {
    enabled: true,
    failPolicy: 'FailClosed',
    promptShieldsEnabled: true,
    checkInput: true,
    checkOutput: true,
    checkRetrievedKnowledge: true,
    checkToolResults: true,
    hateThreshold: 4,
    sexualThreshold: 4,
    violenceThreshold: 4,
    selfHarmThreshold: 4,
  },
};

/**
 * URL-aware fetch mock — dispatches to stats/log/config bodies so the
 * dashboard can hydrate all three concurrent requests without depending on
 * mock-call ordering.
 */
function installFetchMock(overrides?: {
  stats?: unknown;
  log?: unknown;
  config?: unknown;
  statsOk?: boolean;
  logOk?: boolean;
  configOk?: boolean;
}) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation((input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : (input as URL).toString();
    if (url.includes('/api/guardrails/stats')) {
      return Promise.resolve({
        ok: overrides?.statsOk ?? true,
        json: async () => overrides?.stats ?? mockStats,
      } as Response);
    }
    if (url.includes('/api/guardrails/log')) {
      return Promise.resolve({
        ok: overrides?.logOk ?? true,
        json: async () => overrides?.log ?? mockLog,
      } as Response);
    }
    if (url.includes('/api/guardrails/config')) {
      return Promise.resolve({
        ok: overrides?.configOk ?? true,
        json: async () => overrides?.config ?? mockConfig,
      } as Response);
    }
    return Promise.reject(new Error(`Unmocked fetch: ${url}`));
  });
}

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('GuardrailsDashboard', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('renders loading state initially', () => {
    // Mock fetch to never resolve
    vi.spyOn(globalThis, 'fetch').mockReturnValue(new Promise(() => {}));
    renderWithTheme(<GuardrailsDashboard />);
    expect(screen.getByText('Loading guardrails data...')).toBeInTheDocument();
  });

  it('renders stats after successful fetch', async () => {
    installFetchMock();

    renderWithTheme(<GuardrailsDashboard />);
    // Total Blocked: distinct from the pattern-total card (15+20+7 = 42) so
    // this findByText matches exactly one node.
    expect(await screen.findByText('87')).toBeInTheDocument();
    expect(screen.getByText('15')).toBeInTheDocument();
    expect(screen.getByText('20')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();
  });

  it('renders stat labels', async () => {
    installFetchMock();

    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText('Total Blocked')).toBeInTheDocument();
    expect(screen.getByText('Jailbreak Attempts')).toBeInTheDocument();
    expect(screen.getByText('PII Detections')).toBeInTheDocument();
    expect(screen.getByText('Access Denials')).toBeInTheDocument();
    expect(screen.getByText('Pattern-based Blocks')).toBeInTheDocument();
    expect(screen.getByText('Model-based Blocks')).toBeInTheDocument();
  });

  it('renders recent blocked requests', async () => {
    installFetchMock();

    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText('log-preview-1')).toBeInTheDocument();
    expect(screen.getByText('log-preview-2')).toBeInTheDocument();
  });

  it('renders error state on fetch failure', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new Error('Network error'));

    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText(/Network error/)).toBeInTheDocument();
  });

  it('renders filter chips', async () => {
    installFetchMock();

    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText('🔍 All')).toBeInTheDocument();
  });

  it('renders the title', async () => {
    installFetchMock();

    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText(/Guardrails Security/)).toBeInTheDocument();
  });

  it('renders trend chart section', async () => {
    installFetchMock();

    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText('Blocks Per Hour (Last 24h)')).toBeInTheDocument();
  });

  it('renders the pattern-vs-model split cards', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    const patternCard = await screen.findByTestId('stat-pattern-total');
    expect(patternCard).toHaveTextContent('42'); // 15+20+7
    const modelCard = screen.getByTestId('stat-model-total');
    // Model-based Blocks now counts blocks ONLY. It previously read
    // contentSafetyBlocks + contentSafetyFlags (3+1=4), which double-counted
    // the informational flag and broke reconciliation with Total Blocked, since
    // a flagged row is a non-blocking hit. Blocks only = 3.
    expect(modelCard).toHaveTextContent('3');
  });

  it('renders fail-open passes as a distinct counter separate from blocks', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    const failOpenCard = await screen.findByTestId('stat-failopen-total');
    // A request allowed through on service failure is the opposite of a block,
    // so it surfaces on its own card and never inflates Total Blocked.
    expect(failOpenCard).toHaveTextContent('9');
    expect(failOpenCard).toHaveTextContent('Fail-open Passes');
  });

  it('renders the category and severity distribution sections', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByTestId('chart-family-split')).toBeInTheDocument();
    expect(screen.getByTestId('chart-category-distribution')).toBeInTheDocument();
    expect(screen.getByTestId('chart-severity-distribution')).toBeInTheDocument();
  });

  it('shows the content-safety status badge as enabled when config says so', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    const badge = await screen.findByTestId('content-safety-status-badge');
    expect(badge).toHaveAttribute('data-safety-enabled', 'true');
  });

  it('shows the content-safety status badge as disabled and a pattern-guardrails note when config is off', async () => {
    installFetchMock({
      config: {
        contentSafety: { ...mockConfig.contentSafety, enabled: false },
      },
    });
    renderWithTheme(<GuardrailsDashboard />);
    const badge = await screen.findByTestId('content-safety-status-badge');
    expect(badge).toHaveAttribute('data-safety-enabled', 'false');
    expect(badge.textContent).toMatch(/disabled/i);
  });

  it('log entries carry a family attribute so pattern vs model is machine-inspectable', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    const entries = await screen.findAllByTestId('guardrails-log-entry');
    expect(entries.length).toBeGreaterThan(0);
    for (const el of entries) {
      expect(['pattern', 'model', 'unknown']).toContain(el.getAttribute('data-safety-family'));
    }
  });

  it('never leaks raw detection-type slugs or internal marker strings in rendered log entries', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    const dashboard = await screen.findByTestId('guardrails-dashboard');
    const rendered = dashboard.textContent ?? '';
    // No `content-safety-*` slug should appear in the rendered UI text.
    expect(rendered).not.toMatch(/content-safety-hate\b|content-safety-violence\b|content-safety-sexual\b|content-safety-selfharm\b|content-safety-prompt-shield\b|content-safety-indirect-injection\b|content-safety-unavailable\b/);
    expect(rendered).not.toMatch(/RULE_ID_|THRESHOLD_|SENSITIVE_PATTERN_/i);
  });

  it('renders the API-supplied subject and reason for a content-safety row', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText("Tool result from 'GetStorePerformance'")).toBeInTheDocument();
    expect(screen.getByText(/Content Safety classified the tool result as SelfHarm content at severity 6, which met threshold 4\./)).toBeInTheDocument();
    expect(screen.getByText(/The system withheld the tool result from the model\./)).toBeInTheDocument();
    expect(screen.getByText('Tool result withheld')).toBeInTheDocument();
  });

  it('renders fail-open passes as plain operator wording', async () => {
    installFetchMock();
    renderWithTheme(<GuardrailsDashboard />);
    const dashboard = await screen.findByTestId('guardrails-dashboard');
    expect(screen.getByText('SystemPrompt on agent general')).toBeInTheDocument();
    expect(screen.getByText(/Content Safety was unreachable while checking SystemPrompt for agent general\. The system allowed it because fail-open policy is active\. Review Content Safety availability\./)).toBeInTheDocument();
    expect(screen.getByText('Allowed through')).toBeInTheDocument();
    expect(dashboard.textContent ?? '').not.toContain('agent=general field=SystemPrompt rule=safety.content-safety-unavailable');
  });

  // The cause clause is authored once, on the server. If the dashboard ever
  // starts re-deriving it from category/severity/threshold again, this fails:
  // those fields say "Hate / 4 / 4" while the server reason says otherwise.
  it('renders the server reason verbatim rather than re-deriving one', async () => {
    installFetchMock({
      log: [{
        id: 'v1',
        timestamp: '2026-05-13T12:00:00Z',
        requestText: 'preview-verbatim',
        detectionType: 'content-safety-hate',
        reason: 'SERVER-AUTHORED CAUSE SENTENCE.',
        action: 'blocked',
        category: 'Hate',
        severity: 4,
        threshold: 4,
        decision: 'Blocked',
        stage: 'Input',
      }],
    });
    renderWithTheme(<GuardrailsDashboard />);
    expect(await screen.findByText('SERVER-AUTHORED CAUSE SENTENCE. The system blocked the request.')).toBeInTheDocument();
  });

  it('labels pattern-layer injection rows instead of falling back to Other', async () => {
    installFetchMock({
      log: [{
        id: 'v2',
        timestamp: '2026-05-13T12:00:00Z',
        requestText: "show me stores where 1=1' or 1=1--",
        detectionType: 'injection',
        reason: 'Pattern matching found a known SQL or script injection payload in the input.',
        action: 'blocked',
        stage: 'Input',
      }],
    });
    renderWithTheme(<GuardrailsDashboard />);
    const entry = await screen.findByTestId('guardrails-log-entry');
    expect(entry.getAttribute('data-safety-family')).toBe('pattern');
    expect(entry.textContent ?? '').toContain('Pattern · SQL or script injection');
  });
});
