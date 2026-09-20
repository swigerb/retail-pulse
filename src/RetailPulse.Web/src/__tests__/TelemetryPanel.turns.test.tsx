import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { AgentSpan, TelemetryTurn } from '../types';
import { TelemetryPanel } from '../components/TelemetryPanel';

/**
 * Regression suite for issue #303 — the Live Spans panel used to append every
 * SignalR span to one flat list, so a viewer reading the panel next to an
 * answer would routinely see spans from an EARLIER prompt in the same chat
 * session (different brand, different region, different question) sitting
 * inches from the current answer and reasonably conclude the agent had
 * queried the wrong thing. These tests lock in the visible per-turn
 * boundaries, the current-turn marker, and the reset semantics that
 * prevent that misattribution from ever coming back.
 */

function renderPanel(props: Parameters<typeof TelemetryPanel>[0]) {
  return render(
    <FluentProvider theme={teamsDarkTheme}>
      <TelemetryPanel {...props} />
    </FluentProvider>,
  );
}

function makeSpan(overrides: Partial<AgentSpan>): AgentSpan {
  return {
    name: 'span',
    type: 'tool_call',
    detail: '',
    durationMs: 10,
    timestamp: new Date().toISOString(),
    ...overrides,
  };
}

const turn1: TelemetryTurn = {
  id: 'turn-1',
  index: 1,
  label: 'Compare Harvest Table vs FreshMart sell-through rates by region',
  startedAt: '2026-09-20T10:00:00.000Z',
};
const turn2: TelemetryTurn = {
  id: 'turn-2',
  index: 2,
  label: 'How is Coastline Tacos doing in the Northeast compared to the Midwest?',
  startedAt: '2026-09-20T10:05:00.000Z',
};

const twoTurnSpans: AgentSpan[] = [
  makeSpan({
    name: 'GetHistoricalDemand',
    type: 'tool_call',
    detail: '{"brand":"Harvest Table","region":"National"}',
    turnId: turn1.id,
  }),
  makeSpan({
    name: 'GetHistoricalDemand',
    type: 'tool_call',
    detail: '{"brand":"FreshMart","region":"National"}',
    turnId: turn1.id,
  }),
  makeSpan({
    name: 'GetHistoricalDemand',
    type: 'tool_call',
    detail: '{"brand":"Coastline Tacos","region":"Northeast"}',
    turnId: turn2.id,
  }),
  makeSpan({
    name: 'GetHistoricalDemand',
    type: 'tool_call',
    detail: '{"brand":"Coastline Tacos","region":"Midwest"}',
    turnId: turn2.id,
  }),
];

