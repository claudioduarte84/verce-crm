import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { apiClient } from '../api/client'
import { SessionContext } from './SessionContext'
import type { SessionState, SessionUser } from './session'

const SESSION_ENDPOINT = '/api/auth/session'
const LOGOUT_ENDPOINT = '/api/auth/logout'
const CSRF_ENDPOINT = '/api/auth/csrf'

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<SessionState>({ status: 'loading' })

  const refresh = useCallback(async () => {
    setSession({ status: 'loading' })

    // The antiforgery token is bound to the caller's identity at mint time (anonymous vs a
    // specific user) — refreshing it every time session state is (re)checked keeps it valid for
    // whatever comes next: the login form on first mount, or logout/setup-account right after
    // a successful login.
    await apiClient.get(CSRF_ENDPOINT)

    const result = await apiClient.get<SessionUser>(SESSION_ENDPOINT)

    if (result.ok) {
      setSession({ status: 'authenticated', user: result.data })
      return
    }

    if (result.error.kind === 'network') {
      setSession({ status: 'unreachable' })
      return
    }

    // 401 (no session) and 404 (endpoint not implemented yet on the backend — see session.ts)
    // are both treated as "anonymous": the safe, conservative default either way.
    if (result.error.status === 404 && import.meta.env.DEV) {
      console.warn(
        `[session] ${SESSION_ENDPOINT} returned 404 — the backend does not expose this endpoint ` +
          'yet. Treating the user as anonymous. See the S1 FINAL REPORT open issues.',
      )
    }
    setSession({ status: 'anonymous' })
  }, [])

  useEffect(() => {
    // Synchronizing with an external system (the server's session state) on mount is exactly
    // what this rule expects an effect for; the setState calls happen after `await`, in
    // refresh's own async continuation, not synchronously inside this effect body.
    // oxlint-disable-next-line react/set-state-in-effect
    void refresh()
  }, [refresh])

  const logout = useCallback(async () => {
    await apiClient.post(LOGOUT_ENDPOINT)
    setSession({ status: 'anonymous' })
  }, [])

  return <SessionContext value={{ session, refresh, logout }}>{children}</SessionContext>
}
