import { BrowserRouter } from 'react-router-dom'
import { AppRoutes } from './app/AppRoutes'
import { ErrorBoundary } from './app/ErrorBoundary'
import { SessionProvider } from './auth/SessionProvider'
import { BrandingProvider } from './branding/BrandingProvider'

export function App() {
  return (
    <ErrorBoundary>
      <BrowserRouter>
        <SessionProvider>
          <BrandingProvider>
            <AppRoutes />
          </BrandingProvider>
        </SessionProvider>
      </BrowserRouter>
    </ErrorBoundary>
  )
}
