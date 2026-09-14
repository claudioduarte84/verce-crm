import { useId, useState, type FormEvent } from 'react'
import { useLocation, useNavigate, type Location } from 'react-router-dom'
import { apiClient, hasAntiforgeryToken } from '../api/client'
import { useSession } from '../auth/useSession'

const LOGIN_ENDPOINT = '/api/auth/login'

export function LoginPage() {
  const emailId = useId()
  const passwordId = useId()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const { refresh } = useSession()
  const navigate = useNavigate()
  const location = useLocation()
  const from = (location.state as { from?: Location } | undefined)?.from?.pathname ?? '/'

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    // D-8: a pending-setup or wrong-password rejection must look identical — the backend, once
    // it implements this endpoint, is responsible for that; the frontend just surfaces whatever
    // safe message comes back.
    if (!hasAntiforgeryToken()) {
      const csrf = await apiClient.get('/api/auth/csrf')
      if (!csrf.ok) {
        setSubmitting(false)
        setError(csrf.error.safeMessage)
        return
      }
    }

    const result = await apiClient.post(LOGIN_ENDPOINT, { email, password })
    setSubmitting(false)

    if (!result.ok) {
      setError(result.error.safeMessage)
      return
    }

    await refresh({ refreshCsrf: true })
    navigate(from, { replace: true })
  }

  return (
    <form className="auth-form" onSubmit={handleSubmit} noValidate>
      <h1>Entrar</h1>
      <p style={{ margin: 0, color: 'var(--color-text-muted)', fontSize: '0.875rem' }}>VERCE 3D · Laboratório de Custos</p>

      <div className="form-field">
        <label htmlFor={emailId}>E-mail</label>
        <input
          id={emailId}
          type="email"
          autoComplete="username"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
        />
      </div>

      <div className="form-field">
        <label htmlFor={passwordId}>Senha</label>
        <input
          id={passwordId}
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
      </div>

      {error && (
        <p role="alert" className="form-error">
          {error}
        </p>
      )}

      <button type="submit" className="button-primary" disabled={submitting}>
        {submitting ? 'Entrando…' : 'Entrar'}
      </button>
    </form>
  )
}
