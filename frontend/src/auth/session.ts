/**
 * Session/auth foundation (mission §15). Same-origin cookie authentication (ADR-0009 §1),
 * backed by the real `/api/auth/login`, `/api/auth/logout`, `/api/auth/session` and
 * `/api/auth/setup-account` endpoints (Verce.Api/Auth/AuthEndpoints.cs). The 404-tolerant
 * fallback in SessionProvider.refresh() is kept anyway: it costs nothing and keeps this
 * foundation robust if an endpoint is ever renamed or temporarily unavailable.
 */
export interface SessionUser {
  id: string
  email: string
  displayName: string | null
  roles: string[]
}

export type SessionState =
  | { status: 'loading' }
  | { status: 'authenticated'; user: SessionUser }
  | { status: 'anonymous' }
  /** The API could not be reached at all (network failure) — distinct from "anonymous" so the
   * UI can show an outage message instead of silently bouncing to /login. */
  | { status: 'unreachable' }

export interface SessionContextValue {
  session: SessionState
  refresh: () => Promise<void>
  logout: () => Promise<void>
}
