import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CostLaboratoryPage } from './CostLaboratoryPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const supplies = [
  { id: 's1', code: 'FIL-PLA', name: 'PLA Preto', baseUnit: 'Gram', active: true, currentStockBaseUnit: 100, policy: 'WEIGHTED_AVERAGE_ACQUISITION', weightedAverageUnitCost: 0.116667, costBasisAvailable: true },
  { id: 's2', code: 'EMB-BOX', name: 'Caixa', baseUnit: 'Unit', active: true, currentStockBaseUnit: 10, policy: 'WEIGHTED_AVERAGE_ACQUISITION', weightedAverageUnitCost: null, costBasisAvailable: false },
]

const calculation = {
  engineVersion: '1.0.0',
  materials: [{ supplyId: 's1', supplyCode: 'FIL-PLA', supplyName: 'PLA Preto', enteredQuantity: 100, enteredUnit: 'Gram', normalizedQuantityBaseUnit: 100, baseUnit: 'Gram', wastagePercent: 5, effectiveQuantityBaseUnit: 105, costSource: 'WEIGHTED_AVERAGE_ACQUISITION', costPolicy: 'WEIGHTED_AVERAGE_ACQUISITION', unitCostBaseUnit: 0.1, costBeforeWastage: 10, wastageCost: 0.5, costAfterWastage: 10.5, currentStockBaseUnit: 100, exceedsCurrentStock: true, warnings: ['REQUESTED_QUANTITY_EXCEEDS_CURRENT_STOCK'] }],
  labor: { minutes: 30, hourlyRate: 40, rateSource: 'MANUAL_OVERRIDE', cost: 20 },
  machine: { minutes: 120, hourlyRate: 3, cost: 6 },
  additionalDirectCosts: [{ description: 'Acabamento', amount: 13.5 }],
  totals: { materialCostBeforeWastage: 10, materialWastageCost: 0.5, materialsTotalCost: 10.5, laborCost: 20, machineCost: 6, additionalDirectCosts: 13.5, totalEstimatedCost: 50, outputQuantity: 10, estimatedUnitCost: 5 },
}

function renderPage() { return render(<MemoryRouter><CostLaboratoryPage /></MemoryRouter>) }

describe('CostLaboratoryPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset()
    api.get.mockResolvedValue({ ok: true, data: supplies })
  })

  it('loads only the active costing picker and explains acquisition basis', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/costing/supplies?includeInactive=false', expect.any(AbortSignal)))
    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    await user.selectOptions(screen.getByLabelText('Insumo'), 's1')
    expect(screen.getByText(/Custo médio: R\$ 0,116667 \/ g/)).toBeInTheDocument()
    expect(screen.getByText(/Política: WEIGHTED_AVERAGE_ACQUISITION/)).toBeInTheDocument()
  })

  it('submits a multi-component scenario and renders the server breakdown', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: calculation })
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalled())

    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    await user.selectOptions(screen.getByLabelText('Insumo'), 's1')
    await user.type(screen.getByLabelText('Quantidade'), '100')
    await user.type(screen.getByLabelText('Perda desta linha (%)'), '5')
    await user.type(screen.getByLabelText('Minutos', { selector: '#labor-minutes' }), '30')
    await user.type(screen.getByLabelText('Taxa manual por hora (R$)'), '40')
    await user.type(screen.getByLabelText('Minutos', { selector: '#machine-minutes' }), '120')
    await user.type(screen.getByLabelText('Taxa por hora (R$)'), '3')
    await user.click(screen.getByRole('button', { name: 'Adicionar custo' }))
    await user.type(screen.getByLabelText('Descrição 1'), 'Acabamento')
    await user.type(screen.getByLabelText('Valor (R$)'), '13.5')
    await user.clear(screen.getByLabelText('Quantidade produzida'))
    await user.type(screen.getByLabelText('Quantidade produzida'), '10')
    await user.click(screen.getByRole('button', { name: 'Calcular custo estimado' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/costing/calculate', expect.objectContaining({
      outputQuantity: 10,
      materials: [expect.objectContaining({ supplyId: 's1', quantity: 100, wastagePercentOverride: 5 })],
      labor: { minutes: 30, manualHourlyRateOverride: 40 },
      machine: { minutes: 120, hourlyRate: 3 },
      additionalDirectCosts: [{ description: 'Acabamento', amount: 13.5 }],
    })))
    const result = await screen.findByRole('heading', { name: 'Resultado' })
    const card = result.closest('section')!
    expect(within(card).getByText('R$ 5,00 por unidade')).toBeInTheDocument()
    expect(within(card).getByText(/excede o estoque atual/)).toBeInTheDocument()
  })

  it('marks manual cost as a simulation and supports a supply without acquisition basis', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: { ...calculation, materials: [{ ...calculation.materials[0], supplyId: 's2', supplyCode: 'EMB-BOX', supplyName: 'Caixa', baseUnit: 'Unit', enteredUnit: 'Unit', costSource: 'MANUAL_OVERRIDE', costPolicy: 'MANUAL_OVERRIDE', exceedsCurrentStock: false, warnings: [] }] } })
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalled())
    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    await user.selectOptions(screen.getByLabelText('Insumo'), 's2')
    expect(screen.getByText(/Sem base de aquisição/)).toBeInTheDocument()
    await user.type(screen.getByLabelText('Quantidade'), '1')
    await user.type(screen.getByLabelText('Custo manual por unidade-base (R$)'), '2.5')
    expect(screen.getByText(/não altera o cadastro nem o histórico/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Calcular custo estimado' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/costing/calculate', expect.objectContaining({ materials: [expect.objectContaining({ supplyId: 's2', manualUnitCostOverride: 2.5 })] })))
  })

  it('rejects an empty scenario before calling the API', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalled())
    await user.click(screen.getByRole('button', { name: 'Calcular custo estimado' }))
    expect(await screen.findByRole('status')).toHaveTextContent(/pelo menos um componente/)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('searches on the server and keeps separate material selections stable', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalled())
    await user.type(screen.getByLabelText('Buscar insumo'), 'Caixa')
    await waitFor(() => expect(api.get).toHaveBeenLastCalledWith('/api/costing/supplies?includeInactive=false&search=Caixa', expect.any(AbortSignal)))
    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    await user.click(screen.getByRole('button', { name: 'Adicionar material' }))
    const pickers = screen.getAllByLabelText('Insumo')
    await user.selectOptions(pickers[0], 's1')
    await user.selectOptions(pickers[1], 's2')
    expect((pickers[0] as HTMLSelectElement).value).toBe('s1')
    expect((pickers[1] as HTMLSelectElement).value).toBe('s2')
  })

  it('shows a transparent refinement hint at the server result cap', async () => {
    api.get.mockResolvedValue({ ok: true, data: Array.from({ length: 100 }, (_, index) => ({ ...supplies[0], id: `s${index}` })) })
    renderPage()
    expect(await screen.findByText('Muitos resultados encontrados. Refine a busca.')).toBeInTheDocument()
  })
})
