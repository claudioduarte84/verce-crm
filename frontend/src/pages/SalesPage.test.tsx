import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { SalesPage } from './SalesPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
const session = vi.hoisted(() => ({ roles: ['Viewer'] as string[] }))
vi.mock('../api/client', () => ({ apiClient: api }))
vi.mock('../auth/useSession', () => ({ useSession: () => ({ session: { status: 'authenticated', user: { roles: session.roles } } }) }))

beforeEach(() => {
  session.roles = ['Viewer']
  api.get.mockImplementation((path: string) => {
    if (path === '/api/sales-channels') return Promise.resolve({ ok: true, data: [{ id: 'inactive', name: 'Canal histórico', active: false }, { id: 'active', name: 'Canal atual', active: true }] })
    if (path.startsWith('/api/sales?')) return Promise.resolve({ ok: true, data: { items: [{ id: 's1', saleNumber: '260922-1', source: 'MANUAL_ENTRY', status: 'CONFIRMED', customerNameSnapshot: null, salesChannelId: 'inactive', soldAt: '2026-09-22T00:00:00Z', netAmount: 10, effectiveMarginPercent: .4 }], page: 1, pageSize: 20, total: 1 } })
    return Promise.resolve({ ok: true, data: { items: [] } })
  })
})

it('Viewer sees the real inactive historical channel label and no mutation form', async () => {
  render(<MemoryRouter><SalesPage /></MemoryRouter>)
  expect((await screen.findAllByText('Canal histórico')).length).toBeGreaterThan(1)
  expect(screen.queryByRole('heading', { name: 'Registrar venda manual' })).not.toBeInTheDocument()
})
