import type { ReactNode } from 'react';
import { ErrorBoundary } from './ErrorBoundary';

/**
 * Scoped error boundary for a single dashboard view.
 *
 * Only the app-level ErrorBoundary existed, so an uncaught render error in ANY
 * secondary panel replaced the ENTIRE dashboard with "Something went wrong" —
 * chat, telemetry, plans and all. That is what the Campaign Planner's
 * "Evaluate campaign" crash did: one panel took down the whole product.
 *
 * Wrapping each view here contains the blast radius to the panel that failed, so
 * the header stays usable and the operator can navigate away instead of
 * reloading. The app-level boundary remains as the last line of defence for
 * failures in the shell itself.
 */
export function PanelErrorBoundary({
  name,
  children,
}: {
  readonly name: string;
  readonly children: ReactNode;
}) {
  return (
    <ErrorBoundary
      fallback={
        <div
          role="alert"
          data-testid="panel-error"
          style={{
            margin: 24,
            padding: 20,
            borderRadius: 8,
            border: '1px solid var(--brand-accent-border, rgba(255,255,255,0.15))',
            background: 'var(--color-surface, #1A1A1A)',
            color: 'var(--color-text, #F5F5F0)',
          }}
        >
          <div style={{ fontWeight: 600, marginBottom: 6 }}>{name} is unavailable</div>
          <div style={{ color: 'var(--color-text-muted, #A0A0A0)', fontSize: 13 }}>
            This panel hit an unexpected error. The rest of Retail Pulse is still
            running — pick another view from the header, or reload to try again.
          </div>
        </div>
      }
    >
      {children}
    </ErrorBoundary>
  );
}
