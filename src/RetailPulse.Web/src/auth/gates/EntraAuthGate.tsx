import { useCallback, useEffect, useState, type ReactNode } from 'react';
import {
  Body1,
  Button,
  Spinner,
  Subtitle1,
  Title2,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useIsAuthenticated, useMsal } from '@azure/msal-react';
import { InteractionStatus } from '@azure/msal-browser';
import { BrandLogo } from '../../components/BrandLogo';
import { loginRequest } from '../authConfig';
import { AUTH_FORBIDDEN_EVENT } from '../authorizedFetch';
import { gateStyles } from './gateStyles';

/**
 * Polished Entra sign-in gate — the live production sign-in UX, unchanged from the pre-Sprint-3
 * implementation. The app tree renders only for a signed-in user; an unauthenticated visitor sees a
 * branded Microsoft sign-in screen and interactive sign-in is triggered explicitly (never silently
 * from a data fetch). A 403 from the API — authenticated but not assigned the RetailPulse.User role —
 * shows a precise access-denied message instead of a blank dashboard.
 */
export function EntraAuthGate({ children }: { children: ReactNode }) {
  const styles = gateStyles();
  const local = useLocalStyles();
  const { instance, inProgress } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const [forbidden, setForbidden] = useState(false);

  useEffect(() => {
    const onForbidden = () => setForbidden(true);
    window.addEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
    return () => window.removeEventListener(AUTH_FORBIDDEN_EVENT, onForbidden);
  }, []);

  const signIn = useCallback(() => {
    setForbidden(false);
    void instance.loginRedirect(loginRequest);
  }, [instance]);

  const signOut = useCallback(() => {
    void instance.logoutRedirect();
  }, [instance]);

  const busy =
    inProgress === InteractionStatus.Login ||
    inProgress === InteractionStatus.HandleRedirect ||
    inProgress === InteractionStatus.Startup;

  if (isAuthenticated && !forbidden) {
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

        {forbidden ? (
          <>
            <Body1 className={local.error} data-testid="auth-forbidden">
              Your account is signed in but not authorized for Retail Pulse. Ask an
              administrator to assign you the <strong>RetailPulse.User</strong> role, then try
              again.
            </Body1>
            <div className={styles.actions}>
              <Button appearance="primary" onClick={signIn}>
                Retry
              </Button>
              <Button appearance="subtle" onClick={signOut}>
                Sign in with a different account
              </Button>
            </div>
          </>
        ) : busy ? (
          <Spinner label="Signing you in…" data-testid="auth-signing-in" />
        ) : (
          <>
            <Body1 className={styles.muted}>
              Sign in with your organizational account to continue.
            </Body1>
            <div className={styles.actions}>
              <Button appearance="primary" size="large" onClick={signIn} data-testid="auth-signin-button">
                Sign in with Microsoft
              </Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

const useLocalStyles = makeStyles({
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
});
