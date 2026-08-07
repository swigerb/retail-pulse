import { useCallback, useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import {
  Badge,
  Body1,
  Button,
  Spinner,
  Subtitle1,
  Title2,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { BrandLogo } from '../../components/BrandLogo';
import { AUTH_FORBIDDEN_EVENT, AUTH_REQUIRED_EVENT } from '../authorizedFetch';
import type { AnonymousSessionProvider, AnonymousErrorCode } from '../providers/anonymousProvider';
import { gateStyles } from './gateStyles';

/**
 * The limitations every visitor must acknowledge before an anonymous demo session is minted. These
 * mirror the backend's deny-by-default anonymous surface; the UI hides the corresponding features
 * centrally via the capability object, and the backend remains authoritative.
 */
const LIMITATIONS: readonly string[] = [
  'This demo is billable and rate-limited.',
  'Read-only chat only — no write actions, approvals, or configuration.',
  'No live telemetry, streaming, memory, observability, admin, or export.',
  'Sessions are short-lived and are lost on expiry or restart.',
];

/**
 * Anonymous limited-demo sign-in gate (opt-in, non-production). Nothing billable happens until the
 * visitor gives EXPLICIT consent by clicking "Continue in limited demo"; only then is a short-lived,
 * session-only token minted. State comes from the provider's observable store.
 */
export function AnonymousAuthGate({
  provider,
  children,
}: {
  provider: AnonymousSessionProvider;
  children: ReactNode;
}) {
  const styles = gateStyles();
  const local = useLocalStyles();
  const state = useSyncExternalStore(
    (cb) => provider.subscribe(cb),
    () => provider.getState(),
    () => provider.getState(),
  );

  useEffect(() => {
    const onRequired = () => provider.handleAuthRequired();
    window.addEventListener(AUTH_REQUIRED_EVENT, onRequired);
    // A 403 for an anonymous token means the backend rejected a disallowed surface: end the session.
    window.addEventListener(AUTH_FORBIDDEN_EVENT, onRequired);
    return () => {
      window.removeEventListener(AUTH_REQUIRED_EVENT, onRequired);
      window.removeEventListener(AUTH_FORBIDDEN_EVENT, onRequired);
    };
  }, [provider]);

  const consent = useCallback(() => {
    void provider.bootstrap();
  }, [provider]);
  const retry = useCallback(() => provider.retry(), [provider]);

  if (state.status === 'authenticated') {
    return <>{children}</>;
  }

  return (
    <div className={styles.root} data-testid="auth-gate">
      <div className={styles.card}>
        <BrandLogo size={56} showWordmark={false} />
        <div className={styles.headings}>
          <Title2>Retail Pulse</Title2>
          <Subtitle1 className={styles.muted}>Limited demo mode</Subtitle1>
        </div>

        {state.status === 'authenticating' ? (
          <Spinner label="Starting your demo session…" data-testid="auth-signing-in" />
        ) : (
          <>
            <Badge appearance="tint" color="warning" data-testid="anon-warning-badge">
              Limited demo — billable &amp; rate-limited
            </Badge>
            <ul className={local.limitations} data-testid="anon-limitations">
              {LIMITATIONS.map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ul>

            {state.status === 'error' ? (
              <Body1 className={local.error} data-testid="auth-error">
                {errorMessage(state.errorCode as AnonymousErrorCode | undefined)}
              </Body1>
            ) : null}

            <div className={styles.actions}>
              <Button
                appearance="primary"
                size="large"
                onClick={state.status === 'error' ? retry : consent}
                data-testid="anon-continue-button"
              >
                {state.status === 'error' ? 'Try again' : 'Continue in limited demo'}
              </Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

/**
 * In-app banner shown while an anonymous session is active: it restates the limitations, shows the
 * remaining time before expiry, and offers "New anonymous session" / "Clear session" actions. It
 * reads expiry and drives session lifecycle entirely through the provider — components never talk to
 * the token store directly.
 */
export function AnonymousSessionBanner({ provider }: { provider: AnonymousSessionProvider }) {
  const styles = useBannerStyles();
  const [remainingMs, setRemainingMs] = useState<number | null>(() => provider.msUntilExpiry());

  useEffect(() => {
    const tick = () => setRemainingMs(provider.msUntilExpiry());
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [provider]);

  const clear = useCallback(() => provider.endSession(), [provider]);
  const renew = useCallback(() => {
    void provider.newSession();
  }, [provider]);

  return (
    <div className={styles.root} data-testid="anon-session-banner" role="status">
      <Badge appearance="tint" color="warning">
        Limited demo
      </Badge>
      <span className={styles.text}>
        Read-only chat. No telemetry, memory, or export.
        {remainingMs !== null ? (
          <span data-testid="anon-expiry"> Session expires in {formatRemaining(remainingMs)}.</span>
        ) : null}
      </span>
      <span className={styles.spacer} />
      <Button size="small" appearance="secondary" onClick={renew} data-testid="anon-new-session">
        New anonymous session
      </Button>
      <Button size="small" appearance="subtle" onClick={clear} data-testid="anon-clear-session">
        Clear session
      </Button>
    </div>
  );
}

function formatRemaining(ms: number): string {
  const totalSeconds = Math.max(0, Math.round(ms / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes <= 0) return `${seconds}s`;
  return `${minutes}m ${seconds.toString().padStart(2, '0')}s`;
}

function errorMessage(code: AnonymousErrorCode | undefined): string {
  switch (code) {
    case 'rate_limited':
      return 'The demo is busy right now (rate limit reached). Please wait a moment and try again.';
    case 'bootstrap_failed':
    default:
      return 'We could not start a demo session. Please try again.';
  }
}

const useLocalStyles = makeStyles({
  limitations: {
    textAlign: 'left',
    margin: 0,
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
});

const useBannerStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '6px 14px',
    background: tokens.colorNeutralBackground3,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  text: {
    color: tokens.colorNeutralForeground2,
  },
  spacer: {
    flex: 1,
  },
});
