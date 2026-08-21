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

const mockStats: GuardrailsStats = {
  totalBlocked: 42,
  jailbreakAttempts: 15,
  piiDetections: 20,
  accessDenials: 7,
  contentSafetyBlocks: 3,
  contentSafetyFlags: 1,
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
  { id: 'l1', timestamp: '2026-05-13T14:00:00Z', requestPreview: 'log-preview-1', detectionType: 'jailbreak', reason: '', actionTaken: 'Blocked' },
  { id: 'l2', timestamp: '2026-05-13T13:30:00Z', requestPreview: 'log-preview-2', detectionType: 'content-safety-hate', reason: '', actionTaken: 'Blocked', category: 'Hate', severity: 4, decision: 'Blocked' },
  { id: 'l3', timestamp: '2026-05-13T13:15:00Z', requestPreview: 'log-preview-3', detectionType: 'content-safety-violence', reason: '', actionTaken: 'Blocked', category: 'Violence', severity: 6, decision: 'Blocked' },
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
    expect(await screen.findByText('42')).toBeInTheDocument();
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
    expect(modelCard).toHaveTextContent('4'); // 3+1
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
});
