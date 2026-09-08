import { render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'

describe('App', () => {
  const originalFetch = globalThis.fetch

  afterEach(() => {
    globalThis.fetch = originalFetch
    vi.restoreAllMocks()
    window.history.pushState({}, '', '/')
  })

  it('boots and, with no session, redirects an anonymous visitor to the login page', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 401 }))

    render(<App />)

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Entrar' })).toBeInTheDocument())
  })

  it('renders the not-found page for an unknown route', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 401 }))
    window.history.pushState({}, '', '/this-route-does-not-exist')

    render(<App />)

    await waitFor(() => expect(screen.getByText(/página não encontrada/i)).toBeInTheDocument())
  })
})
