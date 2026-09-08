import { useNavigate } from 'react-router-dom'
import { useSession } from '../auth/useSession'

/** Minimal authenticated shell placeholder (mission §14) — S2+ modules (Customers, Catalog,
 * Quoting, ...) mount their own screens here; none of that exists yet. */
export function HomePage() {
  const { session, logout } = useSession()
  const navigate = useNavigate()
  const user = session.status === 'authenticated' ? session.user : null

  async function handleLogout() {
    await logout()
    navigate('/login', { replace: true })
  }

  return (
    <div className="app-shell">
      <header className="app-shell__header">
        <strong>VERCE 3D · Laboratório de Custos</strong>
        <div>
          {user && <span style={{ marginRight: 'var(--space-4)' }}>{user.displayName ?? user.email}</span>}
          <button type="button" className="button-primary" onClick={() => void handleLogout()}>
            Sair
          </button>
        </div>
      </header>
      <main className="app-shell__main">
        <p>Bem-vindo(a). Esta é a base da aplicação — os módulos de negócio chegam nas próximas sprints.</p>
      </main>
    </div>
  )
}
