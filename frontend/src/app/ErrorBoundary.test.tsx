import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ErrorBoundary } from './ErrorBoundary'

function Bomb(): never {
  throw new Error('boom')
}

describe('ErrorBoundary', () => {
  it('renders a safe fallback message instead of crashing when a child throws', () => {
    // React logs the caught error to the console by design; keep the test output clean.
    vi.spyOn(console, 'error').mockImplementation(() => {})

    render(
      <ErrorBoundary>
        <Bomb />
      </ErrorBoundary>,
    )

    expect(screen.getByRole('alert')).toHaveTextContent(/erro inesperado/i)
  })

  it('renders children normally when nothing throws', () => {
    render(
      <ErrorBoundary>
        <div>fine</div>
      </ErrorBoundary>,
    )

    expect(screen.getByText('fine')).toBeInTheDocument()
  })
})
