import { useCallback, useEffect, useSyncExternalStore, type ReactNode } from 'react';
import { Body1, Button, Spinner, Subtitle1, Title2, makeStyles, tokens } from '@fluentui/react-components';
import { BrandLogo } from '../../components/BrandLogo';
import { AUTH_FORBIDDEN_EVENT, AUTH_REQUIRED_EVENT } from '../authorizedFetch';
import type { GitHubSessionProvider, GitHubErrorCode } from '../providers/githubProvider';
import { gateStyles } from './gateStyles';

/**
 * GitHub Backend-for-Frontend sign-in gate (opt-in, non-production). The GitHub provider token never
 * touches the browser: the gate only ever navigates to the fixed same-origin start route and renders
 * the app once a short-lived Retail Pulse session token has been minted by the exchange. It reads its
 * state from the provider's observable store (no MSAL/React-context here) via `useSyncExternalStore`.
 */
export function GitHubAuthGate({
  provider,
  children,
}: {
  provider: GitHubSessionProvider;
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
    const onForbidden = () => provider.handleForbidden();
    window.addEventListener(AUTH_REQUIRED_EVENT, onRequired);
    window.addEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
    return () => {
      window.removeEventListener(AUTH_REQUIRED_EVENT, onRequired);
      window.removeEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
    };
  }, [provider]);

  const startLogin = useCallback(() => provider.startLogin(), [provider]);
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
          <Subtitle1 className={styles.muted}>Real-time retail intelligence</Subtitle1>
        </div>

        {state.status === 'initializing' || state.status === 'authenticating' ? (
          <Spinner label="Connecting to GitHub…" data-testid="auth-signing-in" />
        ) : state.status === 'error' ? (
          <>
            <Body1 className={local.error} data-testid="auth-error">
              {errorMessage(state.errorCode as GitHubErrorCode | undefined)}
            </Body1>
            <div className={styles.actions}>
              <Button appearance="primary" onClick={retry} data-testid="auth-retry-button">
                Try again
              </Button>
            </div>
          </>
        ) : (
          <>
            <Body1 className={styles.muted}>Sign in with your GitHub account to continue.</Body1>
            <div className={styles.actions}>
              <Button
                appearance="primary"
                size="large"
                icon={<GitHubMark />}
                onClick={startLogin}
                data-testid="auth-github-button"
              >
                Continue with GitHub
              </Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

/** Map a safe provider error code to a user-facing message. Never renders a raw provider string. */
function errorMessage(code: GitHubErrorCode | undefined): string {
  switch (code) {
    case 'access_denied':
      return 'GitHub sign-in was cancelled. You can try again when you are ready.';
    case 'not_authorized':
      return 'Your GitHub account is not authorized for Retail Pulse. Ask an administrator to add you to the allowlist, then try again.';
    case 'invalid_code':
      return 'That sign-in link has expired or was already used. Please sign in again.';
    case 'login_failed':
    case 'exchange_failed':
    default:
      return 'We could not complete GitHub sign-in. Please try again.';
  }
}

function GitHubMark() {
  return (
    <svg width="18" height="18" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
      <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z" />
    </svg>
  );
}

const useLocalStyles = makeStyles({
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
});
