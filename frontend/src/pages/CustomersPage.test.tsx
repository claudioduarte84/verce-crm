import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CustomersPage } from './CustomersPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

describe('CustomersPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset(); api.put.mockReset(); api.delete.mockReset()
    api.get.mockResolvedValue({ ok: true, data: { items: [], page: 1, pageSize: 10, total: 0 } })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
  })

  it('renders an empty server result and sends an escaped search through the centralized client', async () => {
    const user = userEvent.setup()
    render(<CustomersPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalled())
    expect(api.get).toHaveBeenLastCalledWith('/api/customers?search=&page=1&pageSize=10')
    expect(screen.queryByText('Sem contato')).not.toBeInTheDocument()
    await user.type(screen.getByLabelText('Buscar clientes'), 'Ana & filhos')
    await user.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(api.get).toHaveBeenLastCalledWith('/api/customers?search=Ana+%26+filhos&page=1&pageSize=10')
  })

  it('renders customers and creates a valid customer, then refreshes the list', async () => {
    const user = userEvent.setup()
    api.get.mockResolvedValue({ ok: true, data: { items: [{ id: 'c1', name: 'Ana Silva', email: 'ana@example.test', isActive: true }], page: 1, pageSize: 10, total: 1 } })
    api.post.mockResolvedValue({ ok: true, data: { id: 'c2' } })
    render(<CustomersPage />)
    expect(await screen.findByText('Ana Silva')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Nome'), 'Bruno Souza')
    await user.type(screen.getByLabelText('CPF ou CNPJ'), '529.982.247-25')
    await user.click(screen.getByRole('button', { name: 'Salvar cliente' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/customers', expect.objectContaining({ name: 'Bruno Souza', document: '529.982.247-25', version: 0 })))
    expect(await screen.findByRole('status')).toHaveTextContent('Cliente criado com sucesso.')
  })

  it('shows the normalized ProblemDetails-safe message when creation conflicts', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: false, error: { safeMessage: 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.' } })
    render(<CustomersPage />)
    await user.type(screen.getByLabelText('Nome'), 'Cliente em conflito')
    await user.click(screen.getByRole('button', { name: 'Salvar cliente' }))
    expect(await screen.findByRole('status')).toHaveTextContent('dados mudaram')
  })

  it('paginates using previous/next controls and disables boundaries', async () => {
    const user = userEvent.setup()
    api.get.mockResolvedValue({ ok: true, data: { items: [{ id: 'c1', name: 'Cliente 1', isActive: true }], page: 1, pageSize: 10, total: 15 } })
    render(<CustomersPage />)
    expect(await screen.findByText('Página 1 de 2')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Próxima' })).not.toBeDisabled()

    api.get.mockResolvedValue({ ok: true, data: { items: [{ id: 'c2', name: 'Cliente 2', isActive: true }], page: 2, pageSize: 10, total: 15 } })
    await user.click(screen.getByRole('button', { name: 'Próxima' }))
    expect(await screen.findByText('Página 2 de 2')).toBeInTheDocument()
    expect(api.get).toHaveBeenLastCalledWith('/api/customers?search=&page=2&pageSize=10')
    expect(screen.getByRole('button', { name: 'Próxima' })).toBeDisabled()
  })

  it('deactivates the selected customer after confirmation and refreshes the list', async () => {
    const user = userEvent.setup()
    const customer = { id: 'c1', personType: 'Individual', name: 'Ana Silva', isActive: true, version: 3, addresses: [] }
    api.get.mockImplementation((path: string) => path.startsWith('/api/customers/c1') ? Promise.resolve({ ok: true, data: customer }) : Promise.resolve({ ok: true, data: { items: [{ id: 'c1', name: 'Ana Silva', isActive: true }], page: 1, pageSize: 10, total: 1 } }))
    api.delete.mockResolvedValue({ ok: true, data: undefined })
    render(<CustomersPage />)
    await user.click(await screen.findByRole('button', { name: 'Ana Silva' }))
    const deactivateButton = await screen.findByRole('button', { name: 'Desativar cliente' })
    await user.click(deactivateButton)
    expect(window.confirm).toHaveBeenCalled()
    await waitFor(() => expect(api.delete).toHaveBeenCalledWith('/api/customers/c1?version=3'))
    expect(await screen.findByRole('status')).toHaveTextContent('Cliente desativado com sucesso.')
  })

  it('edits and deletes an existing address using the current customer version', async () => {
    const user = userEvent.setup()
    const address = { id: 'a1', label: 'Ateliê', zipCode: '01001-000', street: 'Rua A', number: '1', district: 'Centro', city: 'São Paulo', state: 'SP', country: 'BR', isPrimary: true, isDefaultShipping: true, notes: null, complement: null }
    const customer = { id: 'c1', personType: 'Individual', name: 'Ana Silva', isActive: true, version: 5, addresses: [address] }
    api.get.mockImplementation((path: string) => path.startsWith('/api/customers/c1') ? Promise.resolve({ ok: true, data: customer }) : Promise.resolve({ ok: true, data: { items: [{ id: 'c1', name: 'Ana Silva', isActive: true }], page: 1, pageSize: 10, total: 1 } }))
    api.put.mockResolvedValue({ ok: true, data: address })
    api.delete.mockResolvedValue({ ok: true, data: undefined })
    render(<CustomersPage />)
    await user.click(await screen.findByRole('button', { name: 'Ana Silva' }))
    await user.click(await screen.findByRole('button', { name: 'Editar' }))
    expect(screen.getByRole('heading', { name: 'Editar endereço' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Salvar edição' }))
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/customers/c1/addresses/a1', expect.objectContaining({ customerVersion: 5 })))

    await user.click(await screen.findByRole('button', { name: 'Remover' }))
    await waitFor(() => expect(api.delete).toHaveBeenCalledWith('/api/customers/c1/addresses/a1?customerVersion=5'))
  })
})
