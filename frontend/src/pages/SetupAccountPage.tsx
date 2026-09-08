import { useId, useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { apiClient } from '../api/client'

const SETUP_ACCOUNT_ENDPOINT = '/api/auth/setup-account'

/** Consumes the single-use setup token the `bootstrap-owner`/`recover-owner` CLI prints
 * (ADR-0009 §7) — the route shape `/setup-account?token=...` matches what the CLI already
 * emits (Verce.Api/Cli/CliCommands.cs). */
export function SetupAccountPage() {
  const [searchParams] = useSearchParams()
  const token = searchParams.get('token') ?? ''
  const passwordId = useId()
  const confirmId = useId()
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const navigate = useNavigate()

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (password !== confirmPassword) {
      setError('As senhas não coincidem.')
      return
    }

    setSubmitting(true)
    setError(null)
    // D-6/D-7: the backend is responsible for atomic single-use consumption and for rejecting
    // a consumed/expired/invalidated token with an identical, non-revealing message.
    const result = await apiClient.post(SETUP_ACCOUNT_ENDPOINT, { token, password })
    setSubmitting(false)

    if (!result.ok) {
      setError(result.error.safeMessage)
      return
    }

    setDone(true)
  }

  if (!token) {
    return (
      <div role="alert" className="page-centered">
        Link de configuração inválido — nenhum token foi informado.
      </div>
    )
  }

  if (done) {
    return (
      <div className="page-centered">
        <div>
          <p>Conta configurada com sucesso.</p>
          <button type="button" className="button-primary" onClick={() => navigate('/login', { replace: true })}>
            Ir para o login
          </button>
        </div>
      </div>
    )
  }

  return (
    <form className="auth-form" onSubmit={handleSubmit} noValidate>
      <h1>Configurar conta</h1>

      <div className="form-field">
        <label htmlFor={passwordId}>Nova senha</label>
        <input
          id={passwordId}
          type="password"
          autoComplete="new-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
      </div>

      <div className="form-field">
        <label htmlFor={confirmId}>Confirmar senha</label>
        <input
          id={confirmId}
          type="password"
          autoComplete="new-password"
          required
          value={confirmPassword}
          onChange={(e) => setConfirmPassword(e.target.value)}
        />
      </div>

      {error && (
        <p role="alert" className="form-error">
          {error}
        </p>
      )}

      <button type="submit" className="button-primary" disabled={submitting}>
        {submitting ? 'Salvando…' : 'Salvar e continuar'}
      </button>
    </form>
  )
}
