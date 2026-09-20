import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { apiClient } from '../api/client'
import { useSession } from '../auth/useSession'
import type {
  CostCalculationResult, CostingSupplyListItemResponse, ProductListItemResponse, ProductListResponse,
  ProductListStatus, ProductRecipeAdditionalCostLineRequest, ProductRecipeMaterialLineRequest, ProductResponse,
  SupplyBaseUnit,
} from '../api/types'

const PAGE_SIZE = 10
const UNIT_LABELS: Record<SupplyBaseUnit, string> = {
  Gram: 'g', Kilogram: 'kg', Unit: 'un', Milliliter: 'mL', Liter: 'L', Meter: 'm', Centimeter: 'cm',
}
const COMPATIBLE_UNITS: Record<SupplyBaseUnit, SupplyBaseUnit[]> = {
  Gram: ['Gram', 'Kilogram'], Kilogram: ['Kilogram', 'Gram'], Unit: ['Unit'],
  Milliliter: ['Milliliter', 'Liter'], Liter: ['Liter', 'Milliliter'],
  Meter: ['Meter', 'Centimeter'], Centimeter: ['Centimeter', 'Meter'],
}

type MaterialDraft = {
  key: number
  supplyId: string
  supplyLabel: string // remembered even if the supply falls out of the current search results
  quantity: string
  enteredUnit: SupplyBaseUnit
  wastagePercentOverride: string
  manualUnitCostOverride: string
}
type AdditionalDraft = { key: number; description: string; amount: string }

let nextKey = 1
const materialDraft = (): MaterialDraft => ({
  key: nextKey++, supplyId: '', supplyLabel: '', quantity: '', enteredUnit: 'Gram', wastagePercentOverride: '', manualUnitCostOverride: '',
})
const additionalDraft = (): AdditionalDraft => ({ key: nextKey++, description: '', amount: '' })

function numberOrNull(value: string): number | null { return value.trim() === '' ? null : Number(value) }
function money(value: number | string): string {
  return Number(value).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 2 })
}
function decimal(value: number | string, digits = 4): string {
  return Number(value).toLocaleString('pt-BR', { maximumFractionDigits: digits })
}

type ProductForm = { code: string; name: string; description: string }
const blankProductForm: ProductForm = { code: '', name: '', description: '' }

type RecipeForm = {
  wastagePercentOverride: string; laborMinutes: string; laborHourlyRateOverride: string
  machineMinutes: string; machineHourlyRate: string; outputQuantity: string; notes: string
  materialLines: MaterialDraft[]; additionalCostLines: AdditionalDraft[]
}
const blankRecipeForm: RecipeForm = {
  wastagePercentOverride: '', laborMinutes: '', laborHourlyRateOverride: '', machineMinutes: '', machineHourlyRate: '',
  outputQuantity: '1', notes: '', materialLines: [], additionalCostLines: [],
}

function toRecipeForm(product: ProductResponse): RecipeForm {
  const recipe = product.recipe
  return {
    wastagePercentOverride: recipe.wastagePercentOverride?.toString() ?? '',
    laborMinutes: recipe.laborMinutes?.toString() ?? '',
    laborHourlyRateOverride: recipe.laborHourlyRateOverride?.toString() ?? '',
    machineMinutes: recipe.machineMinutes?.toString() ?? '',
    machineHourlyRate: recipe.machineHourlyRate?.toString() ?? '',
    outputQuantity: recipe.outputQuantity.toString(),
    notes: recipe.notes ?? '',
    materialLines: recipe.materialLines.map(line => ({
      key: nextKey++, supplyId: line.supplyId, supplyLabel: `${line.supplyCode} · ${line.supplyName}`,
      quantity: line.enteredQuantity.toString(), enteredUnit: line.enteredUnit,
      wastagePercentOverride: line.wastagePercentOverride?.toString() ?? '', manualUnitCostOverride: line.manualUnitCostOverride?.toString() ?? '',
    })),
    additionalCostLines: recipe.additionalCostLines.map(line => ({ key: nextKey++, description: line.description, amount: line.amount.toString() })),
  }
}