describe('TelemetryPanel — per-turn boundaries (issue #303)', () => {
  it('groups spans under the prompt that produced them and never mixes turns', () => {
    renderPanel({ liveSpans: twoTurnSpans, turns: [turn1, turn2], onClear: vi.fn() });

    // Both turn regions are present as accessible landmarks.
    const groups = screen.getAllByRole('region');
    expect(groups).toHaveLength(2);

    const [firstTurn, secondTurn] = groups;

    // Turn 1 shows the earlier prompt and only its two spans.
    expect(within(firstTurn).getByText(/Turn 1/i)).toBeInTheDocument();
    expect(
      within(firstTurn).getByText(/Compare Harvest Table vs FreshMart/i),
    ).toBeInTheDocument();
    expect(
      within(firstTurn).getByText(/"brand":"Harvest Table"/),
    ).toBeInTheDocument();
    expect(
      within(firstTurn).getByText(/"brand":"FreshMart"/),
    ).toBeInTheDocument();
    expect(
      within(firstTurn).queryByText(/Coastline Tacos/),
    ).not.toBeInTheDocument();

    // Turn 2 shows only the current-question spans — the demo failure mode
    // (Harvest Table / FreshMart spans appearing next to a Coastline Tacos
    // answer) is now structurally impossible.
    expect(within(secondTurn).getByText(/Turn 2/i)).toBeInTheDocument();
    // The prompt shows up in the header as an accessible label — verify
    // Turn 2 owns the Coastline Tacos prompt itself.
    expect(
      within(secondTurn).getByLabelText(/^Prompt:.*Coastline Tacos.*Northeast.*Midwest/i),
    ).toBeInTheDocument();
    expect(
      within(secondTurn).getByText(/"region":"Northeast"/),
    ).toBeInTheDocument();
    expect(
      within(secondTurn).getByText(/"region":"Midwest"/),
    ).toBeInTheDocument();
    expect(
      within(secondTurn).queryByText(/Harvest Table/),
    ).not.toBeInTheDocument();
    expect(
      within(secondTurn).queryByText(/FreshMart/),
    ).not.toBeInTheDocument();
  });

  it('marks the most recent turn as the current answer with aria-current', () => {
    renderPanel({ liveSpans: twoTurnSpans, turns: [turn1, turn2], onClear: vi.fn() });

    const groups = screen.getAllByRole('region');
    // Older turn: no aria-current and dataset flag is false.
    expect(groups[0]).not.toHaveAttribute('aria-current');
    expect(groups[0]).toHaveAttribute('data-turn-current', 'false');
    // Current turn: aria-current="true", dataset flag "true", and the
    // visible "Current" chip screen-readers can also see via the region label.
    expect(groups[1]).toHaveAttribute('aria-current', 'true');
    expect(groups[1]).toHaveAttribute('data-turn-current', 'true');
    expect(within(groups[1]).getByText(/^Current$/i)).toBeInTheDocument();
  });

  it('preserves cumulative header totals (spans count and tool calls) across turns', () => {
    renderPanel({
      liveSpans: twoTurnSpans,
      turns: [turn1, turn2],
      totalDurationMs: 1500,
      totalTokenUsage: { inputTokens: 100, outputTokens: 200, totalTokens: 300, estimatedCostUsd: 0.01 },
      onClear: vi.fn(),
    });

    // Header totals are cumulative across every turn in the session — the
    // issue explicitly asked us to preserve that behaviour so cost and
    // volume stay visible even after per-turn grouping.
    const totalTokensLabel = screen.getByText('Total Tokens');
    const totalTokensStat = totalTokensLabel.parentElement!;
    expect(within(totalTokensStat).getByText('300')).toBeInTheDocument();

    const spansLabel = screen.getByText('Spans');
    const spansStat = spansLabel.parentElement!;
    expect(within(spansStat).getByText('4')).toBeInTheDocument();

    const toolCallsLabel = screen.getByText('Tool Calls');
    const toolCallsStat = toolCallsLabel.parentElement!;
    expect(within(toolCallsStat).getByText('4')).toBeInTheDocument();
  });

  it('invokes onClear when the Clear Telemetry button is pressed', async () => {
    const onClear = vi.fn();
    const user = userEvent.setup();
    renderPanel({ liveSpans: twoTurnSpans, turns: [turn1, turn2], onClear });

    await user.click(screen.getByRole('button', { name: /Clear Telemetry/i }));
    expect(onClear).toHaveBeenCalledTimes(1);
  });

  it('hides the Clear Telemetry button and shows an empty timeline once cleared', () => {
    // After a reset (Clear or New Chat) the Dashboard drops liveSpans AND
    // turns together, so the panel must render the pre-turn empty state.
    renderPanel({ liveSpans: [], turns: [], onClear: vi.fn() });

    expect(screen.queryByRole('button', { name: /Clear Telemetry/i })).not.toBeInTheDocument();
    expect(screen.getByText(/Waiting for agent activity/i)).toBeInTheDocument();
    expect(screen.queryAllByRole('region')).toHaveLength(0);
  });

  it('falls back to a flat span list when no turns are provided (legacy consumers)', () => {
    // Callers that don't opt into turn grouping (isolated component tests,
    // demo previews) get the pre-#303 flat rendering with no grouping chrome.
    renderPanel({
      liveSpans: [makeSpan({ name: 'GetHistoricalDemand', detail: 'legacy' })],
      onClear: vi.fn(),
    });

    expect(screen.getByText('GetHistoricalDemand')).toBeInTheDocument();
    expect(screen.queryAllByRole('region')).toHaveLength(0);
    expect(screen.queryByText(/Turn 1/i)).not.toBeInTheDocument();
  });
});
