import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ReactElement } from 'react';
import AdaptiveCardPanel from '../components/cards/AdaptiveCardPanel';
import { fetchActiveCards } from '../services/cardsApi';
import type { AdaptiveCard } from '../types';

vi.mock('../services/cardsApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../services/cardsApi')>()),
  fetchActiveCards: vi.fn(),
  submitVote: vi.fn(),
}));

// The panel opens a SignalR connection on mount, which has nothing to do with the load
// behaviour under test and cannot reach a hub from jsdom.
vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    build() {
      return {
        on: () => undefined,
        start: () => Promise.resolve(),
        stop: () => Promise.resolve(),
        invoke: () => Promise.resolve(),
      };
    }
  }
  return { HubConnectionBuilder, HttpTransportType: {}, LogLevel: {} };
});

const wrap = (ui: ReactElement) => <FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>;

const card = (id: string, title: string): AdaptiveCard => ({
  id,
  title,
  type: 'voting',
  state: 'active',
  createdBy: 'council-orchestrator',
  createdAt: '2026-08-28T02:40:50Z',
  votes: [],
  comments: [],
  data: {},
} as unknown as AdaptiveCard);

/**
 * The panel only mounts when its view becomes active, so a walkthrough that pans straight
 * to it used to start a cold fetch and show nothing but a spinner for the whole segment.
 * The host can hand over cards it fetched earlier; these pin that contract.
 */
describe('AdaptiveCardPanel prewarming', () => {
  beforeEach(() => vi.clearAllMocks());

  it('paints supplied cards immediately instead of a spinner', async () => {
    // Never resolves: without the prewarm the panel would be stuck on the loading state.
    vi.mocked(fetchActiveCards).mockReturnValue(new Promise(() => {}));

    render(wrap(<AdaptiveCardPanel initialCards={[card('c1', 'Council Verdict: Summit Vodka')]} />));

    expect(screen.getByText('Council Verdict: Summit Vodka')).toBeInTheDocument();
    expect(screen.queryByText(/Loading cards/)).not.toBeInTheDocument();
  });

  it('still refreshes behind the supplied cards', async () => {
    vi.mocked(fetchActiveCards).mockResolvedValue([card('c2', 'Fresher Verdict')]);

    render(wrap(<AdaptiveCardPanel initialCards={[card('c1', 'Stale Verdict')]} />));

    await waitFor(() => expect(screen.getByText('Fresher Verdict')).toBeInTheDocument());
    expect(screen.queryByText('Stale Verdict')).not.toBeInTheDocument();
  });

  it('shows the loading state when nothing was supplied', () => {
    vi.mocked(fetchActiveCards).mockReturnValue(new Promise(() => {}));

    render(wrap(<AdaptiveCardPanel />));

    expect(screen.getByText(/Loading cards/)).toBeInTheDocument();
  });
});
