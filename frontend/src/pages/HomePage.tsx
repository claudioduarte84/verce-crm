import { Link, useNavigate } from 'react-router-dom'
import { useSession } from '../auth/useSession'
import { useBranding } from '../branding/useBranding'

/** Minimal authenticated shell placeholder (mission §14) — S2+ modules (Customers, Catalog,
 * Quoting, ...) mount their own screens here; none of that exists yet. */
export function HomePage() {
  const { session, logout } = useSession()
  const { branding } = useBranding()
  const navigate = useNavigate()
  const user = session.status === 'authenticated' ? session.user : null

  async function handleLogout() {
    await logout()
    navigate('/login', { replace: true })
  }

  return (
    <div className="app-shell">
      <header className="app-shell__header">
        <div className="brand-lockup">
          {branding.branding.compactLogoUrl && <img src={branding.branding.compactLogoUrl} alt={branding.branding.productName} className="brand-lockup__logo" />}
          <strong>{branding.branding.productName}</strong>
        </div>
        <div>
          {user && <span style={{ marginRight: 'var(--space-4)' }}>{user.displayName ?? user.email}</span>}
          <button type="button" className="button-primary" onClick={() => void handleLogout()}>
            Sair
          </button>
        </div>
      </header>
      <main className="app-shell__main">
        <section className="product-page">
          <h1>Início</h1>
          <p>Gerencie os primeiros cadastros que sustentam o seu trabalho.</p>
          <nav className="quick-links" aria-label="Módulos disponíveis">
            <Link to="/customers">Clientes</Link>
            <Link to="/supplies">Suprimentos e estoque</Link>
            <Link to="/cost-laboratory">Laboratório de Custos</Link>
            <Link to="/settings">Configurações e marca</Link>
          </nav>
        </section>
      </main>
    </div>
  )
}
