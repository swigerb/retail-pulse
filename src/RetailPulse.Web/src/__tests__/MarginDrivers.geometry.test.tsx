import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ReactElement } from 'react';
import { MarginDrivers } from '../components/margin/MarginDrivers';
import type { MarginDriver } from '../types';

const wrap = (ui: ReactElement) => (
  <FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>
);

const DRIVERS: MarginDriver[] = [
  { name: 'Overhead', impact: -3.4, trend: 'stable', isRisk: true },
  { name: 'Packaging', impact: 2.9, trend: 'improving', isRisk: false },
  { name: 'Logistics', impact: -2.1, trend: 'worsening', isRisk: true },
];

/**
 * Bars grow outward from a centre line, so each one lives in half the track.
 * These tests pin that geometry: sizing bars against the FULL track width made the
 * largest driver span an entire container from the middle, overrunning the name
 * column on the left and the impact labels on the right.
 */
describe('MarginDrivers bar geometry', () => {
  function barWidths(drivers: MarginDriver[]): number[] {
    const { container } = render(wrap(<MarginDrivers drivers={drivers} />));
    return Array.from(container.querySelectorAll<HTMLElement>('[data-testid="margin-drivers"] div'))
      .map(d => d.style.width)
      .filter(w => w.endsWith('%'))
      .map(w => Number.parseFloat(w));
  }

  it('never lets a bar exceed half the track', () => {
    for (const width of barWidths(DRIVERS)) {
      expect(width).toBeLessThanOrEqual(50);
    }
  });

  it('sizes the largest driver to exactly half the track', () => {
    // -3.4 is the largest absolute impact, so it defines the scale.
    expect(Math.max(...barWidths(DRIVERS))).toBeCloseTo(50, 5);
  });

  it('scales the remaining drivers proportionally against that maximum', () => {
    const widths = barWidths(DRIVERS);
    // 2.1 / 3.4 of the half-track.
    expect(Math.min(...widths)).toBeCloseTo((2.1 / 3.4) * 50, 5);
  });

  it('still bounds the bars when every driver has the same impact', () => {
    const flat: MarginDriver[] = [
      { name: 'A', impact: 2, trend: 'stable', isRisk: false },
      { name: 'B', impact: -2, trend: 'stable', isRisk: false },
    ];
    for (const width of barWidths(flat)) {
      expect(width).toBeCloseTo(50, 5);
    }
  });
});
