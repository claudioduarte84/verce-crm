import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { SaleDetailPage } from './SaleDetailPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
const session = vi.hoisted(() => ({ roles: ['Owner'] as string[] }))
vi.mock('../api/client', () => ({ apiClient: api }))
vi.mock('../auth/useSession', () => ({ useSession: () => ({ session: { status: 'authenticated', user: { roles: session.roles } } }) }))

const confirmed = { id: 's1', saleNumber: '260922-1', source: 'MANUAL_ENTRY', status: 'CONFIRMED', quoteRevisionId: null, customerNameSnapshot: 'Cliente', soldAt: '2026-09-22T00:00:00Z', netAmount: 10, channelFeeAmount: 0, shippingAmount: 0, totalCostAmount: 4, grossProfitAmount: 6, effectiveMarginPercent: .6, version: 7, items: [{ id: 'i1', productName: 'Produto', quantity: 1, lineTotalAmount: 10, lineCostAmount: 4 }], history: [] }

beforeEach(() => { session.roles = ['Owner']; api.get.mockReset(); api.post.mockReset() })
function renderPage() { return render(<MemoryRouter initialEntries={['/sales/s1']}><Routes><Route path="/sales/:saleId" element={<SaleDetailPage />} /></Routes></MemoryRouter>) }

it('manager cancels a confirmed sale with the canonical id, reason and version then refreshes', async () => {
  const user = userEvent.setup(); let canceled = false
  vi.spyOn(window, 'prompt').mockReturnValue('  Cliente desistiu  ')
  api.get.mockImplementation(() => Promise.resolve({ ok: true, data: canceled ? { ...confirmed, status: 'CANCELED', version: 8, history: [{ id: 'h1', toStatus: 'CANCELED', reason: 'Cliente desistiu', changedAt: '2026-09-22T10:00:00Z' }] } : confirmed }))
  api.post.mockImplementation(async () => { canceled = true; return { ok: true, data: undefined } })
  renderPage()
  await screen.findByRole('button', { name: 'Cancelar venda' })
  await user.click(screen.getByRole('button', { name: 'Cancelar venda' }))
  await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/sales/s1/cancel', { reason: 'Cliente desistiu', version: 7 }))
  expect(await screen.findByText('CANCELED', { exact: false })).toBeInTheDocument()
})

it('Viewer never sees the cancellation action', async () => {
  session.roles = ['Viewer']; api.get.mockResolvedValue({ ok: true, data: confirmed })
  renderPage()
  await screen.findByRole('heading', { name: /Venda 260922-1/ })
  expect(screen.queryByRole('button', { name: 'Cancelar venda' })).not.toBeInTheDocument()
})
