import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { useSession } from './useSession'

/** Authenticated/anonymous route handling (mission §15). Anonymous -> redirect to /login,
 * preserving the attempted location. "Unreachable" gets its own message rather than a redirect,
 * so a real outage never looks like a silent logout. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { session } = useSession()
  const location = useLocation()

  if (session.status === 'loading') {
    return (
      <div role="status" aria-live="polite" className="page-centered">
        Carregando sessão…
      </div>
    )
  }

  if (session.status === 'unreachable') {
    return (
      <div role="alert" className="page-centered">
        Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.
      </div>
    )
  }

  if (session.status === 'anonymous') {
    return <Navigate to="/login" replace state={{ from: location }} />
  }

  return children
}
