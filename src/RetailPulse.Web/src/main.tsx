import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import './index.css'
import App from './App.tsx'
import { getMsalInstance, initializeMsal } from './auth/msalInstance'
import { installAuthorizedFetch } from './auth/authorizedFetch'
import { activeAuthMode, getActiveProvider, requiresGate } from './auth/activeProvider'

/**
 * Provider-neutral bootstrap. The active provider is fixed at build time by `VITE_AUTH_MODE`
 * (see auth/authMode). Exactly one provider path runs:
 *   • local-dev pass-through (Entra unconfigured) → render straight to the app (synthetic dev auth),
 *   • Entra                                       → unchanged MSAL lifecycle inside MsalProvider,
 *   • GitHub / Anonymous                          → provider.initialize() (e.g. the GitHub code
 *     exchange, which uses native fetch) runs BEFORE installAuthorizedFetch() so the exchange is
 *     never intercepted, then the app renders without MSAL.
 */
async function bootstrap() {
  const root = createRoot(document.getElementById('root')!)

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
    // Live production Entra path — complete any redirect sign-in, then centrally attach the bearer
    // token to every API/hub fetch before the app makes its first protected call.
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
