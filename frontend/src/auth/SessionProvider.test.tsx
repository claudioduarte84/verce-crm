import { render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from './SessionProvider'
import { useSession } from './useSession'

function jsonResponse(body: unknown, status: number) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
}

function Probe() {
  const { session } = useSession()
  return <div data-testid="status">{session.status}</div>
}

describe('SessionProvider', () => {
  const originalFetch = globalThis.fetch

  afterEach(() => {
    globalThis.fetch = originalFetch
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

  it('treats a 404 (endpoint not implemented yet) as anonymous, not as an error state', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 404 }))

    render(
      <SessionProvider>
        <Probe />
      </SessionProvider>,
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('anonymous'))
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
})
