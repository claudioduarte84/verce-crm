import { render, screen, waitFor } from '@testing-library/react'
import { StrictMode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { BrandingProvider } from './BrandingProvider'
import { useBranding } from './useBranding'

const api = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const sessionState = vi.hoisted(() => ({ current: { status: 'loading' } as { status: string } }))
vi.mock('../auth/useSession', () => ({ useSession: () => ({ session: sessionState.current }) }))

function Probe() {
  const { branding } = useBranding()
  return (
    <div>
      <div data-testid="status">{branding.status}</div>
      <div data-testid="name">{branding.branding.productName}</div>
      <div data-testid="subtitle">{branding.branding.productSubtitle}</div>
      <div data-testid="favicon">{branding.branding.faviconUrl ?? ''}</div>
    </div>
  )
}

describe('BrandingProvider', () => {
  beforeEach(() => {
    api.get.mockReset()
    sessionState.current = { status: 'loading' }
    document.title = 'stale title'
  })

  it('resolves to the safe fallback (never fetching) when there is no authenticated session', async () => {
    sessionState.current = { status: 'anonymous' }
    render(<BrandingProvider><Probe /></BrandingProvider>)
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('fallback'))
    expect(screen.getByTestId('name')).toHaveTextContent('VERCE 3D')
    expect(screen.getByTestId('subtitle')).toHaveTextContent('Laboratório de Custos')
    expect(api.get).not.toHaveBeenCalled()
    expect(document.title).toBe('VERCE 3D | Laboratório de Custos')
  })

  it('fetches and applies real server branding once authenticated, including document title and favicon', async () => {
    sessionState.current = { status: 'authenticated' }
    api.get.mockResolvedValue({
      ok: true,
      data: { productName: 'VERCE Custom', productSubtitle: 'Laboratório Custom', logos: [{ role: 'FAVICON', url: '/api/settings/brand-assets/versions/v1/content' }] },
    })
    document.head.innerHTML = '<link rel="icon" href="/favicon.svg" />'

    render(<BrandingProvider><Probe /></BrandingProvider>)
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('ready'))
    expect(screen.getByTestId('name')).toHaveTextContent('VERCE Custom')
    expect(document.title).toBe('VERCE Custom | Laboratório Custom')
    expect(document.querySelector("link[rel~='icon']")?.getAttribute('href')).toBe('/api/settings/brand-assets/versions/v1/content')
  })

  it('falls back safely without crashing the app when an authenticated fetch fails', async () => {
    sessionState.current = { status: 'authenticated' }
    api.get.mockResolvedValue({ ok: false, error: { safeMessage: 'network' } })
    render(<BrandingProvider><Probe /></BrandingProvider>)
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unavailable'))
    expect(screen.getByTestId('name')).toHaveTextContent('VERCE 3D')
  })

  it('does not duplicate the branding fetch under StrictMode double-invoked effects', async () => {
    sessionState.current = { status: 'authenticated' }
    api.get.mockResolvedValue({ ok: true, data: { productName: 'X', productSubtitle: 'Y', logos: [] } })
    render(
      <StrictMode>
        <BrandingProvider><Probe /></BrandingProvider>
      </StrictMode>,
    )
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('ready'))
    expect(api.get).toHaveBeenCalledTimes(1)
  })
})
