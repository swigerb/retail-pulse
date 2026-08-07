/**
 * Safe, branded configuration-error screen rendered by {@link bootstrap} when the auth bootstrap fails
 * closed (e.g. an explicit Entra build with missing/placeholder tenant/client configuration).
 *
 * It is intentionally dependency-light: pure inline styles, NO Fluent/MSAL/App imports, and — most
 * importantly — it makes NO API or hub calls. It never renders the dashboard or any protected surface;
 * it only tells an operator the deployment is misconfigured. The specific error text is deliberately
 * generic (no secrets, ids, or PII) so it is safe to show in any environment.
 */
export function ConfigErrorScreen() {
  return (
    <div
      role="alert"
      data-testid="config-error-screen"
      style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: '24px',
        background: '#11100f',
        color: '#f5f5f5',
        fontFamily:
          "'Segoe UI', system-ui, -apple-system, BlinkMacSystemFont, sans-serif",
      }}
    >
      <main
        style={{
          maxWidth: '520px',
          width: '100%',
          background: '#1f1e1d',
          border: '1px solid #3b3a39',
          borderRadius: '12px',
          padding: '32px',
          boxShadow: '0 8px 32px rgba(0,0,0,0.4)',
        }}
      >
        <div
          aria-hidden="true"
          style={{
            fontSize: '13px',
            fontWeight: 600,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: '#c8c6c4',
            marginBottom: '12px',
          }}
        >
          Retail Pulse
        </div>
        <h1 style={{ fontSize: '22px', margin: '0 0 12px', fontWeight: 600 }}>
          Sign-in is unavailable
        </h1>
        <p style={{ margin: '0 0 16px', lineHeight: 1.5, color: '#e1dfdd' }}>
          This deployment is not configured correctly, so signing in has been disabled to keep your
          data safe. No dashboard or data is loaded on this screen.
        </p>
        <p style={{ margin: 0, lineHeight: 1.5, color: '#a19f9d', fontSize: '14px' }}>
          If you are an administrator, verify the identity provider configuration for this
          environment and redeploy. If you reached this page unexpectedly, please contact your Retail
          Pulse administrator.
        </p>
      </main>
    </div>
  );
}