export function ProductsPage() {
  const { session } = useSession()
  const roles = session.status === 'authenticated' ? session.user.roles : []
  const canManage = roles.includes('Owner') || roles.includes('Operator')

  const [items, setItems] = useState<ProductListItemResponse[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<ProductListStatus>('active')
  const [selected, setSelected] = useState<ProductResponse | null>(null)
  const [form, setForm] = useState<ProductForm>(blankProductForm)
  const [recipeForm, setRecipeForm] = useState<RecipeForm>(blankRecipeForm)
  const [message, setMessage] = useState('')
  const [cost, setCost] = useState<CostCalculationResult | null>(null)
  const [costMessage, setCostMessage] = useState('')

  const [supplies, setSupplies] = useState<CostingSupplyListItemResponse[]>([])
  const [supplySearch, setSupplySearch] = useState('')

  async function load(targetPage: number, overrides: { status?: ProductListStatus } = {}) {
    const targetStatus = overrides.status ?? status
    const params = new URLSearchParams({ page: String(targetPage), pageSize: String(PAGE_SIZE), status: targetStatus })
    if (search) params.set('search', search)
    const result = await apiClient.get<ProductListResponse>(`/api/products?${params}`)
    if (result.ok) { setItems(result.data.items); setTotal(Number(result.data.total)); setPage(Number(result.data.page)) }
    else setMessage(result.error.safeMessage)
  }

  // oxlint-disable-next-line react/set-state-in-effect, react-hooks/exhaustive-deps
  useEffect(() => { void load(1) }, [])

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const term = supplySearch.trim()
      const query = term ? `&search=${encodeURIComponent(term)}` : ''
      void apiClient.get<CostingSupplyListItemResponse[]>(`/api/costing/supplies?includeInactive=false${query}`, controller.signal)
        .then(response => { if (response.ok) setSupplies(response.data) })
    }, 250)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [supplySearch])

  const supplyById = useMemo(() => new Map(supplies.map(supply => [supply.id, supply])), [supplies])

  async function open(id: string) {
    const result = await apiClient.get<ProductResponse>(`/api/products/${id}`)
    if (result.ok) {
      setSelected(result.data)
      setForm({ code: result.data.code, name: result.data.name, description: result.data.description ?? '' })
      setRecipeForm(toRecipeForm(result.data))
      setCost(null); setCostMessage(''); setMessage('')
    } else setMessage(result.error.safeMessage)
  }

  function newProduct() {
    setSelected(null)
    setForm(blankProductForm)
    setRecipeForm(blankRecipeForm)
    setCost(null); setCostMessage(''); setMessage('')
  }

  async function submitProduct(event: FormEvent) {
    event.preventDefault()
    if (selected) {
      const result = await apiClient.put<ProductResponse>(`/api/products/${selected.id}`, { name: form.name, description: form.description || null, version: selected.version })
      if (result.ok) { setMessage('Produto atualizado com sucesso.'); await open(selected.id); await load(page) }
      else setMessage(result.error.safeMessage)
    } else {
      const result = await apiClient.post<ProductResponse>('/api/products', { code: form.code, name: form.name, description: form.description || null })
      if (result.ok) { setMessage('Produto criado com sucesso. Agora defina a receita.'); await open(result.data.id); await load(1) }
      else setMessage(result.error.safeMessage)
    }
  }

  async function toggleActive() {
    if (!selected) return
    const action = selected.active ? 'deactivate' : 'activate'
    const result = await apiClient.post<void>(`/api/products/${selected.id}/${action}?version=${selected.version}`)
    if (result.ok) { setMessage(selected.active ? 'Produto desativado.' : 'Produto reativado.'); await open(selected.id); await load(page) }
    else setMessage(result.error.safeMessage)
  }

  function updateMaterial(key: number, change: Partial<MaterialDraft>) {
    setRecipeForm(current => ({
      ...current,
      materialLines: current.materialLines.map(line => {
        if (line.key !== key) return line
        const updated = { ...line, ...change }
        if (change.supplyId) {
          const chosen = supplyById.get(change.supplyId)
          if (chosen) { updated.enteredUnit = chosen.baseUnit; updated.supplyLabel = `${chosen.code} · ${chosen.name}` }
        }
        return updated
      }),
    }))
  }

  function validateRecipe(): string | null {
    if (!Number.isInteger(Number(recipeForm.outputQuantity)) || Number(recipeForm.outputQuantity) < 1) return 'A quantidade produzida deve ser um inteiro maior ou igual a 1.'
    for (const line of recipeForm.materialLines) {
      if (!line.supplyId) return 'Selecione um insumo em cada linha de material.'
      if (!(Number(line.quantity) > 0)) return 'A quantidade de cada material deve ser maior que zero.'
    }
    if (recipeForm.additionalCostLines.some(line => !line.description.trim() || Number(line.amount) < 0)) return 'Informe descrição e valor não negativo para cada custo adicional.'
    return null
  }

  async function submitRecipe(event: FormEvent) {
    event.preventDefault()
    if (!selected) return
    setMessage('')
    const invalid = validateRecipe()
    if (invalid) { setMessage(invalid); return }

    const materialLines: ProductRecipeMaterialLineRequest[] = recipeForm.materialLines.map(line => ({
      supplyId: line.supplyId, quantity: Number(line.quantity), enteredUnit: line.enteredUnit,
      wastagePercentOverride: numberOrNull(line.wastagePercentOverride), manualUnitCostOverride: numberOrNull(line.manualUnitCostOverride),
    }))
    const additionalCostLines: ProductRecipeAdditionalCostLineRequest[] = recipeForm.additionalCostLines.map(line => ({
      description: line.description.trim(), amount: Number(line.amount || 0),
    }))
    const result = await apiClient.put<ProductResponse>(`/api/products/${selected.id}/recipe`, {
      wastagePercentOverride: numberOrNull(recipeForm.wastagePercentOverride),
      laborMinutes: numberOrNull(recipeForm.laborMinutes),
      laborHourlyRateOverride: numberOrNull(recipeForm.laborHourlyRateOverride),
      machineMinutes: numberOrNull(recipeForm.machineMinutes),
      machineHourlyRate: numberOrNull(recipeForm.machineHourlyRate),
      outputQuantity: Number(recipeForm.outputQuantity),
      notes: recipeForm.notes || null,
      materialLines, additionalCostLines,
      productVersion: selected.version,
    })
    if (result.ok) { setMessage('Receita salva com sucesso.'); setSelected(result.data); setRecipeForm(toRecipeForm(result.data)); await load(page) }
    else setMessage(result.error.safeMessage)
  }

  async function calculateCost() {
    if (!selected) return
    setCostMessage(''); setCost(null)
    const result = await apiClient.get<CostCalculationResult>(`/api/products/${selected.id}/cost`)
    if (result.ok) setCost(result.data)
    else setCostMessage(result.error.safeMessage)
  }

  const lastPage = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <Link to="/">← Início</Link>
          <h1>Produtos e receitas</h1>
          <p>Cadastre produtos, monte a receita (BOM) e calcule o custo atual reutilizando o Laboratório de Custos.</p>
        </div>
        {canManage && <button type="button" onClick={newProduct}>Novo produto</button>}
      </div>

      <div className="split-grid">
        <div className="card">
          <label htmlFor="product-search">Buscar produtos</label>
          <div className="inline-form">
            <input id="product-search" value={search} onChange={e => setSearch(e.target.value)} placeholder="Código ou nome" />
            <button type="button" onClick={() => void load(1)}>Buscar</button>
          </div>
          <label htmlFor="product-status-filter">Situação</label>
          <select id="product-status-filter" value={status} onChange={e => { const value = e.target.value as ProductListStatus; setStatus(value); void load(1, { status: value }) }}>
            <option value="active">Ativos</option>
            <option value="inactive">Inativos</option>
            <option value="all">Todos</option>
          </select>

          <ul className="data-list">
            {items.map(product => (
              <li key={product.id}>
                <button type="button" onClick={() => void open(product.id)}><strong>{product.code}</strong> — {product.name}</button>
                <span className={product.active ? 'badge' : 'badge badge--muted'}>{product.active ? 'Ativo' : 'Inativo'}</span>
              </li>
            ))}
            {items.length === 0 && <li>Nenhum produto encontrado.</li>}
          </ul>
          <nav className="pagination" aria-label="Paginação de produtos">
            <button type="button" disabled={page <= 1} onClick={() => void load(page - 1)}>Anterior</button>
            <span>Página {page} de {lastPage}</span>
            <button type="button" disabled={page >= lastPage} onClick={() => void load(page + 1)}>Próxima</button>
          </nav>
        </div>

        <form className="card form-stack" onSubmit={submitProduct}>
          <h2>{selected ? 'Editar produto' : 'Novo produto'}</h2>
          <label htmlFor="product-code">Código</label>
          <input id="product-code" required disabled={!!selected} value={form.code} onChange={e => setForm({ ...form, code: e.target.value })} placeholder="MINI-VASO" />
          <label htmlFor="product-name">Nome</label>
          <input id="product-name" required minLength={2} value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} />
          <label htmlFor="product-description">Descrição</label>
          <input id="product-description" value={form.description} onChange={e => setForm({ ...form, description: e.target.value })} />
          {canManage && <button className="button-primary" type="submit">{selected ? 'Salvar alterações' : 'Criar produto'}</button>}
          {selected && canManage && <button type="button" onClick={() => void toggleActive()}>{selected.active ? 'Desativar produto' : 'Reativar produto'}</button>}
        </form>
      </div>

      {selected && (
        <form className="card cost-lab__form" onSubmit={submitRecipe} aria-labelledby="recipe-heading">
          <div className="section-heading">
            <div><h2 id="recipe-heading">Receita (BOM) de {selected.name}</h2><p>O custo é calculado pelo mesmo motor do Laboratório de Custos.</p></div>
            {canManage && <button type="button" onClick={() => setRecipeForm(current => ({ ...current, materialLines: [...current.materialLines, materialDraft()] }))}>Adicionar material</button>}
          </div>

          <label htmlFor="recipe-supply-search">Buscar insumo</label>
          <input id="recipe-supply-search" value={supplySearch} onChange={e => setSupplySearch(e.target.value)} placeholder="Código ou nome" disabled={!canManage} />

          {recipeForm.materialLines.length === 0 && <p className="empty-state">Nenhum material na receita.</p>}
          <div className="cost-lines">
            {recipeForm.materialLines.map((line, index) => {
              const chosen = supplyById.get(line.supplyId)
              const units = chosen ? COMPATIBLE_UNITS[chosen.baseUnit] : (['Gram'] as SupplyBaseUnit[])
              // NB-2 fix: the previously selected supply must remain a valid <option> even after
              // the search box narrows the results out from under it — otherwise the select
              // silently shows blank while `supplyId` is still set underneath.
              const options = line.supplyId && !supplies.some(s => s.id === line.supplyId)
                ? [{ id: line.supplyId, code: '', name: line.supplyLabel } as CostingSupplyListItemResponse, ...supplies]
                : supplies
              return <fieldset className="cost-line" key={line.key}>
                <legend>Material {index + 1}</legend>
                <label htmlFor={`recipe-material-${line.key}`}>Insumo</label>
                <select id={`recipe-material-${line.key}`} value={line.supplyId} disabled={!canManage}
                  onChange={e => updateMaterial(line.key, { supplyId: e.target.value })} required>
                  <option value="">Selecione</option>
                  {options.map(supply => <option key={supply.id} value={supply.id}>{supply.code ? `${supply.code} · ${supply.name}` : supply.name}</option>)}
                </select>
                <div className="inline-form">
                  <div><label htmlFor={`recipe-quantity-${line.key}`}>Quantidade</label><input id={`recipe-quantity-${line.key}`} type="number" min="0" step="any" disabled={!canManage} value={line.quantity} onChange={e => updateMaterial(line.key, { quantity: e.target.value })} required /></div>
                  <div><label htmlFor={`recipe-unit-${line.key}`}>Unidade informada</label><select id={`recipe-unit-${line.key}`} value={line.enteredUnit} disabled={!canManage} onChange={e => updateMaterial(line.key, { enteredUnit: e.target.value as SupplyBaseUnit })}>{units.map(unit => <option key={unit} value={unit}>{UNIT_LABELS[unit]}</option>)}</select></div>
                </div>
                <div className="inline-form">
                  <div><label htmlFor={`recipe-wastage-${line.key}`}>Perda desta linha (%)</label><input id={`recipe-wastage-${line.key}`} type="number" min="0" max="100" step="any" disabled={!canManage} value={line.wastagePercentOverride} onChange={e => updateMaterial(line.key, { wastagePercentOverride: e.target.value })} placeholder="Herdar" /></div>
                  <div><label htmlFor={`recipe-override-${line.key}`}>Custo manual por unidade-base (R$)</label><input id={`recipe-override-${line.key}`} type="number" min="0" step="any" disabled={!canManage} value={line.manualUnitCostOverride} onChange={e => updateMaterial(line.key, { manualUnitCostOverride: e.target.value })} placeholder="Usar média das compras" /></div>
                </div>
                {canManage && <button type="button" className="button-danger" onClick={() => setRecipeForm(current => ({ ...current, materialLines: current.materialLines.filter(item => item.key !== line.key) }))}>Remover material {index + 1}</button>}
              </fieldset>
            })}
          </div>

          <div className="split-grid">
            <section className="card" aria-labelledby="recipe-labor-heading">
              <h3 id="recipe-labor-heading">Mão de obra</h3>
              <label htmlFor="recipe-labor-minutes">Minutos</label>
              <input id="recipe-labor-minutes" type="number" min="0" step="any" disabled={!canManage} value={recipeForm.laborMinutes} onChange={e => setRecipeForm({ ...recipeForm, laborMinutes: e.target.value })} />
              <label htmlFor="recipe-labor-rate">Taxa manual por hora (R$)</label>
              <input id="recipe-labor-rate" type="number" min="0" step="any" disabled={!canManage} value={recipeForm.laborHourlyRateOverride} onChange={e => setRecipeForm({ ...recipeForm, laborHourlyRateOverride: e.target.value })} placeholder="Usar configuração do sistema" />
            </section>
            <section className="card" aria-labelledby="recipe-machine-heading">
              <h3 id="recipe-machine-heading">Máquina</h3>
              <label htmlFor="recipe-machine-minutes">Minutos</label>
              <input id="recipe-machine-minutes" type="number" min="0" step="any" disabled={!canManage} value={recipeForm.machineMinutes} onChange={e => setRecipeForm({ ...recipeForm, machineMinutes: e.target.value })} />
              <label htmlFor="recipe-machine-rate">Taxa por hora (R$)</label>
              <input id="recipe-machine-rate" type="number" min="0" step="any" disabled={!canManage} value={recipeForm.machineHourlyRate} onChange={e => setRecipeForm({ ...recipeForm, machineHourlyRate: e.target.value })} />
            </section>
          </div>

          <section className="card" aria-labelledby="recipe-additional-heading">
            <div className="section-heading"><h3 id="recipe-additional-heading">Custos adicionais</h3>{canManage && <button type="button" onClick={() => setRecipeForm(current => ({ ...current, additionalCostLines: [...current.additionalCostLines, additionalDraft()] }))}>Adicionar custo</button>}</div>
            {recipeForm.additionalCostLines.map((line, index) => (
              <div className="inline-form" key={line.key}>
                <div><label htmlFor={`recipe-additional-description-${line.key}`}>Descrição {index + 1}</label><input id={`recipe-additional-description-${line.key}`} disabled={!canManage} value={line.description} onChange={e => setRecipeForm(current => ({ ...current, additionalCostLines: current.additionalCostLines.map(item => item.key === line.key ? { ...item, description: e.target.value } : item) }))} /></div>
                <div><label htmlFor={`recipe-additional-amount-${line.key}`}>Valor (R$)</label><input id={`recipe-additional-amount-${line.key}`} type="number" min="0" step="any" disabled={!canManage} value={line.amount} onChange={e => setRecipeForm(current => ({ ...current, additionalCostLines: current.additionalCostLines.map(item => item.key === line.key ? { ...item, amount: e.target.value } : item) }))} /></div>
                {canManage && <button type="button" className="button-danger" onClick={() => setRecipeForm(current => ({ ...current, additionalCostLines: current.additionalCostLines.filter(item => item.key !== line.key) }))}>Remover</button>}
              </div>
            ))}
          </section>

          <section className="card cost-lab__action">
            <label htmlFor="recipe-output-quantity">Quantidade produzida por lote</label>
            <input id="recipe-output-quantity" type="number" min="1" step="1" disabled={!canManage} value={recipeForm.outputQuantity} onChange={e => setRecipeForm({ ...recipeForm, outputQuantity: e.target.value })} required />
            <label htmlFor="recipe-notes">Notas</label>
            <input id="recipe-notes" disabled={!canManage} value={recipeForm.notes} onChange={e => setRecipeForm({ ...recipeForm, notes: e.target.value })} />
            {canManage && <button className="button-primary" type="submit">Salvar receita</button>}
            <button type="button" onClick={() => void calculateCost()}>Calcular custo atual</button>
          </section>

          {costMessage && <p role="status" className="form-error">{costMessage}</p>}
          {cost && <ProductCostResult result={cost} />}
        </form>
      )}

      {message && <p role="status" className="form-error">{message}</p>}
    </section>
  )
}

