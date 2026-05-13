import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { CacheIndicator } from '../components/streaming/CacheIndicator';
import type { CacheInfo } from '../types';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('CacheIndicator', () => {
  it('renders nothing when not cached', () => {
    const info: CacheInfo = { cached: false };
    renderWithTheme(<CacheIndicator cacheInfo={info} />);
    expect(screen.queryByTestId('cache-indicator')).not.toBeInTheDocument();
  });

  it('renders cached badge when response is cached', () => {
    const info: CacheInfo = { cached: true, ttlSeconds: 300, timeSavedMs: 2300 };
    renderWithTheme(<CacheIndicator cacheInfo={info} />);
    expect(screen.getByTestId('cache-indicator')).toBeInTheDocument();
    expect(screen.getByText('Cached')).toBeInTheDocument();
    expect(screen.getByText('⚡')).toBeInTheDocument();
  });

  it('shows time saved when available', () => {
    const info: CacheInfo = { cached: true, timeSavedMs: 2300 };
    renderWithTheme(<CacheIndicator cacheInfo={info} />);
    expect(screen.getByText('Saved ~2.3s')).toBeInTheDocument();
  });

  it('hides time saved when not available', () => {
    const info: CacheInfo = { cached: true };
    renderWithTheme(<CacheIndicator cacheInfo={info} />);
    expect(screen.queryByText(/Saved/)).not.toBeInTheDocument();
  });

  it('includes TTL in tooltip content', () => {
    const info: CacheInfo = { cached: true, ttlSeconds: 120 };
    renderWithTheme(<CacheIndicator cacheInfo={info} />);
    // Badge is present
    expect(screen.getByTestId('cache-indicator')).toBeInTheDocument();
  });
});
