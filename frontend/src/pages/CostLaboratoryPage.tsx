import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { apiClient } from '../api/client'
import type {
  CostCalculationRequest,
  CostCalculationResult,
  CostingAdditionalDirectCostRequest,
  CostingMaterialRequest,
  CostingSupplyListItemResponse,
  SupplyBaseUnit,
} from '../api/types'

type MaterialDraft = {
  key: number
  supplyId: string
  quantity: string
  enteredUnit: SupplyBaseUnit
  wastagePercentOverride: string
  manualUnitCostOverride: string
}

type AdditionalDraft = { key: number; description: string; amount: string }

const UNIT_LABELS: Record<SupplyBaseUnit, string> = {
  Gram: 'g', Kilogram: 'kg', Unit: 'un', Milliliter: 'mL', Liter: 'L', Meter: 'm', Centimeter: 'cm',
}
const COMPATIBLE_UNITS: Record<SupplyBaseUnit, SupplyBaseUnit[]> = {
  Gram: ['Gram', 'Kilogram'], Kilogram: ['Kilogram', 'Gram'], Unit: ['Unit'],
  Milliliter: ['Milliliter', 'Liter'], Liter: ['Liter', 'Milliliter'],
  Meter: ['Meter', 'Centimeter'], Centimeter: ['Centimeter', 'Meter'],
}

let nextKey = 1
const materialDraft = (): MaterialDraft => ({
  key: nextKey++, supplyId: '', quantity: '', enteredUnit: 'Gram', wastagePercentOverride: '', manualUnitCostOverride: '',
})
const additionalDraft = (): AdditionalDraft => ({ key: nextKey++, description: '', amount: '' })

function numberOrNull(value: string): number | null { return value.trim() === '' ? null : Number(value) }
function money(value: number | string): string {
  return Number(value).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 2 })
}
function unitCost(value: number | string): string {
  return Number(value).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 6 })
}
function decimal(value: number | string, digits = 4): string {
  return Number(value).toLocaleString('pt-BR', { maximumFractionDigits: digits })
}

