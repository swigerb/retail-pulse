import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import './index.css'
import App from './App.tsx'
import { authConfig } from './auth/authConfig'
import { getMsalInstance, initializeMsal } from './auth/msalInstance'
import { installAuthorizedFetch } from './auth/authorizedFetch'

async function bootstrap() {
  const root = createRoot(document.getElementById('root')!)

  if (authConfig.isConfigured) {
    // Production: complete any redirect sign-in, then centrally attach the bearer token to
    // every API/hub fetch before the app makes its first protected call.
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

  // Local dev: no Entra config — render straight to the app (API uses synthetic dev auth).
  root.render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}

void bootstrap()
