import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { QuoteNewPage } from './QuoteNewPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const navigateSpy = vi.hoisted(() => vi.fn())
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom')
  return { ...actual, useNavigate: () => navigateSpy }
})

const channels = [{ id: 'ch1', code: 'DIRECT', name: 'Venda Direta', kind: 'Direct', defaultMarginPercent: null, notes: null, active: true, version: 1 }]

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/quotes/new']}>
      <Routes><Route path="/quotes/new" element={<QuoteNewPage />} /></Routes>
    </MemoryRouter>,
  )
}

describe('QuoteNewPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset(); navigateSpy.mockReset()
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/pricing/channels')) return Promise.resolve({ ok: true, data: channels })
      if (path.startsWith('/api/customers')) return Promise.resolve({ ok: true, data: { items: [], page: 1, pageSize: 10, total: 0 } })
      if (path.startsWith('/api/products')) return Promise.resolve({ ok: true, data: { items: [], page: 1, pageSize: 10, total: 0 } })
      return Promise.resolve({ ok: true, data: null })
    })
  })

  it('creates an ad-hoc quote sending the margin and discount as fractions, then navigates to the detail page', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: { id: 'q1' } })
    renderPage()

    await screen.findByRole('option', { name: 'Venda Direta' })
    await user.selectOptions(screen.getByLabelText('Canal de vendas'), 'ch1')
    await user.click(screen.getByLabelText('Item avulso'))
    await user.type(screen.getByPlaceholderText('Item avulso'), 'Vaso decorativo')
    await user.type(screen.getByLabelText(/Custo manual por unidade/), '20')
    await user.clear(screen.getByLabelText('Quantidade'))
    await user.type(screen.getByLabelText('Quantidade'), '2')
    await user.clear(screen.getByLabelText(/Margem desejada/))
    await user.type(screen.getByLabelText(/Margem desejada/), '40')

    await user.click(screen.getByRole('button', { name: 'Criar orçamento' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/quotes', expect.objectContaining({
      salesChannelId: 'ch1',
      items: [expect.objectContaining({ adHocDescription: 'Vaso decorativo', manualUnitCost: 20, quantity: 2, desiredMarginPercent: 0.4 })],
    })))
    expect(navigateSpy).toHaveBeenCalledWith('/quotes/q1')
  })

  it('shows a specific message for a known business error code instead of the generic one', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: false, error: { status: 422, safeMessage: 'Não foi possível concluir a operação.', problem: { code: 'QUOTE_SALES_CHANNEL_INACTIVE' } } })
    renderPage()

    await screen.findByRole('option', { name: 'Venda Direta' })
    await user.selectOptions(screen.getByLabelText('Canal de vendas'), 'ch1')
    await user.click(screen.getByLabelText('Item avulso'))
    await user.type(screen.getByPlaceholderText('Item avulso'), 'X')
    await user.type(screen.getByLabelText(/Custo manual por unidade/), '10')

    await user.click(screen.getByRole('button', { name: 'Criar orçamento' }))
    expect(await screen.findByRole('status')).toHaveTextContent('O canal de vendas selecionado está inativo.')
    expect(navigateSpy).not.toHaveBeenCalled()
  })

  it('rejects submission when a filled-in item has a zero quantity', async () => {
    // Every other invalid case (no product selected, no ad-hoc cost) is also blocked by the
    // <select required>/<input required> HTML attributes before this validation ever runs — a
    // native browser (and jsdom) refuses to fire the submit event at all in those cases. Zero
    // quantity is the one case that satisfies `required` (a value is present) yet still fails
    // the app's own `> 0` check, so it is the only path that actually reaches `validate()`.
    const user = userEvent.setup()
    renderPage()
    await screen.findByRole('option', { name: 'Venda Direta' })
    await user.selectOptions(screen.getByLabelText('Canal de vendas'), 'ch1')
    await user.click(screen.getByLabelText('Item avulso'))
    await user.type(screen.getByPlaceholderText('Item avulso'), 'X')
    await user.type(screen.getByLabelText(/Custo manual por unidade/), '10')
    await user.clear(screen.getByLabelText('Quantidade'))
    await user.type(screen.getByLabelText('Quantidade'), '0')

    await user.click(screen.getByRole('button', { name: 'Criar orçamento' }))
    expect(await screen.findByRole('status')).toHaveTextContent('quantidade de cada item deve ser maior que zero')
    expect(api.post).not.toHaveBeenCalled()
  })
})
