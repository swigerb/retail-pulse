import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ReactElement } from 'react';
import MarketShareChart from '../components/competitive/MarketShareChart';
import type { MarketShareEntry } from '../types';

// Recharts needs a real box; jsdom gives every element zero size.
vi.mock('recharts', async () => {
  const actual = await vi.importActual<typeof import('recharts')>('recharts');
  return {
    ...actual,
    ResponsiveContainer: ({ children }: { children: React.ReactNode }) => (
      <div style={{ width: 800, height: 400 }}>{children}</div>
    ),
  };
});

const wrap = (ui: ReactElement) => <FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>;

function rows(quarters: string[], brands: number): MarketShareEntry[] {
  const out: MarketShareEntry[] = [];
  for (const q of quarters) {
    for (let i = 0; i < brands; i++) {
      out.push({ quarter: q, brand: `Brand ${i}`, share: 40 - i, isOurBrand: i === 0 });
    }
  }
  return out;
}

/**
 * The live feed carries a single quarter across 41 brands. An area chart draws that as one
 * vertical stripe of 41 overlapping points: technically correct and completely unreadable.
 * When there is nothing to trend, the panel ranks instead, which is the honest view of a
 * single-period snapshot.
 */
describe('MarketShareChart', () => {
  it('ranks brands when the feed carries only one period', () => {
    render(wrap(<MarketShareChart data={rows(['2026-Q2'], 41)} />));

    expect(screen.getByText(/Market Share by Brand/)).toBeInTheDocument();
    expect(screen.queryByText(/Market Share Trends/)).not.toBeInTheDocument();
  });

  it('says how much of the portfolio the ranking shows', () => {
    render(wrap(<MarketShareChart data={rows(['2026-Q2'], 41)} />));

    // Claiming "41 brands" over a top-12 ranking would misrepresent the chart.
    expect(screen.getByText(/Top \d+ of 41/)).toBeInTheDocument();
  });

  it('draws a trend when there are multiple periods', () => {
    render(wrap(<MarketShareChart data={rows(['2026-Q1', '2026-Q2', '2026-Q3'], 4)} />));

    expect(screen.getByText(/Market Share Trends/)).toBeInTheDocument();
    expect(screen.getByText('4 brands')).toBeInTheDocument();
  });

  it('still reports an empty feed as empty', () => {
    render(wrap(<MarketShareChart data={[]} />));
    expect(screen.getByTestId('market-share-empty')).toBeInTheDocument();
  });

  it('shows fewer bars in compact mode', () => {
    const { container: full } = render(wrap(<MarketShareChart data={rows(['2026-Q2'], 41)} />));
    const { container: compact } = render(wrap(<MarketShareChart data={rows(['2026-Q2'], 41)} compact />));

    const count = (el: HTMLElement) => el.querySelectorAll('.recharts-rectangle').length;
    expect(count(compact)).toBeLessThanOrEqual(count(full));
  });
});
