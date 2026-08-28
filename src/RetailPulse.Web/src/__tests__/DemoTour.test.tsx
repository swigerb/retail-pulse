import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ReactElement } from 'react';
import { DemoTour } from '../components/demo/DemoTour';
import { DEMO_STEPS } from '../components/demo/demoSteps';

const wrap = (ui: ReactElement) => <FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>;

function renderTour(overrides: Partial<Parameters<typeof DemoTour>[0]> = {}) {
  const props = {
    open: true,
    onClose: vi.fn(),
    onNavigate: vi.fn(),
    onTelemetry: vi.fn(),
    ...overrides,
  };
  render(wrap(<DemoTour {...props} />));
  return props;
}

/**
 * Demo Mode is the first thing shown to an audience, so its failure modes matter more
 * than most: a step that advances on a stray click, narration that drifts out of sync
 * with the view behind it, or a card positioned off-screen all break the demo in front
 * of the person being demoed to.
 */
describe('DemoTour', () => {
  beforeEach(() => {
    // jsdom gives every element a zero box, which the component treats as "no target".
    // Provide a real one so spotlight positioning is exercised rather than skipped.
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({
      top: 100, left: 200, width: 300, height: 80, right: 500, bottom: 180, x: 200, y: 100,
      toJSON: () => ({}),
    } as DOMRect);
  });

  afterEach(() => vi.restoreAllMocks());

  it('renders nothing when closed', () => {
    renderTour({ open: false });
    expect(screen.queryByTestId('demo-tour-card')).not.toBeInTheDocument();
  });

  it('opens on the first step', () => {
    renderTour();
    expect(screen.getByTestId('demo-tour-title')).toHaveTextContent(DEMO_STEPS[0].title);
    expect(screen.getByTestId('demo-tour-progress')).toHaveTextContent(`1 of ${DEMO_STEPS.length}`);
  });

  it('advances and goes back', () => {
    renderTour();
    fireEvent.click(screen.getByTestId('demo-tour-next'));
    expect(screen.getByTestId('demo-tour-title')).toHaveTextContent(DEMO_STEPS[1].title);

    fireEvent.click(screen.getByTestId('demo-tour-back'));
    expect(screen.getByTestId('demo-tour-title')).toHaveTextContent(DEMO_STEPS[0].title);
  });

  it('disables Back on the first step so the tour cannot run off the start', () => {
    renderTour();
    expect(screen.getByTestId('demo-tour-back')).toBeDisabled();
  });

  it('drives the dashboard to the view each step describes', () => {
    const props = renderTour();

    // The narration must match what is on screen behind it.
    const firstWithView = DEMO_STEPS.find(s => s.view);
    expect(props.onNavigate).toHaveBeenCalledWith(firstWithView?.view);
  });

  it('opens the telemetry drawer only for the steps that describe it', () => {
    const props = renderTour();
    // Step 1 does not describe the drawer.
    expect(props.onTelemetry).toHaveBeenLastCalledWith(false);

    const drawerStepIndex = DEMO_STEPS.findIndex(s => s.telemetry);
    for (let i = 0; i < drawerStepIndex; i++) {
      fireEvent.click(screen.getByTestId('demo-tour-next'));
    }
    expect(props.onTelemetry).toHaveBeenLastCalledWith(true);
  });

  it('closes from the Exit button', () => {
    const props = renderTour();
    fireEvent.click(screen.getByTestId('demo-tour-exit'));
    expect(props.onClose).toHaveBeenCalled();
  });

  it('finishes on the last step instead of advancing past the end', () => {
    const props = renderTour();
    for (let i = 0; i < DEMO_STEPS.length - 1; i++) {
      fireEvent.click(screen.getByTestId('demo-tour-next'));
    }

    expect(screen.getByTestId('demo-tour-next')).toHaveTextContent('Finish');
    fireEvent.click(screen.getByTestId('demo-tour-next'));
    expect(props.onClose).toHaveBeenCalled();
  });

  it('does not advance when the backdrop is clicked', () => {
    const props = renderTour();
    fireEvent.click(screen.getByTestId('demo-tour-overlay'));

    // An accidental click mid-presentation should do nothing at all.
    expect(screen.getByTestId('demo-tour-progress')).toHaveTextContent('1 of');
    expect(props.onClose).not.toHaveBeenCalled();
  });

  it('supports keyboard navigation from a lectern', () => {
    const props = renderTour();

    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(screen.getByTestId('demo-tour-title')).toHaveTextContent(DEMO_STEPS[1].title);

    fireEvent.keyDown(window, { key: 'ArrowLeft' });
    expect(screen.getByTestId('demo-tour-title')).toHaveTextContent(DEMO_STEPS[0].title);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(props.onClose).toHaveBeenCalled();
  });

  it('restarts from the beginning when reopened', () => {
    const { rerender } = render(wrap(
      <DemoTour open onClose={vi.fn()} onNavigate={vi.fn()} onTelemetry={vi.fn()} />,
    ));
    fireEvent.click(screen.getByTestId('demo-tour-next'));
    expect(screen.getByTestId('demo-tour-progress')).toHaveTextContent('2 of');

    rerender(wrap(<DemoTour open={false} onClose={vi.fn()} onNavigate={vi.fn()} onTelemetry={vi.fn()} />));
    rerender(wrap(<DemoTour open onClose={vi.fn()} onNavigate={vi.fn()} onTelemetry={vi.fn()} />));

    // Pressing the button in front of an audience must start at step one.
    expect(screen.getByTestId('demo-tour-progress')).toHaveTextContent('1 of');
  });

  it('draws a spotlight for a step with a target', async () => {
    // Step two points at the chat host, so it has to exist for there to be anything to
    // spotlight. Rendering the tour in isolation is what a real page never does.
    const host = document.createElement('div');
    host.setAttribute('data-testid', 'chat-host');
    document.body.appendChild(host);

    try {
      renderTour();
      fireEvent.click(screen.getByTestId('demo-tour-next'));

      // Measurement is deferred to an animation frame so the target has painted.
      await screen.findByTestId('demo-tour-spotlight');
    } finally {
      host.remove();
    }
  });

  it('degrades to a centred card when the target is absent', () => {
    // A missing target must not point the spotlight at the top-left corner, which is what
    // an unchecked getBoundingClientRect would do.
    renderTour();
    fireEvent.click(screen.getByTestId('demo-tour-next'));

    expect(screen.queryByTestId('demo-tour-spotlight')).not.toBeInTheDocument();
    expect(screen.getByTestId('demo-tour-card')).toBeInTheDocument();
  });

  it('never parks the flyout on top of the element it is describing', () => {
    // The telemetry drawer is a 560px panel flush with the right edge. Clamping a
    // right-placed card back inside the viewport pushed it directly over its own
    // spotlight, so the narration was invisible behind the thing it pointed at.
    const drawer = document.createElement('div');
    drawer.id = 'telemetry-drawer';
    document.body.appendChild(drawer);

    const rect = { top: 0, left: 1992, width: 560, height: 1261, right: 2552, bottom: 1261, x: 1992, y: 0 };
    vi.spyOn(drawer, 'getBoundingClientRect').mockReturnValue({ ...rect, toJSON: () => ({}) } as DOMRect);

    try {
      renderTour();
      // Advance to the first step that spotlights the drawer.
      const drawerStep = DEMO_STEPS.findIndex(s => s.telemetry);
      for (let i = 0; i < drawerStep; i++) {
        fireEvent.click(screen.getByTestId('demo-tour-next'));
      }

      const card = screen.getByTestId('demo-tour-card');
      const left = Number.parseFloat(card.style.left);

      // Either clear of the drawer, or centred — never sitting on top of it.
      expect(left + 380).toBeLessThanOrEqual(rect.left + 1);
    } finally {
      drawer.remove();
    }
  });
});

describe('demo script', () => {
  it('has a unique id per step', () => {
    const ids = DEMO_STEPS.map(s => s.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('covers the AI gateway and its costing story', () => {
    const text = DEMO_STEPS.map(s => `${s.title} ${s.body}`).join(' ').toLowerCase();

    expect(text).toContain('api management');
    expect(text).toContain('managed identity');
    expect(text).toContain('token');
    expect(text).toContain('cost');
  });

  it('visits every navigable view', () => {
    const visited = new Set(DEMO_STEPS.map(s => s.view).filter(Boolean));

    // Every view reachable from the nav should get its moment, or the tour is not a
    // walkthrough of what shipped.
    for (const view of [
      'chat', 'promo', 'competitive', 'knowledge', 'council',
      'security', 'cards', 'observability', 'stores', 'financials', 'portfolio',
    ]) {
      expect(visited).toContain(view);
    }
  });

  it('keeps body copy short enough for a flyout', () => {
    for (const step of DEMO_STEPS) {
      expect(step.body.length).toBeLessThanOrEqual(400);
    }
  });
});
