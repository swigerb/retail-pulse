import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { MemoryPanel } from '../components/MemoryPanel';
import type { MemoryEntry } from '../types';

// Mock the memory API
vi.mock('../services/memoryApi', () => ({
  fetchMemories: vi.fn().mockResolvedValue([]),
  deleteMemory: vi.fn().mockResolvedValue(undefined),
  deleteAllMemories: vi.fn().mockResolvedValue(undefined),
}));

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const mockEntries: MemoryEntry[] = [
  { id: 'm1', type: 'conversation', content: 'User prefers Southeast region data', storedAt: new Date(Date.now() - 3600000).toISOString() },
  { id: 'm2', type: 'preference', content: 'Preferred chart type: bar chart', storedAt: new Date(Date.now() - 7200000).toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() },
  { id: 'm3', type: 'entity', content: 'Brand X - Premium Spirits Category', storedAt: new Date(Date.now() - 86400000).toISOString(), tags: ['brand', 'spirits'] },
  { id: 'm4', type: 'entity', content: 'FreshMart - Grocery Category', storedAt: new Date(Date.now() - 172800000).toISOString() },
  { id: 'm5', type: 'conversation', content: 'Discussed Q1 supply chain issues', storedAt: new Date(Date.now() - 259200000).toISOString() },
];

describe('MemoryPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders memory panel with entries', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    expect(screen.getByTestId('memory-panel')).toBeInTheDocument();
    expect(screen.getAllByTestId('memory-entry')).toHaveLength(5);
  });

  it('groups entries by type', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    expect(screen.getByText('Conversations')).toBeInTheDocument();
    expect(screen.getByText('Preferences')).toBeInTheDocument();
    expect(screen.getByText('Entities')).toBeInTheDocument();
  });

  it('displays entry content', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    expect(screen.getByText('User prefers Southeast region data')).toBeInTheDocument();
    expect(screen.getByText('Preferred chart type: bar chart')).toBeInTheDocument();
    expect(screen.getByText('Brand X - Premium Spirits Category')).toBeInTheDocument();
  });

  it('shows relative time for stored entries', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    // Multiple entries show relative times
    const timeElements = screen.getAllByText(/Stored \d+[hmd] ago/);
    expect(timeElements.length).toBeGreaterThan(0);
  });

  it('shows expiry information', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    expect(screen.getByText(/expires in/)).toBeInTheDocument();
  });

  it('shows tags on entries', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    expect(screen.getByText('#brand')).toBeInTheDocument();
    expect(screen.getByText('#spirits')).toBeInTheDocument();
  });

  it('has forget buttons for each entry', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    const forgetButtons = screen.getAllByTestId('forget-button');
    expect(forgetButtons).toHaveLength(5);
  });

  it('calls deleteMemory when forget button is clicked', async () => {
    const { deleteMemory } = await import('../services/memoryApi');
    render(wrap(<MemoryPanel entries={mockEntries} />));

    const forgetButtons = screen.getAllByTestId('forget-button');
    await userEvent.click(forgetButtons[0]);

    expect(deleteMemory).toHaveBeenCalledWith('m1');
  });

  it('has forget-all button', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    expect(screen.getByTestId('forget-all-button')).toBeInTheDocument();
  });

  it('calls deleteAllMemories when forget-all is clicked', async () => {
    const { deleteAllMemories } = await import('../services/memoryApi');
    render(wrap(<MemoryPanel entries={mockEntries} />));

    await userEvent.click(screen.getByTestId('forget-all-button'));

    expect(deleteAllMemories).toHaveBeenCalled();
  });

  it('filters entries by search', async () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));

    const searchInput = screen.getByTestId('memory-search');
    await userEvent.type(searchInput, 'Brand X');

    expect(screen.getAllByTestId('memory-entry')).toHaveLength(1);
    expect(screen.getByText('Brand X - Premium Spirits Category')).toBeInTheDocument();
  });

  it('filters entries by type', async () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));

    // Click the Entities filter chip (in the filter chips area, not the group header)
    const entityChips = screen.getAllByText(/Entities/);
    // The filter chip has the count in parens, e.g. "🏷️ Entities (2)"
    const filterChip = entityChips.find(el => el.textContent?.includes('('));
    await userEvent.click(filterChip!);

    expect(screen.getAllByTestId('memory-entry')).toHaveLength(2);
  });

  it('shows empty state when no entries', () => {
    render(wrap(<MemoryPanel entries={[]} />));
    expect(screen.getByTestId('memory-empty')).toBeInTheDocument();
    expect(screen.getByText('No memories stored yet')).toBeInTheDocument();
  });

  it('shows total count badge', () => {
    render(wrap(<MemoryPanel entries={mockEntries} />));
    expect(screen.getByText('5')).toBeInTheDocument();
  });

  it('re-fetches managed memories when refreshKey changes', async () => {
    const { fetchMemories } = await import('../services/memoryApi');
    vi.mocked(fetchMemories)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([mockEntries[0]]);

    const { rerender } = render(wrap(<MemoryPanel refreshKey={0} />));

    await waitFor(() => expect(fetchMemories).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByText('No memories stored yet')).toBeInTheDocument());

    rerender(wrap(<MemoryPanel refreshKey={1} />));

    await waitFor(() => expect(fetchMemories).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('User prefers Southeast region data')).toBeInTheDocument();
  });
});
