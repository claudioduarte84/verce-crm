import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { QuotesPage } from './QuotesPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const emptyListResponse = { ok: true, data: { items: [], page: 1, pageSize: 20, total: 0 } }

function renderPage() { return render(<MemoryRouter><QuotesPage /></MemoryRouter>) }

describe('QuotesPage', () => {
  beforeEach(() => {
    api.get.mockReset()
    api.get.mockResolvedValue(emptyListResponse)
  })

  it('loads the first page on mount and shows an empty state', async () => {
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/quotes?page=1&pageSize=20', expect.any(AbortSignal)))
    expect(await screen.findByText('Nenhum orçamento encontrado.')).toBeInTheDocument()
  })

  it('renders quote rows with status, validity and outcome', async () => {
    api.get.mockResolvedValue({
      ok: true,
      data: {
        items: [{
          id: 'q1', number: '260101-1', numberDate: '2026-01-01', customerId: null, customerName: null,
          currentRevisionIndex: 1, currentRevisionSuffix: '', currentStatus: 'SENT', validUntil: '2099-01-01',
          currentTotalAmount: 150.5, commercialOutcome: 'OPEN', productionOrderStatus: null, version: 1,
        }],
        page: 1, pageSize: 20, total: 1,
      },
    })
    renderPage()
    expect(await screen.findByText('260101-1')).toBeInTheDocument()
    expect(screen.getByText('Cliente avulso')).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'Enviado' })).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'Em aberto' })).toBeInTheDocument()
  })

  it('applies search and status filters as query parameters', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1))

    await user.type(screen.getByLabelText('Buscar por número ou cliente'), '260101')
    await user.selectOptions(screen.getByLabelText('Status'), 'SENT')
    await user.click(screen.getByRole('button', { name: 'Buscar' }))

    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith('/api/quotes?page=1&pageSize=20&search=260101&status=SENT', expect.any(AbortSignal)))
  })

  it('paginates using previous/next controls', async () => {
    const user = userEvent.setup()
    api.get.mockResolvedValue({ ok: true, data: { items: [], page: 1, pageSize: 20, total: 45 } })
    renderPage()
    expect(await screen.findByText('Página 1 de 3')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()

    api.get.mockResolvedValue({ ok: true, data: { items: [], page: 2, pageSize: 20, total: 45 } })
    await user.click(screen.getByRole('button', { name: 'Próxima' }))
    expect(await screen.findByText('Página 2 de 3')).toBeInTheDocument()
    expect(api.get).toHaveBeenLastCalledWith('/api/quotes?page=2&pageSize=20', expect.any(AbortSignal))
  })

  it('shows a safe error message when the list request fails', async () => {
    api.get.mockResolvedValue({ ok: false, error: { safeMessage: 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.' } })
    renderPage()
    expect(await screen.findByRole('status')).toHaveTextContent('Não foi possível conectar')
  })
})
