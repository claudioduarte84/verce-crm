import { BrowserRouter } from 'react-router-dom'
import { AppRoutes } from './app/AppRoutes'
import { ErrorBoundary } from './app/ErrorBoundary'
import { SessionProvider } from './auth/SessionProvider'

export function App() {
  return (
    <ErrorBoundary>
      <BrowserRouter>
        <SessionProvider>
          <AppRoutes />
        </SessionProvider>
      </BrowserRouter>
    </ErrorBoundary>
  )
}
