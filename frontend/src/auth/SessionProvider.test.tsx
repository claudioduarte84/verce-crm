import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from './SessionProvider'
import { useSession } from './useSession'

function jsonResponse(body: unknown, status: number) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
}

function Probe() {
  const { session, logout, refresh } = useSession()
  return (
    <div>
      <div data-testid="status">{session.status}</div>
      <button onClick={() => void logout()}>logout</button>
      <button onClick={() => void refresh()}>refresh</button>
    </div>
  )
}

describe('SessionProvider', () => {
  const originalFetch = globalThis.fetch

  afterEach(() => {
    globalThis.fetch = originalFetch
    document.cookie = 'XSRF-TOKEN=; Max-Age=0; Path=/'
    vi.restoreAllMocks()
  })

  it('starts in the loading state before the session check resolves', () => {
    globalThis.fetch = vi.fn(() => new Promise<Response>(() => {})) // never resolves within this test

    render(
      <SessionProvider>
        <Probe />
      </SessionProvider>,
    )

    expect(screen.getByTestId('status')).toHaveTextContent('loading')
  })

  it('becomes authenticated when the session endpoint returns 200 with a user', async () => {
    // A fresh Response per call: refresh() now also fetches /api/auth/csrf before checking the
    // session, and a Response body can only be read once.
    globalThis.fetch = vi.fn().mockImplementation(() =>
      Promise.resolve(jsonResponse({ id: 'u1', email: 'owner@example.com', displayName: 'Owner', roles: ['Owner'] }, 200)),
    )

    render(
      <SessionProvider>
        <Probe />
      </SessionProvider>,
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
  })

  it('treats a 401 (no session) as anonymous', async () => {
    globalThis.fetch = vi.fn().mockImplementation(() =>
      Promise.resolve(jsonResponse({ title: 'Unauthorized', status: 401 }, 401)),
    )

    render(
      <SessionProvider>
        <Probe />
      </SessionProvider>,
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('anonymous'))
  })

  it.each([403, 404, 429, 500])('treats HTTP %s as unreachable, never as anonymous', async (status) => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status }))

    render(
      <SessionProvider>
        <Probe />
      </SessionProvider>,
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unreachable'))
  })

  it('becomes unreachable on a genuine network failure, never silently anonymous', async () => {
    globalThis.fetch = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'))

    render(
      <SessionProvider>
        <Probe />
      </SessionProvider>,
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unreachable'))
  })

  it('invalidates the readable antiforgery token after successful logout', async () => {
    document.cookie = 'XSRF-TOKEN=principal-a; Path=/'
    globalThis.fetch = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const path = String(input)
      if (path.endsWith('/logout')) return Promise.resolve(new Response(null, { status: 204 }))
      return Promise.resolve(jsonResponse({ id: 'u1', email: 'owner@example.com', displayName: 'Owner', roles: ['Owner'] }, 200))
    })

    render(<SessionProvider><Probe /></SessionProvider>)
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    fireEvent.click(screen.getByRole('button', { name: 'logout' }))
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('anonymous'))
    expect(document.cookie).not.toContain('XSRF-TOKEN=')
  })

  it('ignores a refresh result that completes after logout advances the auth generation', async () => {
    document.cookie = 'XSRF-TOKEN=principal-a; Path=/'
    let completeSession!: (response: Response) => void
    const pendingSession = new Promise<Response>((resolve) => { completeSession = resolve })
    globalThis.fetch = vi.fn().mockImplementation((input: RequestInfo | URL) =>
      String(input).endsWith('/logout') ? Promise.resolve(new Response(null, { status: 204 })) : pendingSession)

    render(<SessionProvider><Probe /></SessionProvider>)
    fireEvent.click(screen.getByRole('button', { name: 'logout' }))
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('anonymous'))
    completeSession(jsonResponse({ id: 'u1', email: 'owner@example.com', displayName: 'Owner', roles: ['Owner'] }, 200))
    await Promise.resolve()
    expect(screen.getByTestId('status')).toHaveTextContent('anonymous')
  })
})
