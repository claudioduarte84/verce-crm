import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ProductsPage } from './ProductsPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const sessionState = vi.hoisted(() => ({ roles: ['Owner'] as string[] }))
vi.mock('../auth/useSession', () => ({
  useSession: () => ({ session: { status: 'authenticated', user: { id: 'u1', email: 'owner@example.test', displayName: 'Owner', roles: sessionState.roles } } }),
}))

const emptyListResponse = { ok: true, data: { items: [], page: 1, pageSize: 10, total: 0 } }
const supplies = [
  { id: 's1', code: 'FIL-PLA', name: 'PLA Preto', baseUnit: 'Gram', active: true, currentStockBaseUnit: 100, policy: 'WEIGHTED_AVERAGE_ACQUISITION', weightedAverageUnitCost: 0.1, costBasisAvailable: true },
  { id: 's2', code: 'EMB-BOX', name: 'Caixa', baseUnit: 'Unit', active: true, currentStockBaseUnit: 10, policy: 'WEIGHTED_AVERAGE_ACQUISITION', weightedAverageUnitCost: 2, costBasisAvailable: true },
]

const product = {
  id: 'p1', code: 'MINI-VASE', name: 'Mini Vaso', description: null, active: true, version: 1,
  recipe: {
    id: 'r1', productId: 'p1', revisionNumber: 1, wastagePercentOverride: null, laborMinutes: null, laborHourlyRateOverride: null,
    machineMinutes: null, machineHourlyRate: null, outputQuantity: 1, notes: null, materialLines: [], additionalCostLines: [],
  },
}

const cost = {
  engineVersion: '1.0.0',
  materials: [{ supplyId: 's1', supplyCode: 'FIL-PLA', supplyName: 'PLA Preto', enteredQuantity: 100, enteredUnit: 'Gram', normalizedQuantityBaseUnit: 100, baseUnit: 'Gram', wastagePercent: 0, effectiveQuantityBaseUnit: 100, costSource: 'WEIGHTED_AVERAGE_ACQUISITION', costPolicy: 'WEIGHTED_AVERAGE_ACQUISITION', unitCostBaseUnit: 0.1, costBeforeWastage: 10, wastageCost: 0, costAfterWastage: 10, currentStockBaseUnit: 100, exceedsCurrentStock: false, warnings: [] }],
  labor: null, machine: null, additionalDirectCosts: [],
  totals: { materialCostBeforeWastage: 10, materialWastageCost: 0, materialsTotalCost: 10, laborCost: 0, machineCost: 0, additionalDirectCosts: 0, totalEstimatedCost: 10, outputQuantity: 1, estimatedUnitCost: 10 },
}

function renderPage() { return render(<MemoryRouter><ProductsPage /></MemoryRouter>) }

