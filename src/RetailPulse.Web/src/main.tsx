import { StrictMode } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import './index.css'
import App from './App.tsx'
import { getMsalInstance, initializeMsal } from './auth/msalInstance'
import { installAuthorizedFetch } from './auth/authorizedFetch'
import { activeAuthMode, getActiveProvider, requiresGate } from './auth/activeProvider'
import { assertEntraConfigured } from './auth/authConfig'
import { ConfigErrorScreen } from './auth/ConfigErrorScreen'

/**
 * Provider-neutral bootstrap. The active provider is fixed at build time by `VITE_AUTH_MODE`
 * (see auth/authMode). Exactly one provider path runs:
 *   • local-dev pass-through (Entra unconfigured) → render straight to the app (synthetic dev auth),
 *   • Entra                                       → unchanged MSAL lifecycle inside MsalProvider,
 *   • GitHub / Anonymous                          → provider.initialize() (e.g. the GitHub code
 *     exchange, which uses native fetch) runs BEFORE installAuthorizedFetch() so the exchange is
 *     never intercepted, then the app renders without MSAL.
 *
 * The whole sequence is wrapped in a fail-closed guard: if bootstrap throws (e.g. an explicit Entra
 * build whose tenant/client configuration is missing or a placeholder), we render a safe, branded
 * configuration-error screen that makes NO API/hub calls instead of the dashboard.
 */
async function bootstrap() {
  const root = createRoot(document.getElementById('root')!)

  try {
    await bootstrapAuthenticated(root)
  } catch (error) {
    // Never expose a protected surface on a bootstrap failure; show a safe, branded error screen.
    // eslint-disable-next-line no-console
    console.error('Retail Pulse bootstrap failed:', error)
    root.render(
      <StrictMode>
        <ConfigErrorScreen />
      </StrictMode>,
    )
  }
}

async function bootstrapAuthenticated(root: Root) {
  // Local-dev pass-through: no gate, no MSAL, no fetch wrapper.
  if (!requiresGate) {
    root.render(
      <StrictMode>
        <App />
      </StrictMode>,
    )
    return
  }

  if (activeAuthMode === 'entra') {
    // Live production Entra path. FAIL CLOSED FIRST: a missing/placeholder/invalid tenant or client id
    // throws here — before any MSAL init or App render, and before any API/hub call is ever made.
    assertEntraConfigured()

    // Complete any redirect sign-in, then centrally attach the bearer token to every API/hub fetch
    // before the app makes its first protected call.
    await initializeMsal()
    installAuthorizedFetch()

    root.render(
      <StrictMode>
        <MsalProvider instance={getMsalInstance()}>
          <App />
        </MsalProvider>
      </StrictMode>,
    )
    return
  }

  // GitHub / Anonymous: run the provider's one-time bootstrap (GitHub code redemption happens here
  // with native fetch) BEFORE installing the fetch wrapper, then render without MSAL.
  await getActiveProvider().initialize()
  installAuthorizedFetch()

  root.render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}

void bootstrap()