export function CostLaboratoryPage() {
  const [supplies, setSupplies] = useState<CostingSupplyListItemResponse[]>([])
  const [supplySearch, setSupplySearch] = useState('')
  const [materials, setMaterials] = useState<MaterialDraft[]>([])
  const [defaultWastage, setDefaultWastage] = useState('')
  const [laborMinutes, setLaborMinutes] = useState('')
  const [laborRate, setLaborRate] = useState('')
  const [machineMinutes, setMachineMinutes] = useState('')
  const [machineRate, setMachineRate] = useState('')
  const [additional, setAdditional] = useState<AdditionalDraft[]>([])
  const [outputQuantity, setOutputQuantity] = useState('1')
  const [result, setResult] = useState<CostCalculationResult | null>(null)
  const [message, setMessage] = useState('')
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const search = supplySearch.trim()
      const query = search ? `&search=${encodeURIComponent(search)}` : ''
      void apiClient.get<CostingSupplyListItemResponse[]>(`/api/costing/supplies?includeInactive=false${query}`, controller.signal)
      .then(response => {
        if (response.ok) setSupplies(response.data)
        else setMessage(response.error.safeMessage)
      })
    }, 250)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [supplySearch])

  const supplyById = useMemo(() => new Map(supplies.map(supply => [supply.id, supply])), [supplies])

  function updateMaterial(key: number, change: Partial<MaterialDraft>) {
    setMaterials(current => current.map(line => {
      if (line.key !== key) return line
      const updated = { ...line, ...change }
      if (change.supplyId) {
        const selected = supplyById.get(change.supplyId)
        if (selected) updated.enteredUnit = selected.baseUnit
      }
      return updated
    }))
  }

  function validate(): string | null {
    if (!Number.isInteger(Number(outputQuantity)) || Number(outputQuantity) < 1) return 'A quantidade produzida deve ser um inteiro maior ou igual a 1.'
    for (const line of materials) {
      if (!line.supplyId) return 'Selecione um insumo em cada linha de material.'
      if (!(Number(line.quantity) > 0)) return 'A quantidade de cada material deve ser maior que zero.'
      const wastage = numberOrNull(line.wastagePercentOverride)
      if (wastage != null && (wastage < 0 || wastage > 100)) return 'A perda deve ficar entre 0% e 100%.'
      const override = numberOrNull(line.manualUnitCostOverride)
      if (override != null && override < 0) return 'O custo manual não pode ser negativo.'
    }
    const scenarioWastage = numberOrNull(defaultWastage)
    if (scenarioWastage != null && (scenarioWastage < 0 || scenarioWastage > 100)) return 'A perda padrão do cenário deve ficar entre 0% e 100%.'
    if (Number(laborMinutes || 0) < 0 || Number(laborRate || 0) < 0 || Number(machineMinutes || 0) < 0 || Number(machineRate || 0) < 0)
      return 'Tempos e taxas não podem ser negativos.'
    if (additional.some(line => !line.description.trim() || Number(line.amount) < 0)) return 'Informe descrição e valor não negativo para cada custo adicional.'
    const hasComponent = materials.length > 0 || Number(laborMinutes) > 0 || Number(machineMinutes) > 0 || additional.some(line => Number(line.amount) > 0)
    if (!hasComponent) return 'Inclua pelo menos um componente de custo antes de calcular.'
    return null
  }

  async function calculate(event: FormEvent) {
    event.preventDefault()
    setMessage('')
    setResult(null)
    const invalid = validate()
    if (invalid) { setMessage(invalid); return }

    const materialRequests: CostingMaterialRequest[] = materials.map(line => ({
      supplyId: line.supplyId,
      quantity: Number(line.quantity),
      enteredUnit: line.enteredUnit,
      wastagePercentOverride: numberOrNull(line.wastagePercentOverride),
      manualUnitCostOverride: numberOrNull(line.manualUnitCostOverride),
    }))
    const additionalRequests: CostingAdditionalDirectCostRequest[] = additional.map(line => ({
      description: line.description.trim(), amount: Number(line.amount || 0),
    }))
    const request: CostCalculationRequest = {
      materials: materialRequests,
      defaultWastagePercent: numberOrNull(defaultWastage),
      labor: laborMinutes.trim() === '' ? null : { minutes: Number(laborMinutes), manualHourlyRateOverride: numberOrNull(laborRate) },
      machine: machineMinutes.trim() === '' && machineRate.trim() === '' ? null : { minutes: Number(machineMinutes || 0), hourlyRate: Number(machineRate || 0) },
      additionalDirectCosts: additionalRequests,
      outputQuantity: Number(outputQuantity),
      includeInactiveSupplies: false,
    }
    setLoading(true)
    const response = await apiClient.post<CostCalculationResult>('/api/costing/calculate', request)
    setLoading(false)
    if (response.ok) setResult(response.data)
    else setMessage(response.error.safeMessage)
  }

  return (
    <main className="product-page cost-lab">
      <div className="page-heading">
        <Link to="/">← Início</Link>
        <h1>Laboratório de Custos</h1>
        <p>Simule materiais, mão de obra, máquina e custos diretos. O servidor resolve toda a matemática e não movimenta estoque.</p>
      </div>

      <form onSubmit={calculate} className="cost-lab__form">
        <section className="card" aria-labelledby="materials-heading">
          <div className="section-heading"><div><h2 id="materials-heading">Materiais</h2><p>O custo sugerido usa a média ponderada das compras registradas.</p></div><button type="button" onClick={() => setMaterials(lines => [...lines, materialDraft()])}>Adicionar material</button></div>
          <label htmlFor="supply-search">Buscar insumo</label>
          <input id="supply-search" value={supplySearch} onChange={event => setSupplySearch(event.target.value)} placeholder="Código ou nome" />
          {supplies.length === 100 && <p className="search-hint">Muitos resultados encontrados. Refine a busca.</p>}
          <label htmlFor="scenario-wastage">Perda padrão do cenário (%)</label>
          <input id="scenario-wastage" type="number" min="0" max="100" step="any" value={defaultWastage} onChange={event => setDefaultWastage(event.target.value)} placeholder="Usar configuração do sistema" />
          {materials.length === 0 && <p className="empty-state">Nenhum material adicionado.</p>}
          <div className="cost-lines">
            {materials.map((line, index) => {
              const selected = supplyById.get(line.supplyId)
              const units = selected ? COMPATIBLE_UNITS[selected.baseUnit] : (['Gram'] as SupplyBaseUnit[])
              return <fieldset className="cost-line" key={line.key}>
                <legend>Material {index + 1}</legend>
                <label htmlFor={`material-${line.key}`}>Insumo</label>
                <select id={`material-${line.key}`} value={line.supplyId} onChange={event => updateMaterial(line.key, { supplyId: event.target.value })} required>
                  <option value="">Selecione</option>
                  {supplies.map(supply => <option key={supply.id} value={supply.id}>{supply.code} · {supply.name}</option>)}
                </select>
                <div className="inline-form">
                  <div><label htmlFor={`quantity-${line.key}`}>Quantidade</label><input id={`quantity-${line.key}`} type="number" min="0" step="any" value={line.quantity} onChange={event => updateMaterial(line.key, { quantity: event.target.value })} required /></div>
                  <div><label htmlFor={`unit-${line.key}`}>Unidade informada</label><select id={`unit-${line.key}`} value={line.enteredUnit} onChange={event => updateMaterial(line.key, { enteredUnit: event.target.value as SupplyBaseUnit })}>{units.map(unit => <option key={unit} value={unit}>{UNIT_LABELS[unit]}</option>)}</select></div>
                </div>
                <div className="inline-form">
                  <div><label htmlFor={`wastage-${line.key}`}>Perda desta linha (%)</label><input id={`wastage-${line.key}`} type="number" min="0" max="100" step="any" value={line.wastagePercentOverride} onChange={event => updateMaterial(line.key, { wastagePercentOverride: event.target.value })} placeholder="Herdar" /></div>
                  <div><label htmlFor={`override-${line.key}`}>Custo manual por unidade-base (R$)</label><input id={`override-${line.key}`} type="number" min="0" step="any" value={line.manualUnitCostOverride} onChange={event => updateMaterial(line.key, { manualUnitCostOverride: event.target.value })} placeholder="Usar média das compras" /></div>
                </div>
                {selected && <div className="basis-summary">
                  <span>Unidade-base: {UNIT_LABELS[selected.baseUnit]}</span>
                  <span>Estoque: {decimal(selected.currentStockBaseUnit)} {UNIT_LABELS[selected.baseUnit]}</span>
                  <span>{selected.costBasisAvailable ? `Custo médio: ${unitCost(selected.weightedAverageUnitCost ?? 0)} / ${UNIT_LABELS[selected.baseUnit]}` : 'Sem base de aquisição — informe custo manual'}</span>
                  <small>Política: {selected.policy}</small>
                </div>}
                {line.manualUnitCostOverride !== '' && <p className="simulation-note">Simulação: este custo manual não altera o cadastro nem o histórico do insumo.</p>}
                <button type="button" className="button-danger" onClick={() => setMaterials(lines => lines.filter(item => item.key !== line.key))}>Remover material {index + 1}</button>
              </fieldset>
            })}
          </div>
        </section>

        <div className="split-grid">
          <section className="card" aria-labelledby="labor-heading"><h2 id="labor-heading">Mão de obra</h2><label htmlFor="labor-minutes">Minutos</label><input id="labor-minutes" type="number" min="0" step="any" value={laborMinutes} onChange={event => setLaborMinutes(event.target.value)} /><label htmlFor="labor-rate">Taxa manual por hora (R$)</label><input id="labor-rate" type="number" min="0" step="any" value={laborRate} onChange={event => setLaborRate(event.target.value)} placeholder="Usar configuração do sistema" /></section>
          <section className="card" aria-labelledby="machine-heading"><h2 id="machine-heading">Máquina</h2><label htmlFor="machine-minutes">Minutos</label><input id="machine-minutes" type="number" min="0" step="any" value={machineMinutes} onChange={event => setMachineMinutes(event.target.value)} /><label htmlFor="machine-rate">Taxa por hora (R$)</label><input id="machine-rate" type="number" min="0" step="any" value={machineRate} onChange={event => setMachineRate(event.target.value)} /></section>
        </div>

        <section className="card" aria-labelledby="additional-heading">
          <div className="section-heading"><h2 id="additional-heading">Custos adicionais</h2><button type="button" onClick={() => setAdditional(lines => [...lines, additionalDraft()])}>Adicionar custo</button></div>
          {additional.map((line, index) => <div className="inline-form" key={line.key}><div><label htmlFor={`additional-description-${line.key}`}>Descrição {index + 1}</label><input id={`additional-description-${line.key}`} value={line.description} onChange={event => setAdditional(lines => lines.map(item => item.key === line.key ? { ...item, description: event.target.value } : item))} /></div><div><label htmlFor={`additional-amount-${line.key}`}>Valor (R$)</label><input id={`additional-amount-${line.key}`} type="number" min="0" step="any" value={line.amount} onChange={event => setAdditional(lines => lines.map(item => item.key === line.key ? { ...item, amount: event.target.value } : item))} /></div><button type="button" className="button-danger" onClick={() => setAdditional(lines => lines.filter(item => item.key !== line.key))}>Remover</button></div>)}
        </section>

        <section className="card cost-lab__action"><label htmlFor="output-quantity">Quantidade produzida</label><input id="output-quantity" type="number" min="1" step="1" value={outputQuantity} onChange={event => setOutputQuantity(event.target.value)} required /><button className="button-primary" type="submit" disabled={loading}>{loading ? 'Calculando…' : 'Calcular custo estimado'}</button></section>
      </form>

      {message && <p role="status" className="form-error">{message}</p>}
      {result && <CostResult result={result} />}
    </main>
  )
}

