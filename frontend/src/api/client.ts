import { isProblemDetails, type ProblemDetails } from './problemDetails'

/**
 * Normalized, typed API error. `safeMessage` is ALWAYS a message fit to show a user directly
 * (pt-BR, per CLAUDE.md §1) — it never repeats a raw backend `detail` in Production, only in
 * development where `debugDetail` is also populated (mission §22 / SECURITY: no stack traces or
 * internal detail reaching Production UI).
 */
export interface ApiError {
  status: number
  kind: 'network' | 'problem' | 'unexpected'
  safeMessage: string
  debugDetail?: string
  fieldErrors?: Record<string, string[]>
  problem?: ProblemDetails
}

export type ApiResult<T> = { ok: true; data: T; status: number } | { ok: false; error: ApiError }

const DEFAULT_ERROR_MESSAGE = 'Não foi possível concluir a operação. Tente novamente em instantes.'
const NETWORK_ERROR_MESSAGE = 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.'

function safeMessageFor(problem: ProblemDetails | undefined, status: number): string {
  // Only a curated set of statuses get a specific, safe, user-facing message. Everything else
  // (500s, anything unexpected) collapses to the generic message — the backend's `detail` is
  // never shown verbatim to an end user, only kept in `debugDetail` for development.
  if (status === 401) return 'Sessão expirada ou inválida. Faça login novamente.'
  if (status === 403) return 'Você não tem permissão para realizar esta ação.'
  if (status === 404) return 'Recurso não encontrado.'
  if (status === 409) return 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.'
  if (status >= 400 && status < 500 && problem?.title) return problem.title
  return DEFAULT_ERROR_MESSAGE
}

/** Reads a cookie value by name, or null if absent — used for the antiforgery double-submit
 * header (mission §16/§17). Harmless no-op today: the backend does not yet issue this cookie. */
function readCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`))
  return match ? decodeURIComponent(match[1]) : null
}

const ANTIFORGERY_COOKIE_NAME = 'XSRF-TOKEN'
const ANTIFORGERY_HEADER_NAME = 'X-XSRF-TOKEN'
const STATE_CHANGING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

export function hasAntiforgeryToken(): boolean {
  return readCookie(ANTIFORGERY_COOKIE_NAME) !== null
}

/** Removes only the SPA-readable request-token cache. The server-owned HttpOnly antiforgery
 * cookie remains authoritative and a later GET /api/auth/csrf issues a matching request token. */
export function clearAntiforgeryToken(): void {
  document.cookie = `${ANTIFORGERY_COOKIE_NAME}=; Max-Age=0; Path=/; SameSite=Strict; Secure`
}

export interface ApiRequestOptions {
  method?: string
  body?: unknown
  signal?: AbortSignal
}

async function requestForm<T>(path: string, form: FormData): Promise<ApiResult<T>> {
  const token = readCookie(ANTIFORGERY_COOKIE_NAME)
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (token) headers[ANTIFORGERY_HEADER_NAME] = token
  try {
    const response = await fetch(path, { method: 'POST', headers, credentials: 'include', body: form })
    const payload = await response.json().catch(() => undefined)
    if (response.ok) return { ok: true, data: payload as T, status: response.status }
    const problem = isProblemDetails(payload) ? payload : undefined
    return { ok: false, error: { status: response.status, kind: problem ? 'problem' : 'unexpected', safeMessage: safeMessageFor(problem, response.status), fieldErrors: problem?.errors, problem } }
  } catch (cause) { return { ok: false, error: { status: 0, kind: 'network', safeMessage: NETWORK_ERROR_MESSAGE, debugDetail: String(cause) } } }
}

async function request<T>(path: string, options: ApiRequestOptions = {}): Promise<ApiResult<T>> {
  const method = options.method ?? 'GET'
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (STATE_CHANGING_METHODS.has(method)) {
    const token = readCookie(ANTIFORGERY_COOKIE_NAME)
    if (token) headers[ANTIFORGERY_HEADER_NAME] = token
  }

  let response: Response
  try {
    response = await fetch(path, {
      method,
      headers,
      credentials: 'include', // same-origin cookie auth (ADR-0009 §1) — never a bearer token
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    })
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause
    return {
      ok: false,
      error: { status: 0, kind: 'network', safeMessage: NETWORK_ERROR_MESSAGE, debugDetail: String(cause) },
    }
  }

  const contentType = response.headers.get('content-type') ?? ''
  const isJson = contentType.includes('application/json') || contentType.includes('application/problem+json')
  const payload: unknown = isJson ? await response.json().catch(() => undefined) : undefined

  if (response.ok) {
    return { ok: true, data: payload as T, status: response.status }
  }

  const problem = isProblemDetails(payload) ? payload : undefined
  return {
    ok: false,
    error: {
      status: response.status,
      kind: problem ? 'problem' : 'unexpected',
      safeMessage: safeMessageFor(problem, response.status),
      debugDetail: import.meta.env.DEV ? (problem?.detail ?? response.statusText) : undefined,
      fieldErrors: problem?.errors,
      problem,
    },
  }
}

/** Centralized API client (mission §17): one place owning base path, credentials, antiforgery,
 * JSON parsing and ProblemDetails normalization. No feature-specific clients until S2 modules
 * exist — callers pass their own path and payload/result types. */
export const apiClient = {
  get: <T>(path: string, signal?: AbortSignal) => request<T>(path, { method: 'GET', signal }),
  post: <T>(path: string, body?: unknown, signal?: AbortSignal) => request<T>(path, { method: 'POST', body, signal }),
  put: <T>(path: string, body?: unknown, signal?: AbortSignal) => request<T>(path, { method: 'PUT', body, signal }),
  delete: <T>(path: string, signal?: AbortSignal) => request<T>(path, { method: 'DELETE', signal }),
  postForm: <T>(path: string, form: FormData) => requestForm<T>(path, form),
}
