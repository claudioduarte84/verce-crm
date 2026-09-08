import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClient } from './client'

function jsonResponse(body: unknown, init: ResponseInit & { contentType?: string } = {}) {
  const { contentType = 'application/json', ...rest } = init
  return new Response(JSON.stringify(body), {
    ...rest,
    headers: { 'content-type': contentType },
  })
}

describe('apiClient', () => {
  const originalFetch = globalThis.fetch

  function clearXsrfCookie() {
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/'
  }

  beforeEach(() => {
    clearXsrfCookie()
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
    vi.restoreAllMocks()
    clearXsrfCookie()
  })

  it('returns ok:true with the parsed JSON body on a 2xx response', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse({ id: '1', name: 'Ana' }, { status: 200 }))

    const result = await apiClient.get<{ id: string; name: string }>('/api/probe')

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data).toEqual({ id: '1', name: 'Ana' })
      expect(result.status).toBe(200)
    }
  })

  it('always sends credentials so the same-origin auth cookie is included', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}, { status: 200 }))
    globalThis.fetch = fetchMock

    await apiClient.get('/api/probe')

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/probe',
      expect.objectContaining({ credentials: 'include' }),
    )
  })

  it('normalizes a ProblemDetails 401 into a safe, non-revealing message', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      jsonResponse(
        { title: 'Unauthorized', status: 401, detail: 'JWT signature invalid for user internal-id 42' },
        { status: 401, contentType: 'application/problem+json' },
      ),
    )

    const result = await apiClient.get('/api/probe')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.status).toBe(401)
      expect(result.error.kind).toBe('problem')
      expect(result.error.safeMessage).not.toContain('internal-id')
      expect(result.error.safeMessage).toBe('Sessão expirada ou inválida. Faça login novamente.')
    }
  })

  it('normalizes a ProblemDetails 403 into a safe permission message', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      jsonResponse({ title: 'Forbidden', status: 403 }, { status: 403, contentType: 'application/problem+json' }),
    )

    const result = await apiClient.get('/api/probe')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.status).toBe(403)
      expect(result.error.safeMessage).toBe('Você não tem permissão para realizar esta ação.')
    }
  })

  it('collapses a non-ProblemDetails 500 response to the generic safe message, never raw text', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('Internal Server Error\n   at SomeInternalClass.Method()', {
        status: 500,
        headers: { 'content-type': 'text/plain' },
      }),
    )

    const result = await apiClient.get('/api/probe')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.safeMessage).not.toContain('SomeInternalClass')
      expect(result.error.kind).toBe('unexpected')
    }
  })

  it('reports a network failure distinctly from an HTTP error status', async () => {
    globalThis.fetch = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'))

    const result = await apiClient.get('/api/probe')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.kind).toBe('network')
      expect(result.error.status).toBe(0)
    }
  })

  it('attaches the antiforgery header from the XSRF-TOKEN cookie on a POST, when present', async () => {
    document.cookie = 'XSRF-TOKEN=probe-token-value'
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}, { status: 200 }))
    globalThis.fetch = fetchMock

    await apiClient.post('/api/probe', { a: 1 })

    const [, options] = fetchMock.mock.calls[0] as [string, RequestInit & { headers: Record<string, string> }]
    expect(options.headers['X-XSRF-TOKEN']).toBe('probe-token-value')
  })

  it('omits the antiforgery header on a GET even when the cookie is present', async () => {
    document.cookie = 'XSRF-TOKEN=probe-token-value'
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}, { status: 200 }))
    globalThis.fetch = fetchMock

    await apiClient.get('/api/probe')

    const [, options] = fetchMock.mock.calls[0] as [string, RequestInit & { headers: Record<string, string> }]
    expect(options.headers['X-XSRF-TOKEN']).toBeUndefined()
  })

  it('never sends the antiforgery header when the cookie is absent', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}, { status: 200 }))
    globalThis.fetch = fetchMock

    await apiClient.post('/api/probe', {})

    const [, options] = fetchMock.mock.calls[0] as [string, RequestInit & { headers: Record<string, string> }]
    expect(options.headers['X-XSRF-TOKEN']).toBeUndefined()
  })
})
