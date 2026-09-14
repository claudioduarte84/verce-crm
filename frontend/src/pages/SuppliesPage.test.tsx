import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { SuppliesPage } from './SuppliesPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const sessionState = vi.hoisted(() => ({ roles: ['Owner'] as string[] }))
vi.mock('../auth/useSession', () => ({
  useSession: () => ({ session: { status: 'authenticated', user: { id: 'u1', email: 'owner@example.test', displayName: 'Owner', roles: sessionState.roles } } }),
}))

const categoriesResponse = { ok: true, data: [{ code: 'FILAMENT', name: 'Filamento', isActive: true }, { code: 'PACKAGING', name: 'Embalagem', isActive: true }] }
const emptyListResponse = { ok: true, data: { items: [], page: 1, pageSize: 10, total: 0 } }

describe('SuppliesPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset(); api.put.mockReset(); api.delete.mockReset()
    sessionState.roles = ['Owner']
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/supplies/categories')) return Promise.resolve(categoriesResponse)
      if (path.includes('/inventory/movements')) return Promise.resolve({ ok: true, data: { items: [], page: 1, pageSize: 20, total: 0 } })
      return Promise.resolve(emptyListResponse)
    })
  })

  it('loads the categories and the first page of supplies on mount', async () => {
    render(<SuppliesPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/supplies/categories'))
    await waitFor(() => expect(api.get).toHaveBeenCalledWith(expect.stringContaining('status=active')))
  })

  it('maps Ativos, Inativos and Todos to explicit status contract values', async () => {
    const user = userEvent.setup()
    render(<SuppliesPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalledWith(expect.stringContaining('status=active')))
    await user.selectOptions(screen.getByLabelText('Situação'), 'inactive')
    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith(expect.stringContaining('status=inactive')))
    await user.selectOptions(screen.getByLabelText('Situação'), 'all')
    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith(expect.stringContaining('status=all')))
  })

  it('renders the supply list with stock and low-stock state', async () => {
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/supplies/categories')) return Promise.resolve(categoriesResponse)
      if (path.startsWith('/api/supplies?')) return Promise.resolve({
        ok: true,
        data: { items: [{ id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', categoryCode: 'FILAMENT', baseUnit: 'Gram', currentStockBaseUnit: 50, minimumStock: 100, isLowStock: true, latestPurchaseUnitCost: 0.09, active: true, version: 1 }], page: 1, pageSize: 10, total: 1 },
      })
      return Promise.resolve(emptyListResponse)
    })
    render(<SuppliesPage />)
    expect(await screen.findByText(/FIL-PLA-PRETO/)).toBeInTheDocument()
    expect(screen.getByText('Estoque baixo')).toBeInTheDocument()
  })

  it('searches supplies through the centralized client', async () => {
    const user = userEvent.setup()
    render(<SuppliesPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalledWith(expect.stringContaining('/api/supplies?')))
    await user.type(screen.getByLabelText('Buscar insumos'), 'PLA')
    await user.click(screen.getByRole('button', { name: 'Buscar' }))
    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith(expect.stringContaining('search=PLA')))
  })

  it('applies a changed category filter immediately', async () => {
    const user = userEvent.setup()
    render(<SuppliesPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/supplies/categories'))
    await user.selectOptions(document.getElementById('supply-category-filter')!, 'PACKAGING')
    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith(expect.stringContaining('categoryCode=PACKAGING')))
  })

  it('creates a supply without filament details', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: { id: 's2' } })
    render(<SuppliesPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/supplies/categories'))

    const createForm = screen.getByRole('heading', { name: 'Novo insumo' }).closest('form')!
    await user.type(within(createForm).getByLabelText('Código'), 'EMB-CAIXA-20X20')
    await user.type(within(createForm).getByLabelText('Nome'), 'Caixa 20x20')
    await user.selectOptions(within(createForm).getByLabelText('Categoria'), 'PACKAGING')
    await user.click(within(createForm).getByRole('button', { name: 'Salvar insumo' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/supplies', expect.objectContaining({ code: 'EMB-CAIXA-20X20', name: 'Caixa 20x20', categoryCode: 'PACKAGING', filament: null })))
    expect(await screen.findByRole('status')).toHaveTextContent('Insumo criado com sucesso.')
  })

  it('shows filament fields only when the filament checkbox is checked, and includes them on submit', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: { id: 's3' } })
    render(<SuppliesPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/supplies/categories'))

    const createForm = screen.getByRole('heading', { name: 'Novo insumo' }).closest('form')!
    expect(within(createForm).queryByLabelText('Marca')).not.toBeInTheDocument()
    await user.click(within(createForm).getByLabelText('Este insumo é filamento'))
    expect(within(createForm).getByLabelText('Marca')).toBeInTheDocument()

    await user.type(within(createForm).getByLabelText('Código'), 'FIL-PLA-AZUL')
    await user.type(within(createForm).getByLabelText('Nome'), 'PLA Azul')
    await user.selectOptions(within(createForm).getByLabelText('Categoria'), 'FILAMENT')
    await user.type(within(createForm).getByLabelText('Marca'), 'Voolt3D')
    await user.type(within(createForm).getByLabelText('Cor'), 'Azul')
    await user.type(within(createForm).getByLabelText('Diâmetro (mm)'), '1.75')
    await user.type(within(createForm).getByLabelText('Peso líquido do carretel (g)'), '1000')
    await user.click(within(createForm).getByRole('button', { name: 'Salvar insumo' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/supplies', expect.objectContaining({
      filament: expect.objectContaining({ brand: 'Voolt3D', colorName: 'Azul', diameterMm: 1.75, spoolNetWeightGrams: 1000 }),
    })))
  })

  it('shows the safe error message when creation fails', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: false, error: { safeMessage: 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.' } })
    render(<SuppliesPage />)
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/supplies/categories'))
    const createForm = screen.getByRole('heading', { name: 'Novo insumo' }).closest('form')!
    await user.type(within(createForm).getByLabelText('Código'), 'FIL-DUP')
    await user.type(within(createForm).getByLabelText('Nome'), 'Duplicado')
    await user.selectOptions(within(createForm).getByLabelText('Categoria'), 'FILAMENT')
    await user.click(within(createForm).getByRole('button', { name: 'Salvar insumo' }))
    expect(await screen.findByRole('status')).toHaveTextContent('dados mudaram')
  })

  it('opens a supply, registers a purchase receipt and reloads it', async () => {
    const user = userEvent.setup()
    const supply = { id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', description: null, categoryCode: 'FILAMENT', baseUnit: 'Gram', minimumStock: null, preferredSupplier: null, notes: null, active: true, currentStockBaseUnit: 0, latestPurchaseUnitCost: null, isLowStock: false, hasRecordedMovement: false, filament: null, version: 1 }
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/supplies/categories')) return Promise.resolve(categoriesResponse)
      if (path === '/api/supplies/s1') return Promise.resolve({ ok: true, data: supply })
      if (path.includes('/inventory/movements')) return Promise.resolve({ ok: true, data: { items: [], page: 1, pageSize: 20, total: 0 } })
      if (path.startsWith('/api/supplies?')) return Promise.resolve({ ok: true, data: { items: [{ id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', categoryCode: 'FILAMENT', baseUnit: 'Gram', currentStockBaseUnit: 0, minimumStock: null, isLowStock: false, latestPurchaseUnitCost: null, active: true, version: 1 }], page: 1, pageSize: 10, total: 1 } })
      return Promise.resolve(emptyListResponse)
    })
    api.post.mockResolvedValue({ ok: true, data: { ...supply, currentStockBaseUnit: 1000, hasRecordedMovement: true, version: 2 } })

    render(<SuppliesPage />)
    await user.click(await screen.findByRole('button', { name: /FIL-PLA-PRETO/ }))
    const purchaseForm = (await screen.findByRole('heading', { name: 'Registrar compra' })).closest('form')!

    await user.type(within(purchaseForm).getByLabelText('Quantidade'), '1')
    await user.selectOptions(within(purchaseForm).getByRole('combobox'), 'Kilogram')
    await user.type(within(purchaseForm).getByLabelText('Valor total (R$)'), '89.90')
    await user.click(within(purchaseForm).getByRole('button', { name: 'Registrar compra' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/supplies/s1/inventory/purchase-receipt', expect.objectContaining({ quantity: 1, enteredUnit: 'Kilogram', totalCost: 89.9, supplyVersion: 1 })))
    expect(await screen.findByRole('status')).toHaveTextContent('Compra registrada.')
  })

  it('rejects an insufficient-stock adjustment with the server-provided message', async () => {
    const user = userEvent.setup()
    const supply = { id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', description: null, categoryCode: 'FILAMENT', baseUnit: 'Gram', minimumStock: null, preferredSupplier: null, notes: null, active: true, currentStockBaseUnit: 50, latestPurchaseUnitCost: null, isLowStock: false, hasRecordedMovement: true, filament: null, version: 2 }
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/supplies/categories')) return Promise.resolve(categoriesResponse)
      if (path === '/api/supplies/s1') return Promise.resolve({ ok: true, data: supply })
      if (path.includes('/inventory/movements')) return Promise.resolve({ ok: true, data: { items: [], page: 1, pageSize: 20, total: 0 } })
      if (path.startsWith('/api/supplies?')) return Promise.resolve({ ok: true, data: { items: [{ id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', categoryCode: 'FILAMENT', baseUnit: 'Gram', currentStockBaseUnit: 50, minimumStock: null, isLowStock: false, latestPurchaseUnitCost: null, active: true, version: 2 }], page: 1, pageSize: 10, total: 1 } })
      return Promise.resolve(emptyListResponse)
    })
    api.post.mockResolvedValue({ ok: false, error: { safeMessage: 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.' } })

    render(<SuppliesPage />)
    await user.click(await screen.findByRole('button', { name: /FIL-PLA-PRETO/ }))
    const adjustmentForm = (await screen.findByRole('heading', { name: 'Ajuste manual' })).closest('form')!
    await user.selectOptions(within(adjustmentForm).getByLabelText('Tipo'), 'Decrease')
    await user.type(within(adjustmentForm).getByLabelText('Quantidade'), '100')
    await user.type(within(adjustmentForm).getByLabelText('Motivo'), 'Amostra')
    await user.click(within(adjustmentForm).getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/supplies/s1/inventory/adjustment', expect.objectContaining({ kind: 'Decrease', quantity: 100 })))
    expect(await screen.findByRole('status')).toHaveTextContent('dados mudaram')
  })

  it('renders entered and normalized history facts with server-backed pagination', async () => {
    const user = userEvent.setup()
    const supply = { id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', description: null, categoryCode: 'FILAMENT', baseUnit: 'Gram', minimumStock: null, preferredSupplier: null, notes: null, active: true, currentStockBaseUnit: 1000, latestPurchaseUnitCost: 0.0899, isLowStock: false, hasRecordedMovement: true, filament: null, version: 2 }
    const firstMovement = { id: 'm1', type: 'PurchaseReceipt', enteredQuantity: 1, enteredUnit: 'Kilogram', quantityDeltaBaseUnit: 1000, baseUnit: 'Gram', occurredAt: '2026-09-14T12:00:00Z', reason: null, reference: 'NF-42', supplier: 'Fornecedor X', unitCostSnapshot: 0.0899, totalCostSnapshot: 89.9, createdAt: '2026-09-14T12:00:00Z' }
    const secondMovement = { ...firstMovement, id: 'm2', enteredQuantity: 2, reference: 'NF-43' }
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/supplies/categories')) return Promise.resolve(categoriesResponse)
      if (path === '/api/supplies/s1') return Promise.resolve({ ok: true, data: supply })
      if (path.includes('/inventory/movements?page=1')) return Promise.resolve({ ok: true, data: { items: [firstMovement], page: 1, pageSize: 20, total: 21 } })
      if (path.includes('/inventory/movements?page=2')) return Promise.resolve({ ok: true, data: { items: [secondMovement], page: 2, pageSize: 20, total: 21 } })
      if (path.startsWith('/api/supplies?')) return Promise.resolve({ ok: true, data: { items: [{ id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', categoryCode: 'FILAMENT', baseUnit: 'Gram', currentStockBaseUnit: 1000, minimumStock: null, isLowStock: false, latestPurchaseUnitCost: 0.0899, active: true, version: 2 }], page: 1, pageSize: 10, total: 1 } })
      return Promise.resolve(emptyListResponse)
    })

    render(<SuppliesPage />)
    await user.click(await screen.findByRole('button', { name: /FIL-PLA-PRETO/ }))
    expect(await screen.findByText('Informado: 1 kg')).toBeInTheDocument()
    expect(screen.getByText('Normalizado: +1.000 g')).toBeInTheDocument()
    expect(screen.getByText('Fornecedor: Fornecedor X')).toBeInTheDocument()
    expect(screen.getByText('Referência: NF-42')).toBeInTheDocument()
    expect(screen.getByText('Custo total: R$ 89,90')).toBeInTheDocument()
    await user.click(within(screen.getByLabelText('Paginação do histórico')).getByRole('button', { name: 'Próxima' }))
    expect(await screen.findByText('Informado: 2 kg')).toBeInTheDocument()
    expect(api.get).toHaveBeenCalledWith(expect.stringContaining('/inventory/movements?page=2&pageSize=20'))
  })

  it('preserves entered quantity precision separately from normalized base precision', async () => {
    const supply = { id: 's1', code: 'FIL-PRECISION', name: 'PLA Precisão', description: null, categoryCode: 'FILAMENT', baseUnit: 'Gram', minimumStock: null, preferredSupplier: null, notes: null, active: true, currentStockBaseUnit: 0.01, latestPurchaseUnitCost: 1, isLowStock: false, hasRecordedMovement: true, filament: null, version: 2 }
    const movement = { id: 'm-precision', type: 'PurchaseReceipt', enteredQuantity: 0.00001, enteredUnit: 'Kilogram', quantityDeltaBaseUnit: 0.01, baseUnit: 'Gram', occurredAt: '2026-09-14T12:00:00Z', reason: null, reference: null, supplier: null, unitCostSnapshot: 1, totalCostSnapshot: 0.01, createdAt: '2026-09-14T12:00:00Z' }
    const maximumPrecisionMovement = { ...movement, id: 'm-maximum-precision', enteredQuantity: 1.23456789, quantityDeltaBaseUnit: 1234.5679 }
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/supplies/categories')) return Promise.resolve(categoriesResponse)
      if (path === '/api/supplies/s1') return Promise.resolve({ ok: true, data: supply })
      if (path.includes('/inventory/movements')) return Promise.resolve({ ok: true, data: { items: [movement, maximumPrecisionMovement], page: 1, pageSize: 20, total: 2 } })
      if (path.startsWith('/api/supplies?')) return Promise.resolve({ ok: true, data: { items: [{ id: 's1', code: 'FIL-PRECISION', name: 'PLA Precisão', categoryCode: 'FILAMENT', baseUnit: 'Gram', currentStockBaseUnit: 0.01, minimumStock: null, isLowStock: false, latestPurchaseUnitCost: 1, active: true, version: 2 }], page: 1, pageSize: 10, total: 1 } })
      return Promise.resolve(emptyListResponse)
    })

    render(<SuppliesPage />)
    await userEvent.setup().click(await screen.findByRole('button', { name: /FIL-PRECISION/ }))
    expect(await screen.findByText('Informado: 0,00001 kg')).toBeInTheDocument()
    expect(screen.getByText('Normalizado: +0,01 g')).toBeInTheDocument()
    expect(screen.getByText('Informado: 1,23456789 kg')).toBeInTheDocument()
    expect(screen.getByText('Normalizado: +1.234,5679 g')).toBeInTheDocument()
  })

  it('hides mutation controls for a Viewer', async () => {
    sessionState.roles = ['Viewer']
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/supplies/categories')) return Promise.resolve(categoriesResponse)
      if (path.startsWith('/api/supplies?')) return Promise.resolve({ ok: true, data: { items: [{ id: 's1', code: 'FIL-PLA-PRETO', name: 'PLA Preto', categoryCode: 'FILAMENT', baseUnit: 'Gram', currentStockBaseUnit: 0, minimumStock: null, isLowStock: false, latestPurchaseUnitCost: null, active: true, version: 1 }], page: 1, pageSize: 10, total: 1 } })
      return Promise.resolve(emptyListResponse)
    })
    render(<SuppliesPage />)
    expect(await screen.findByText(/FIL-PLA-PRETO/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Novo insumo' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Salvar insumo' })).not.toBeInTheDocument()
  })
})