function ProductCostResult({ result }: { result: CostCalculationResult }) {
  const totals = result.totals
  return <section className="card cost-result" aria-labelledby="product-cost-heading">
    <div className="section-heading"><div><h3 id="product-cost-heading">Custo atual</h3><p>Motor {result.engineVersion}</p></div><strong className="unit-cost">{money(totals.estimatedUnitCost)} por unidade</strong></div>
    {result.materials.map((line, index) => <article className="result-line" key={`${line.supplyId}-${index}`}>
      <h4>{line.supplyCode} · {line.supplyName}</h4>
      <p>{decimal(line.enteredQuantity, 8)} {line.enteredUnit} → {decimal(line.normalizedQuantityBaseUnit)} {line.baseUnit}; com {decimal(line.wastagePercent)}% de perda: {decimal(line.effectiveQuantityBaseUnit)} {line.baseUnit}.</p>
      <p>{money(line.costBeforeWastage)} + {money(line.wastageCost)} de perda = <strong>{money(line.costAfterWastage)}</strong></p>
    </article>)}
    <dl className="totals-grid">
      <div><dt>Materiais com perda</dt><dd>{money(totals.materialsTotalCost)}</dd></div>
      <div><dt>Mão de obra</dt><dd>{money(totals.laborCost)}</dd></div>
      <div><dt>Máquina</dt><dd>{money(totals.machineCost)}</dd></div>
      <div><dt>Custos adicionais</dt><dd>{money(totals.additionalDirectCosts)}</dd></div>
      <div className="totals-grid__total"><dt>Total estimado do lote ({totals.outputQuantity} un.)</dt><dd>{money(totals.totalEstimatedCost)}</dd></div>
      <div className="totals-grid__total"><dt>Custo estimado unitário</dt><dd>{money(totals.estimatedUnitCost)}</dd></div>
    </dl>
  </section>
}
