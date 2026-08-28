import { describe, it, expect } from 'vitest';
import { toAlert } from '../services/alertsApi';

/**
 * Every Alert History row rendered the literal text "Invalid Date".
 *
 * The server emitters all send `detectedAt`; the panels all read `firedAt`. The missing
 * field became `new Date(undefined)`, which is an Invalid Date — and sorting by time
 * compared NaN, so the list order was arbitrary too.
 */
describe('alert wire mapping', () => {
  // Captured from the shape ProactiveAlertService and CompetitiveIntelAgent emit.
  const wire = {
    id: 'alert-1',
    type: 'competitor_price_drop',
    severity: 'medium',
    title: "Competitor price drop: Maker's Mark on Ridgeline Bourbon in Southeast",
    description: 'Maker\u2019s Mark dropped 12% in Southeast.',
    brand: 'Ridgeline Bourbon',
    region: 'Southeast',
    recommendedAction: 'DIFFERENTIATE',
    detectedAt: '2026-08-27T14:31:00.000Z',
    metadata: {},
  };

  it('reads the timestamp the server actually sends', () => {
    expect(toAlert(wire).firedAt).toBe('2026-08-27T14:31:00.000Z');
  });

  it('produces a parseable date, which is what Invalid Date was not', () => {
    expect(Number.isNaN(Date.parse(toAlert(wire).firedAt))).toBe(false);
  });

  it('still accepts firedAt, so a server rename cannot reintroduce the bug', () => {
    const renamed = { ...wire, detectedAt: undefined, firedAt: '2026-01-02T03:04:05.000Z' };
    expect(toAlert(renamed).firedAt).toBe('2026-01-02T03:04:05.000Z');
  });

  it('falls back to a real timestamp when the server sends none', () => {
    const { detectedAt: _omitted, ...noTime } = wire;
    expect(Number.isNaN(Date.parse(toAlert(noTime).firedAt))).toBe(false);
  });

  it('falls back rather than passing through an unparseable timestamp', () => {
    const bad = { ...wire, detectedAt: 'not-a-date' };
    expect(Number.isNaN(Date.parse(toAlert(bad).firedAt))).toBe(false);
  });

  it('defaults status to active, because the server sends none', () => {
    // The feed branches on status to decide what is still actionable.
    expect(toAlert(wire).status).toBe('active');
  });

  it('preserves a status the server does send', () => {
    expect(toAlert({ ...wire, status: 'dismissed' }).status).toBe('dismissed');
    expect(toAlert({ ...wire, status: 'snoozed' }).status).toBe('snoozed');
  });

  it('rejects an unrecognised status rather than propagating it into the filters', () => {
    expect(toAlert({ ...wire, status: 'weird' }).status).toBe('active');
  });

  it('normalises severity casing and falls back for unknown values', () => {
    expect(toAlert({ ...wire, severity: 'HIGH' }).severity).toBe('high');
    expect(toAlert({ ...wire, severity: 'catastrophic' }).severity).toBe('medium');
  });

  it('carries the fields the history table renders', () => {
    const alert = toAlert(wire);
    expect(alert.brand).toBe('Ridgeline Bourbon');
    expect(alert.region).toBe('Southeast');
    expect(alert.title).toContain("Maker's Mark");
  });

  it('sorts newest first once mapped, which NaN timestamps prevented', () => {
    const older = toAlert({ ...wire, id: 'a', detectedAt: '2026-08-01T00:00:00.000Z' });
    const newer = toAlert({ ...wire, id: 'b', detectedAt: '2026-08-27T00:00:00.000Z' });

    const sorted = [older, newer].sort(
      (x, y) => new Date(y.firedAt).getTime() - new Date(x.firedAt).getTime(),
    );

    expect(sorted.map(a => a.id)).toEqual(['b', 'a']);
  });

  it('survives an empty payload without throwing', () => {
    const alert = toAlert({});
    expect(alert.status).toBe('active');
    expect(alert.severity).toBe('medium');
    expect(Number.isNaN(Date.parse(alert.firedAt))).toBe(false);
  });
});
