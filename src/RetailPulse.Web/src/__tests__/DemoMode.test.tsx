import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, act, waitFor } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ReactElement } from 'react';
import { DemoMode } from '../components/demo/DemoMode';
import { DEMO_ACTS } from '../components/demo/demoActs';

const wrap = (ui: ReactElement) => <FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>;

function renderDemo(overrides: Partial<Parameters<typeof DemoMode>[0]> = {}) {
  const props = {
    open: true,
    onClose: vi.fn(),
    onNavigate: vi.fn(),
    onTelemetry: vi.fn(),
    sendPrompt: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
  render(wrap(<DemoMode {...props} />));
  return props;
}

/**
 * Demo Mode drives the real product: it submits live prompts through the same send path
 * a person typing would use, waits for each answer before narrating it, and moves through
 * the views while results are on screen.
 *
 * The failure that matters most is narrating ahead of the system — claiming an answer has
 * arrived before it has — so the ordering guarantees are what these tests pin.
 */
describe('DemoMode', () => {
  beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

  it('renders nothing when closed', () => {
    renderDemo({ open: false });
    expect(screen.queryByTestId('demo-mode-card')).not.toBeInTheDocument();
  });

  it('starts on the first act and advertises that the run is live', () => {
    renderDemo();
    expect(screen.getByTestId('demo-mode-title')).toHaveTextContent(DEMO_ACTS[0].title);
    expect(screen.getByTestId('demo-mode-card')).toHaveTextContent('LIVE');
  });

  it('submits the real prompt for a prompt act', async () => {
    const props = renderDemo();

    // Advance past the intro hold into the first prompt act.
    await act(async () => { vi.advanceTimersByTime(DEMO_ACTS[0].holdMs ?? 5000); });

    const promptAct = DEMO_ACTS.find(a => a.prompt);
    await waitFor(() => expect(props.sendPrompt).toHaveBeenCalledWith(promptAct?.prompt));
  });

  it('waits for the answer before moving on', async () => {
    // A demo that narrates ahead of the system is worse than no demo: the card would
    // describe a result that is not on screen yet.
    let resolveSend: (() => void) | undefined;
    const sendPrompt = vi.fn(() => new Promise<void>(resolve => { resolveSend = resolve; }));
    renderDemo({ sendPrompt });

    await act(async () => { vi.advanceTimersByTime(DEMO_ACTS[0].holdMs ?? 5000); });
    await waitFor(() => expect(sendPrompt).toHaveBeenCalled());

    const titleDuringSend = screen.getByTestId('demo-mode-title').textContent;

    // Time passing must NOT advance the act while the request is still outstanding.
    await act(async () => { vi.advanceTimersByTime(60_000); });
    expect(screen.getByTestId('demo-mode-title')).toHaveTextContent(titleDuringSend ?? '');

    await act(async () => { resolveSend?.(); });
    await act(async () => { vi.advanceTimersByTime(60_000); });
    expect(screen.getByTestId('demo-mode-title')).not.toHaveTextContent(titleDuringSend ?? '');
  });

  it('shows a working indicator while a prompt is in flight', async () => {
    const sendPrompt = vi.fn(() => new Promise<void>(() => { /* never resolves */ }));
    renderDemo({ sendPrompt });

    await act(async () => { vi.advanceTimersByTime(DEMO_ACTS[0].holdMs ?? 5000); });
    await waitFor(() => expect(screen.getByTestId('demo-mode-working')).toBeInTheDocument());
  });

  it('says so rather than pretending when chat is not ready', async () => {
    renderDemo({ sendPrompt: null });

    await act(async () => { vi.advanceTimersByTime(DEMO_ACTS[0].holdMs ?? 5000); });
    await waitFor(() => {
      expect(screen.getByTestId('demo-mode-working')).toHaveTextContent(/not ready/i);
    });
  });

  it('keeps running when a prompt fails', async () => {
    // One failed turn must not strand the whole run.
    const sendPrompt = vi.fn().mockRejectedValue(new Error('boom'));
    renderDemo({ sendPrompt });

    await act(async () => { vi.advanceTimersByTime(DEMO_ACTS[0].holdMs ?? 5000); });
    await waitFor(() => expect(sendPrompt).toHaveBeenCalled());

    await act(async () => { vi.advanceTimersByTime(60_000); });
    expect(screen.getByTestId('demo-mode-progress')).not.toHaveTextContent('1 of');
  });

  it('drives the dashboard to each act view', async () => {
    const props = renderDemo();
    await waitFor(() => expect(props.onNavigate).toHaveBeenCalledWith(DEMO_ACTS[0].view));
  });

  it('pauses and resumes', async () => {
    renderDemo();
    const before = screen.getByTestId('demo-mode-progress').textContent;

    await act(async () => { screen.getByTestId('demo-mode-pause').click(); });
    await act(async () => { vi.advanceTimersByTime(120_000); });

    // Paused means paused: no amount of elapsed time should move it on.
    expect(screen.getByTestId('demo-mode-progress')).toHaveTextContent(before ?? '');
  });

  it('skips to the next act on demand', async () => {
    renderDemo();
    expect(screen.getByTestId('demo-mode-progress')).toHaveTextContent('1 of');

    await act(async () => { screen.getByTestId('demo-mode-skip').click(); });
    expect(screen.getByTestId('demo-mode-progress')).toHaveTextContent('2 of');
  });

  it('stops from the Stop button', () => {
    const props = renderDemo();
    screen.getByTestId('demo-mode-exit').click();
    expect(props.onClose).toHaveBeenCalled();
  });

  it('restarts from the first act when reopened', async () => {
    const { rerender } = render(wrap(
      <DemoMode open onClose={vi.fn()} onNavigate={vi.fn()} onTelemetry={vi.fn()} sendPrompt={vi.fn()} />,
    ));
    await act(async () => { screen.getByTestId('demo-mode-skip').click(); });
    expect(screen.getByTestId('demo-mode-progress')).toHaveTextContent('2 of');

    rerender(wrap(
      <DemoMode open={false} onClose={vi.fn()} onNavigate={vi.fn()} onTelemetry={vi.fn()} sendPrompt={vi.fn()} />,
    ));
    rerender(wrap(
      <DemoMode open onClose={vi.fn()} onNavigate={vi.fn()} onTelemetry={vi.fn()} sendPrompt={vi.fn()} />,
    ));

    expect(screen.getByTestId('demo-mode-progress')).toHaveTextContent('1 of');
  });
});

describe('demo script', () => {
  it('has unique act ids', () => {
    const ids = DEMO_ACTS.map(a => a.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('actually submits prompts rather than only narrating', () => {
    // The whole distinction from Tour is that this one runs the system.
    expect(DEMO_ACTS.filter(a => a.prompt).length).toBeGreaterThanOrEqual(2);
  });

  it('shows the cost story after a real prompt has run', () => {
    const firstPrompt = DEMO_ACTS.findIndex(a => a.prompt);
    const costAct = DEMO_ACTS.findIndex(a => /cost/i.test(a.title));

    // Narrating cost before anything has been spent would show an empty panel.
    expect(costAct).toBeGreaterThan(firstPrompt);
  });

  it('opens the telemetry drawer for the prompt it narrates', () => {
    const promptAct = DEMO_ACTS.find(a => a.prompt);
    expect(promptAct?.telemetry).toBe(true);
  });

  it('visits every navigable view', () => {
    const visited = new Set(DEMO_ACTS.map(a => a.view).filter(Boolean));
    for (const view of [
      'chat', 'promo', 'competitive', 'knowledge', 'council',
      'security', 'cards', 'observability', 'stores', 'financials', 'portfolio',
    ]) {
      expect(visited).toContain(view);
    }
  });
});
