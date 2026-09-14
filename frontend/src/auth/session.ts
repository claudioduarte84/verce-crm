/**
 * Session/auth foundation (mission §15). Same-origin cookie authentication (ADR-0009 §1),
 * backed by the real `/api/auth/login`, `/api/auth/logout`, `/api/auth/session` and
 * `/api/auth/setup-account` endpoints (Verce.Api/Auth/AuthEndpoints.cs).
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
  /** The session could not be established because of a transport or non-401 HTTP failure —
   * distinct from "anonymous" so the UI never turns 403/429/5xx into a silent logout. */
  | { status: 'unreachable' }

export interface SessionContextValue {
  session: SessionState
  /** A successful login passes refreshCsrf so the anonymous antiforgery token is replaced with
   * one minted for the authenticated principal. Ordinary session checks reuse that token. */
  refresh: (options?: { refreshCsrf?: boolean }) => Promise<void>
  logout: () => Promise<void>
}
