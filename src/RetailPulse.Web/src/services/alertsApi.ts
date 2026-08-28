import type { Alert, AlertSeverity } from '../types';

/**
 * Normalises an alert pushed over the telemetry hub into the shape the panels read.
 *
 * Every emitter (`ProactiveAlertService`, `CompetitiveIntelAgent`) sends `detectedAt`,
 * while the panels read `firedAt`. `new Date(undefined)` is an Invalid Date, so every
 * row in Alert History rendered the literal text "Invalid Date" — and sorting by time
 * compared NaN, leaving the list in arbitrary order.
 *
 * The server also sends no `status`, so an alert arrived without the field the feed
 * uses to decide whether it is active, snoozed or dismissed.
 */
interface WireAlert {
  readonly id?: string;
  readonly title?: string;
  readonly severity?: string;
  readonly brand?: string;
  readonly region?: string;
  readonly description?: string;
  readonly recommendedAction?: string;
  /** What the server actually sends. */
  readonly detectedAt?: string;
  /** Accepted so a future server rename cannot silently reintroduce Invalid Date. */
  readonly firedAt?: string;
  readonly status?: string;
  readonly changePercent?: number;
  readonly metadata?: Record<string, unknown>;
}

const SEVERITIES: readonly AlertSeverity[] = ['high', 'medium', 'low'];

function toSeverity(raw: string | undefined): AlertSeverity {
  const value = (raw ?? '').toLowerCase();
  return SEVERITIES.find(s => s === value) ?? 'medium';
}

export function toAlert(wire: WireAlert): Alert {
  const firedAt = wire.firedAt ?? wire.detectedAt;

  return {
    id: wire.id ?? '',
    title: wire.title ?? 'Untitled alert',
    severity: toSeverity(wire.severity),
    brand: wire.brand,
    region: wire.region,
    changePercent: wire.changePercent,
    description: wire.description ?? '',
    recommendedAction: wire.recommendedAction ?? '',
    // Fall back to arrival time rather than emitting an unparseable string. An alert
    // that reached the client did happen now, near enough, and a readable timestamp is
    // more honest than "Invalid Date".
    firedAt: firedAt && !Number.isNaN(Date.parse(firedAt)) ? firedAt : new Date().toISOString(),
    status: wire.status === 'dismissed' || wire.status === 'snoozed' ? wire.status : 'active',
  };
}
