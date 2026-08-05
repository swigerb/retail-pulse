const HUB_PATH = '/hubs/telemetry';

/**
 * Resolves the SignalR telemetry hub URL.
 *
 * When `VITE_API_ORIGIN` is configured at build time, the hub targets that
 * origin directly (e.g. the Azure Container Apps API), because Static Web Apps
 * only proxies `/api` — not `/hubs`. Trailing slashes on the origin are
 * normalized so the result is always a clean absolute `${origin}/hubs/telemetry`.
 *
 * When the origin is unset or blank, it returns the relative `/hubs/telemetry`
 * path, preserving the local Aspire/Vite `/hubs` proxy behavior.
 *
 * The `origin` parameter defaults to the build-time env var but can be passed
 * explicitly to keep the resolver testable.
 */
export function resolveTelemetryHubUrl(
  origin: string | undefined = import.meta.env.VITE_API_ORIGIN,
): string {
  const trimmed = origin?.trim();
  if (!trimmed) {
    return HUB_PATH;
  }
  const normalizedOrigin = trimmed.replace(/\/+$/, '');
  return `${normalizedOrigin}${HUB_PATH}`;
}
