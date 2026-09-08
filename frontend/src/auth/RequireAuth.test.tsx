import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { RequireAuth } from './RequireAuth'
import { SessionContext } from './SessionContext'
import type { SessionState } from './session'

function renderWithSession(session: SessionState) {
  const value = { session, refresh: vi.fn(), logout: vi.fn() }
  return render(
    <MemoryRouter initialEntries={['/']}>
      <SessionContext value={value}>
        <Routes>
          <Route
            path="/"
            element={
              <RequireAuth>
                <div>protected content</div>
              </RequireAuth>
            }
          />
          <Route path="/login" element={<div>login page</div>} />
        </Routes>
      </SessionContext>
    </MemoryRouter>,
  )
}

describe('RequireAuth', () => {
  it('shows a loading indicator while the session is being resolved', () => {
    renderWithSession({ status: 'loading' })
    expect(screen.getByRole('status')).toHaveTextContent(/carregando/i)
  })

  it('renders the protected children when authenticated', () => {
    renderWithSession({
      status: 'authenticated',
      user: { id: 'u1', email: 'a@b.com', displayName: null, roles: [] },
    })
    expect(screen.getByText('protected content')).toBeInTheDocument()
  })

  it('redirects to /login when anonymous', () => {
    renderWithSession({ status: 'anonymous' })
    expect(screen.getByText('login page')).toBeInTheDocument()
    expect(screen.queryByText('protected content')).not.toBeInTheDocument()
  })

  it('shows an outage message instead of redirecting when the API is unreachable', () => {
    renderWithSession({ status: 'unreachable' })
    expect(screen.getByRole('alert')).toHaveTextContent(/não foi possível conectar/i)
    expect(screen.queryByText('login page')).not.toBeInTheDocument()
  })
})
