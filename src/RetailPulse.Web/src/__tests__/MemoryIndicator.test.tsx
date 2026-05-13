import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { MemoryIndicator } from '../components/MemoryIndicator';
import type { MemoryContext } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

describe('MemoryIndicator', () => {
  it('renders chip with memory summary', () => {
    const ctx: MemoryContext = {
      summary: 'Brand X, Southeast region',
      entries: [
        { id: 'm1', type: 'entity', content: 'Brand X', storedAt: new Date().toISOString() },
        { id: 'm2', type: 'preference', content: 'Southeast region', storedAt: new Date().toISOString() },
      ],
    };
    render(wrap(<MemoryIndicator memoryContext={ctx} />));
    expect(screen.getByTestId('memory-indicator')).toBeInTheDocument();
    expect(screen.getByText(/Remembered: Brand X, Southeast region/)).toBeInTheDocument();
  });

  it('renders brain emoji', () => {
    const ctx: MemoryContext = {
      summary: 'Recent query context',
      entries: [{ id: 'm1', type: 'conversation', content: 'Previous discussion', storedAt: new Date().toISOString() }],
    };
    render(wrap(<MemoryIndicator memoryContext={ctx} />));
    expect(screen.getByText('🧠')).toBeInTheDocument();
  });

  it('returns null when no entries', () => {
    const ctx: MemoryContext = { summary: '', entries: [] };
    const { container } = render(wrap(<MemoryIndicator memoryContext={ctx} />));
    expect(container.querySelector('[data-testid="memory-indicator"]')).toBeNull();
  });

  it('has accessible label with summary', () => {
    const ctx: MemoryContext = {
      summary: 'Brand preference noted',
      entries: [{ id: 'm1', type: 'preference', content: 'Prefers bar charts', storedAt: new Date().toISOString() }],
    };
    render(wrap(<MemoryIndicator memoryContext={ctx} />));
    expect(screen.getByLabelText('Memory context: Brand preference noted')).toBeInTheDocument();
  });
});