describe('ProductsPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset(); api.put.mockReset()
    sessionState.roles = ['Owner']
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/costing/supplies')) return Promise.resolve({ ok: true, data: supplies })
      if (path.startsWith('/api/products?')) return Promise.resolve(emptyListResponse)
      if (path === '/api/products/p1') return Promise.resolve({ ok: true, data: product })
      if (path === '/api/products/p1/cost') return Promise.resolve({ ok: true, data: cost })
      return Promise.resolve(emptyListResponse)
    })
  })

  it('loads the active product list by default', async () => {
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalledWith(expect.stringContaining('status=active')))
  })

  it('creates a product and immediately opens it for recipe editing', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: product })
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalled())

    await user.type(screen.getByLabelText('Código'), 'mini-vase')
    await user.type(screen.getByLabelText('Nome'), 'Mini Vaso')
    await user.click(screen.getByRole('button', { name: 'Criar produto' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/products', { code: 'mini-vase', name: 'Mini Vaso', description: null }))
    expect(await screen.findByRole('heading', { name: /Receita \(BOM\) de Mini Vaso/ })).toBeInTheDocument()
  })

  it('adds a multi-material recipe and submits the exact PUT payload', async () => {
    const user = userEvent.setup()
    renderPage()
    await user.type(screen.getByLabelText('Código'), 'MINI-VASE')
    await user.type(screen.getByLabelText('Nome'), 'Mini Vaso')
    api.post.mockResolvedValueOnce({ ok: true, data: product })
    await user.click(screen.getByRole('button', { name: 'Criar produto' }))
    await screen.findByRole('heading', { name: /Receita/ })

    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    const pickers = screen.getAllByLabelText('Insumo')
    await user.selectOptions(pickers[0], 's1')
    await user.selectOptions(pickers[1], 's2')
    const quantities = screen.getAllByLabelText('Quantidade')
    await user.type(quantities[0], '100')
    await user.type(quantities[1], '2')

    const updated = { ...product, version: 2, recipe: { ...product.recipe, materialLines: [] } }
    api.put.mockResolvedValue({ ok: true, data: updated })
    await user.click(screen.getByRole('button', { name: 'Salvar receita' }))

    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/products/p1/recipe', expect.objectContaining({
      materialLines: [
        expect.objectContaining({ supplyId: 's1', quantity: 100 }),
        expect.objectContaining({ supplyId: 's2', quantity: 2 }),
      ],
      productVersion: 1,
    })))
  })

  it('keeps a selected supply visible in its own dropdown even after the search narrows past it (NB-2 regression)', async () => {
    const user = userEvent.setup()
    renderPage()
    await user.type(screen.getByLabelText('Código'), 'MINI-VASE')
    await user.type(screen.getByLabelText('Nome'), 'Mini Vaso')
    api.post.mockResolvedValueOnce({ ok: true, data: product })
    await user.click(screen.getByRole('button', { name: 'Criar produto' }))
    await screen.findByRole('heading', { name: /Receita/ })

    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    await user.selectOptions(screen.getByLabelText('Insumo'), 's1')
    expect((screen.getByLabelText('Insumo') as HTMLSelectElement).value).toBe('s1')

    // Narrow the search so the server would only return a DIFFERENT supply — the already
    // selected line must not silently lose its value or its visible label.
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/costing/supplies')) return Promise.resolve({ ok: true, data: [supplies[1]] })
      return Promise.resolve(emptyListResponse)
    })
    await user.type(screen.getByLabelText('Buscar insumo'), 'Caixa')
    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith('/api/costing/supplies?includeInactive=false&search=Caixa', expect.any(AbortSignal)))

    const picker = screen.getByLabelText('Insumo') as HTMLSelectElement
    expect(picker.value).toBe('s1')
    expect(screen.getByRole('option', { name: /FIL-PLA/ })).toBeInTheDocument()
  })

  it('calculates and renders the current product cost by reusing the CostEngine result shape', async () => {
    const user = userEvent.setup()
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/costing/supplies')) return Promise.resolve({ ok: true, data: supplies })
      if (path === '/api/products/p1') return Promise.resolve({ ok: true, data: product })
      if (path === '/api/products/p1/cost') return Promise.resolve({ ok: true, data: cost })
      return Promise.resolve(emptyListResponse)
    })
    renderPage()
    await user.type(screen.getByLabelText('Código'), 'MINI-VASE')
    await user.type(screen.getByLabelText('Nome'), 'Mini Vaso')
    api.post.mockResolvedValueOnce({ ok: true, data: product })
    await user.click(screen.getByRole('button', { name: 'Criar produto' }))
    await screen.findByRole('heading', { name: /Receita/ })

    await user.click(screen.getByRole('button', { name: 'Calcular custo atual' }))
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/products/p1/cost'))
    expect(await screen.findByText('R$ 10,00 por unidade')).toBeInTheDocument()
  })

  it('rejects an empty material selection before submitting the recipe', async () => {
    const user = userEvent.setup()
    renderPage()
    await user.type(screen.getByLabelText('Código'), 'MINI-VASE')
    await user.type(screen.getByLabelText('Nome'), 'Mini Vaso')
    api.post.mockResolvedValueOnce({ ok: true, data: product })
    await user.click(screen.getByRole('button', { name: 'Criar produto' }))
    await screen.findByRole('heading', { name: /Receita/ })

    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    const submit = screen.getByRole('button', { name: 'Salvar receita' })
    // Browser-native `required` validation on the still-empty Insumo select would otherwise
    // block the submit event outright; bypass it to exercise the component's own guard.
    submit.closest('form')!.noValidate = true
    await user.click(submit)
    expect(await screen.findByRole('status')).toHaveTextContent(/Selecione um insumo/)
    expect(api.put).not.toHaveBeenCalled()
  })

  it('toggles a product between active and inactive', async () => {
    const user = userEvent.setup()
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/costing/supplies')) return Promise.resolve({ ok: true, data: supplies })
      if (path === '/api/products/p1') return Promise.resolve({ ok: true, data: product })
      return Promise.resolve(emptyListResponse)
    })
    api.post.mockImplementation((path: string) => {
      if (path === '/api/products/p1/deactivate?version=1') return Promise.resolve({ ok: true, data: undefined })
      return Promise.resolve({ ok: true, data: product })
    })
    renderPage()
    await user.type(screen.getByLabelText('Código'), 'MINI-VASE')
    await user.type(screen.getByLabelText('Nome'), 'Mini Vaso')
    await user.click(screen.getByRole('button', { name: 'Criar produto' }))
    await screen.findByRole('heading', { name: /Receita/ })

    await user.click(screen.getByRole('button', { name: 'Desativar produto' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/products/p1/deactivate?version=1'))
  })

  it('hides management controls for a Viewer', async () => {
    sessionState.roles = ['Viewer']
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalled())
    expect(screen.queryByRole('button', { name: 'Novo produto' })).not.toBeInTheDocument()
  })
})
