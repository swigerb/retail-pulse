import { resolveApiUrl } from '../config/apiOrigin';
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

/**
 * Loads the alerts the server already knows about.
 *
 * The SPA only ever populated alerts from the live hub event, so refreshing the page
 * discarded every alert even though the server still held it, and Alert History could
 * only show what happened to fire while that browser tab was open.
 *
 * The server's Alert record carries no status field — active and historical are decided
 * by which endpoint answers — so the status is set from the source here.
 */
async function getAlerts(path: string, status: Alert['status']): Promise<Alert[]> {
  const res = await fetch(resolveApiUrl(path));
  if (!res.ok) return [];
  const wire = (await res.json()) as WireAlert[];
  return wire.map(w => ({ ...toAlert(w), status }));
}

export async function fetchAlerts(historyLimit = 50): Promise<Alert[]> {
  const [active, history] = await Promise.all([
    getAlerts('/api/alerts/active', 'active'),
    getAlerts(`/api/alerts/history?limit=${historyLimit}`, 'dismissed'),
  ]);

  // An alert can appear in both responses; the active reading wins so a live alert is
  // never demoted into history by the order the two requests happen to resolve in.
  const byId = new Map<string, Alert>();
  for (const alert of history) byId.set(alert.id, alert);
  for (const alert of active) byId.set(alert.id, alert);

  return [...byId.values()].sort(
    (a, b) => new Date(b.firedAt).getTime() - new Date(a.firedAt).getTime(),
  );
}
