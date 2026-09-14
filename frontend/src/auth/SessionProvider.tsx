import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { apiClient, clearAntiforgeryToken, hasAntiforgeryToken } from '../api/client'
import { SessionContext } from './SessionContext'
import type { SessionState, SessionUser } from './session'

const SESSION_ENDPOINT = '/api/auth/session'
const LOGOUT_ENDPOINT = '/api/auth/logout'
const CSRF_ENDPOINT = '/api/auth/csrf'

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<SessionState>({ status: 'loading' })
  // React StrictMode deliberately replays mount effects in development. A session refresh
  // makes two rate-limited auth requests (CSRF + session), so simultaneous refreshes must share
  // one in-flight operation rather than doubling traffic or tripping SECURITY §3.2's limit.
  const refreshInFlight = useRef<Promise<void> | null>(null)
  const generation = useRef(0)

  const refresh = useCallback(async ({ refreshCsrf = false }: { refreshCsrf?: boolean } = {}) => {
    if (!refreshCsrf && refreshInFlight.current) {
      return refreshInFlight.current
    }

    // A post-login refresh establishes a new principal generation. Any older session request
    // must become incapable of restoring its identity after that boundary.
    if (refreshCsrf) generation.current += 1
    const operationGeneration = generation.current

    const operation = (async () => {
    if (generation.current === operationGeneration) setSession({ status: 'loading' })

    // Mint only when missing, or immediately after login when the anonymous token must be
    // replaced with one for the authenticated principal. Reissuing it on every route/reload
    // wastes a rate-limited /api/auth request without improving CSRF protection.
    if (refreshCsrf || !hasAntiforgeryToken()) {
      await apiClient.get(CSRF_ENDPOINT)
    }

    const result = await apiClient.get<SessionUser>(SESSION_ENDPOINT)
    if (generation.current !== operationGeneration) return

    if (result.ok) {
      setSession({ status: 'authenticated', user: result.data })
      return
    }

    // Only 401 proves that the identity is absent/expired. Authorization failures, throttling,
    // server errors and transport failures are transient session-check failures and must never
    // masquerade as logout.
    setSession(result.error.status === 401 ? { status: 'anonymous' } : { status: 'unreachable' })
    })()

    refreshInFlight.current = operation
    try {
      await operation
    } finally {
      if (refreshInFlight.current === operation) refreshInFlight.current = null
    }
  }, [])

  useEffect(() => {
    // Synchronizing with an external system (the server's session state) on mount is exactly
    // what this rule expects an effect for; the setState calls happen after `await`, in
    // refresh's own async continuation, not synchronously inside this effect body.
    // oxlint-disable-next-line react/set-state-in-effect
    void refresh()
  }, [refresh])

  const logout = useCallback(async () => {
    generation.current += 1
    const result = await apiClient.post(LOGOUT_ENDPOINT)
    if (result.ok) clearAntiforgeryToken()
    setSession(result.ok || result.error.status === 401 ? { status: 'anonymous' } : { status: 'unreachable' })
  }, [])

  return <SessionContext value={{ session, refresh, logout }}>{children}</SessionContext>
}
