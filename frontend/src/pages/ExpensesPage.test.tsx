import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import { ExpensesPage } from './ExpensesPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() }))
const session = vi.hoisted(() => ({ roles: ['Owner'] as string[] }))
vi.mock('../api/client', () => ({ apiClient: api }))
vi.mock('../auth/useSession', () => ({ useSession: () => ({ session: { status: 'authenticated', user: { roles: session.roles } } }) }))

beforeEach(() => {
  session.roles = ['Owner']; vi.spyOn(window, 'confirm').mockReturnValue(true)
  api.get.mockImplementation((path: string) => {
    if (path === '/api/expenses/categories') return Promise.resolve({ ok: true, data: [{ id: 'c1', name: 'Marketing', defaultTreatment: 'OPERATING_EXPENSE', isActive: true }] })
    if (path === '/api/sales-channels') return Promise.resolve({ ok: true, data: [{ id: 'old', name: 'Mercado Livre Histórico', active: false }, { id: 'ch1', name: 'Canal ativo', active: true }] })
    return Promise.resolve({ ok: true, data: { items: [{ id: 'e1', expenseCategoryId: 'c1', description: 'Histórico', amount: 10, incurredOn: '2026-09-22', accountingTreatment: 'OPERATING_EXPENSE', inventoryMovementId: null, salesChannelId: 'old', version: 1 }], page: 1, pageSize: 20, total: 1 } })
  })
})

it('preserves inactive historical labels and submits a canonical expense payload', async () => {
  const user = userEvent.setup(); api.post.mockResolvedValue({ ok: true, data: {} })
  render(<ExpensesPage />)
  expect((await screen.findAllByText('Mercado Livre Histórico')).length).toBeGreaterThan(1)
  await user.type(screen.getByLabelText('Descrição'), 'Anúncio')
  await user.type(screen.getByLabelText('Valor (R$)'), '15')
  await user.selectOptions(screen.getByLabelText('Canal de venda (opcional)'), 'ch1')
  await user.click(screen.getByRole('button', { name: 'Registrar despesa' }))
  expect(api.post).toHaveBeenCalledWith('/api/expenses', expect.objectContaining({ expenseCategoryId: 'c1', description: 'Anúncio', amount: 15, salesChannelId: 'ch1' }))
})

it('lets a Viewer read an inactive historical channel without expense mutation controls', async () => {
  session.roles = ['Viewer']
  render(<ExpensesPage />)

  expect(await screen.findByText('Histórico')).toBeInTheDocument()
  expect(screen.getByText(/R\$\s*10,00/)).toBeInTheDocument()
  expect(screen.getByRole('cell', { name: 'Mercado Livre Histórico' })).toBeInTheDocument()
  expect(screen.queryByText('—')).not.toBeInTheDocument()
  expect(screen.queryByRole('heading', { name: 'Nova despesa' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Registrar despesa' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Excluir' })).not.toBeInTheDocument()
})

it('warns before a PurchaseReceipt link can be submitted as an operating expense', async () => {
  const user = userEvent.setup(); render(<ExpensesPage />)
  await screen.findByText('Histórico')
  await user.type(screen.getByPlaceholderText('UUID do movimento'), 'movement-1')
  expect(await screen.findByText(/o sistema força Compra de estoque/)).toBeInTheDocument()
})

it('edits an expense with its current version and refreshes the canonical row', async () => {
  const user = userEvent.setup(); api.put.mockResolvedValue({ ok: true, data: {} })
  render(<ExpensesPage />)
  await screen.findByText('Histórico')
  await user.click(screen.getByRole('button', { name: 'Editar' }))
  const description = screen.getByLabelText('Descrição')
  await user.clear(description); await user.type(description, 'Histórico corrigido')
  await user.click(screen.getByRole('button', { name: 'Salvar despesa' }))
  expect(api.put).toHaveBeenCalledWith('/api/expenses/e1', expect.objectContaining({
    expenseCategoryId: 'c1', description: 'Histórico corrigido', amount: 10, incurredOn: '2026-09-22',
    accountingTreatment: 'OPERATING_EXPENSE', salesChannelId: 'old', version: 1,
  }))
  expect(await screen.findByRole('status')).toHaveTextContent('Despesa atualizada.')
})

it('deletes an expense through the confirmation flow and refreshes the list', async () => {
  const user = userEvent.setup(); let deleted = false
  api.delete.mockResolvedValue({ ok: true, data: undefined })
  api.get.mockImplementation((path: string) => {
    if (path === '/api/expenses/categories') return Promise.resolve({ ok: true, data: [{ id: 'c1', name: 'Marketing', defaultTreatment: 'OPERATING_EXPENSE', isActive: true }] })
    if (path === '/api/sales-channels') return Promise.resolve({ ok: true, data: [{ id: 'old', name: 'Mercado Livre Histórico', active: false }, { id: 'ch1', name: 'Canal ativo', active: true }] })
    return Promise.resolve({ ok: true, data: { items: deleted ? [] : [{ id: 'e1', expenseCategoryId: 'c1', description: 'Histórico', amount: 10, incurredOn: '2026-09-22', accountingTreatment: 'OPERATING_EXPENSE', inventoryMovementId: null, salesChannelId: 'old', version: 1 }], page: 1, pageSize: 20, total: deleted ? 0 : 1 } })
  })
  api.delete.mockImplementation(async () => { deleted = true; return { ok: true, data: undefined } })
  render(<ExpensesPage />)
  await screen.findByText('Histórico')
  await user.click(screen.getByRole('button', { name: 'Excluir' }))
  expect(api.delete).toHaveBeenCalledWith('/api/expenses/e1?version=1')
  expect(await screen.findByRole('status')).toHaveTextContent('Despesa excluída.')
  expect(screen.queryByText('Histórico')).not.toBeInTheDocument()
})