function CostResult({ result }: { result: CostCalculationResult }) {
  const totals = result.totals
  return <section className="card cost-result" aria-labelledby="result-heading">
    <div className="section-heading"><div><h2 id="result-heading">Resultado</h2><p>Motor {result.engineVersion}</p></div><strong className="unit-cost">{money(totals.estimatedUnitCost)} por unidade</strong></div>
    {result.materials.map((line, index) => <article className="result-line" key={`${line.supplyId}-${index}`}>
      <h3>{line.supplyCode} · {line.supplyName}</h3>
      <p>{decimal(line.enteredQuantity, 8)} {UNIT_LABELS[line.enteredUnit as SupplyBaseUnit] ?? line.enteredUnit} → {decimal(line.normalizedQuantityBaseUnit)} {UNIT_LABELS[line.baseUnit as SupplyBaseUnit] ?? line.baseUnit}; com {decimal(line.wastagePercent)}% de perda: {decimal(line.effectiveQuantityBaseUnit)} {UNIT_LABELS[line.baseUnit as SupplyBaseUnit] ?? line.baseUnit}.</p>
      <p>{money(line.costBeforeWastage)} + {money(line.wastageCost)} de perda = <strong>{money(line.costAfterWastage)}</strong></p>
      <small>Fonte: {line.costSource === 'MANUAL_OVERRIDE' ? 'override manual de simulação' : 'média ponderada das aquisições'} · {unitCost(line.unitCostBaseUnit)} por {UNIT_LABELS[line.baseUnit as SupplyBaseUnit] ?? line.baseUnit}</small>
      {line.exceedsCurrentStock && <p className="stock-warning">A quantidade efetiva excede o estoque atual de {decimal(line.currentStockBaseUnit)} {UNIT_LABELS[line.baseUnit as SupplyBaseUnit] ?? line.baseUnit}. O cálculo foi mantido e nenhum estoque foi alterado.</p>}
    </article>)}
    <dl className="totals-grid">
      <div><dt>Materiais antes da perda</dt><dd>{money(totals.materialCostBeforeWastage)}</dd></div>
      <div><dt>Custo da perda</dt><dd>{money(totals.materialWastageCost)}</dd></div>
      <div><dt>Materiais com perda</dt><dd>{money(totals.materialsTotalCost)}</dd></div>
      <div><dt>Mão de obra</dt><dd>{money(totals.laborCost)}</dd></div>
      <div><dt>Máquina</dt><dd>{money(totals.machineCost)}</dd></div>
      <div><dt>Custos adicionais</dt><dd>{money(totals.additionalDirectCosts)}</dd></div>
      <div className="totals-grid__total"><dt>Total estimado do lote ({totals.outputQuantity} un.)</dt><dd>{money(totals.totalEstimatedCost)}</dd></div>
      <div className="totals-grid__total"><dt>Custo estimado unitário</dt><dd>{money(totals.estimatedUnitCost)}</dd></div>
    </dl>
  </section>
}
