import { useEffect, useState, type FormEvent } from 'react'
import { apiClient } from '../api/client'
import { useSession } from '../auth/useSession'
import type {
  FilamentDetailsRequest, FilamentMaterialType, InventoryAdjustmentKind, InventoryMovementResponse,
  InventoryMovementListResponse, SupplyBaseUnit, SupplyCategoryResponse, SupplyCreateRequest,
  SupplyListItemResponse, SupplyListResponse, SupplyListStatus, SupplyResponse,
} from '../api/types'

const PAGE_SIZE = 10
const MOVEMENT_PAGE_SIZE = 20
const BASE_UNITS: SupplyBaseUnit[] = ['Gram', 'Kilogram', 'Unit', 'Milliliter', 'Liter', 'Meter', 'Centimeter']
const UNIT_LABELS: Record<SupplyBaseUnit, string> = {
  Gram: 'g', Kilogram: 'kg', Unit: 'un', Milliliter: 'mL', Liter: 'L', Meter: 'm', Centimeter: 'cm',
}
const MATERIAL_TYPES: FilamentMaterialType[] = ['Pla', 'PlaPlus', 'Petg', 'Abs', 'Asa', 'Tpu', 'Nylon', 'Pc', 'Pva', 'Other']
const MATERIAL_LABELS: Record<FilamentMaterialType, string> = {
  Pla: 'PLA', PlaPlus: 'PLA+', Petg: 'PETG', Abs: 'ABS', Asa: 'ASA', Tpu: 'TPU', Nylon: 'Nylon', Pc: 'PC', Pva: 'PVA', Other: 'Outro',
}

type SupplyForm = {
  code: string; name: string; description: string; categoryCode: string; baseUnit: SupplyBaseUnit; minimumStock: string; preferredSupplier: string; notes: string
  isFilament: boolean; filamentMaterialType: FilamentMaterialType; filamentBrand: string; filamentColorName: string; filamentColorCode: string; filamentDiameterMm: string; filamentSpoolNetWeightGrams: string
}

type SupplyListFilterOverrides = {
  categoryFilter?: string
  activeFilter?: 'active' | 'inactive' | 'all'
  lowStockOnly?: boolean
}

const STATUS_BY_FILTER: Record<'active' | 'inactive' | 'all', SupplyListStatus> = {
  active: 'active', inactive: 'inactive', all: 'all',
}

const blankForm: SupplyForm = {
  code: '', name: '', description: '', categoryCode: '', baseUnit: 'Gram', minimumStock: '', preferredSupplier: '', notes: '',
  isFilament: false, filamentMaterialType: 'Pla', filamentBrand: '', filamentColorName: '', filamentColorCode: '', filamentDiameterMm: '', filamentSpoolNetWeightGrams: '',
}

function toForm(supply: SupplyResponse): SupplyForm {
  const f = supply.filament
  return {
    code: supply.code, name: supply.name, description: supply.description ?? '', categoryCode: supply.categoryCode, baseUnit: supply.baseUnit,
    minimumStock: supply.minimumStock?.toString() ?? '', preferredSupplier: supply.preferredSupplier ?? '', notes: supply.notes ?? '',
    isFilament: f != null, filamentMaterialType: f?.materialType ?? 'Pla', filamentBrand: f?.brand ?? '', filamentColorName: f?.colorName ?? '',
    filamentColorCode: f?.colorCode ?? '', filamentDiameterMm: f?.diameterMm?.toString() ?? '', filamentSpoolNetWeightGrams: f?.spoolNetWeightGrams?.toString() ?? '',
  }
}

function toFilamentRequest(form: SupplyForm): FilamentDetailsRequest | null {
  if (!form.isFilament) return null
  return {
    materialType: form.filamentMaterialType, brand: form.filamentBrand, colorName: form.filamentColorName,
    colorCode: form.filamentColorCode || null, diameterMm: Number(form.filamentDiameterMm || 0), spoolNetWeightGrams: Number(form.filamentSpoolNetWeightGrams || 0),
  }
}

/** `numeric(p,s)` columns round-trip through the generated contract as `number | string`
 * (large/precise decimals may arrive as strings) — every quantity display goes through here. */
function formatQuantity(value: number | string, unit: SupplyBaseUnit, maximumFractionDigits: 4 | 8): string {
  return `${Number(value).toLocaleString('pt-BR', { maximumFractionDigits })} ${UNIT_LABELS[unit]}`
}

function formatBaseQuantity(value: number | string, unit: SupplyBaseUnit): string {
  return formatQuantity(value, unit, 4)
}

function formatEnteredQuantity(value: number | string, unit: SupplyBaseUnit): string {
  return formatQuantity(value, unit, 8)
}

export function SuppliesPage() {
  const { session } = useSession()
  const roles = session.status === 'authenticated' ? session.user.roles : []
  const canManage = roles.includes('Owner') || roles.includes('Operator')

  const [items, setItems] = useState<SupplyListItemResponse[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [categoryFilter, setCategoryFilter] = useState('')
  const [activeFilter, setActiveFilter] = useState<'active' | 'inactive' | 'all'>('active')
  const [lowStockOnly, setLowStockOnly] = useState(false)
  const [categories, setCategories] = useState<SupplyCategoryResponse[]>([])
  const [form, setForm] = useState<SupplyForm>(blankForm)
  const [selected, setSelected] = useState<SupplyResponse | null>(null)
  const [movements, setMovements] = useState<InventoryMovementResponse[]>([])
  const [movementPage, setMovementPage] = useState(1)
  const [movementTotal, setMovementTotal] = useState(0)
  const [message, setMessage] = useState('')

  // Adjustment/purchase sub-forms
  const [adjustmentKind, setAdjustmentKind] = useState<InventoryAdjustmentKind>('Increase')
  const [adjustmentQuantity, setAdjustmentQuantity] = useState('')
  const [adjustmentUnit, setAdjustmentUnit] = useState<SupplyBaseUnit>('Gram')
  const [adjustmentReason, setAdjustmentReason] = useState('')
  const [purchaseQuantity, setPurchaseQuantity] = useState('')
  const [purchaseUnit, setPurchaseUnit] = useState<SupplyBaseUnit>('Gram')
  const [purchaseTotalCost, setPurchaseTotalCost] = useState('')
  const [purchaseSupplier, setPurchaseSupplier] = useState('')
  const [purchaseReference, setPurchaseReference] = useState('')
  const [initialQuantity, setInitialQuantity] = useState('')
  const [initialUnit, setInitialUnit] = useState<SupplyBaseUnit>('Gram')

  async function load(targetPage: number, overrides: SupplyListFilterOverrides = {}) {
    const selectedCategory = overrides.categoryFilter ?? categoryFilter
    const selectedActive = overrides.activeFilter ?? activeFilter
    const selectedLowStock = overrides.lowStockOnly ?? lowStockOnly
    const params = new URLSearchParams({ page: String(targetPage), pageSize: String(PAGE_SIZE) })
    if (search) params.set('search', search)
    if (selectedCategory) params.set('categoryCode', selectedCategory)
    params.set('status', STATUS_BY_FILTER[selectedActive])
    if (selectedLowStock) params.set('lowStock', 'true')
    const result = await apiClient.get<SupplyListResponse>(`/api/supplies?${params}`)
    if (result.ok) { setItems(result.data.items); setTotal(Number(result.data.total)); setPage(Number(result.data.page)) }
    else setMessage(result.error.safeMessage)
  }

  async function loadCategories() {
    const result = await apiClient.get<SupplyCategoryResponse[]>('/api/supplies/categories')
    if (result.ok) setCategories(result.data)
  }

  async function open(id: string) {
    const result = await apiClient.get<SupplyResponse>(`/api/supplies/${id}`)
    if (result.ok) {
      setSelected(result.data)
      setForm(toForm(result.data))
      setInitialUnit(result.data.baseUnit); setPurchaseUnit(result.data.baseUnit); setAdjustmentUnit(result.data.baseUnit)
      await loadMovements(id)
    } else setMessage(result.error.safeMessage)
  }

  async function loadMovements(id: string, targetPage = 1) {
    const result = await apiClient.get<InventoryMovementListResponse>(`/api/supplies/${id}/inventory/movements?page=${targetPage}&pageSize=${MOVEMENT_PAGE_SIZE}`)
    if (result.ok) {
      setMovements(result.data.items)
      setMovementPage(Number(result.data.page))
      setMovementTotal(Number(result.data.total))
    }
  }

  // oxlint-disable-next-line react/set-state-in-effect, react-hooks/exhaustive-deps
  useEffect(() => { void load(1); void loadCategories() }, [])

  function newSupply() {
    setSelected(null)
    setForm(blankForm)
    setMessage('')
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    const filament = toFilamentRequest(form)
    const minimumStock = form.minimumStock ? Number(form.minimumStock) : null
    if (selected) {
      const result = await apiClient.put<SupplyResponse>(`/api/supplies/${selected.id}`, {
        name: form.name, description: form.description || null, categoryCode: form.categoryCode, minimumStock,
        preferredSupplier: form.preferredSupplier || null, notes: form.notes || null, filament, version: selected.version,
      })
      if (result.ok) { setMessage('Insumo atualizado com sucesso.'); await open(selected.id); await load(page) }
      else setMessage(result.error.safeMessage)
    } else {
      const request: SupplyCreateRequest = {
        code: form.code, name: form.name, description: form.description || null, categoryCode: form.categoryCode, baseUnit: form.baseUnit,
        minimumStock, preferredSupplier: form.preferredSupplier || null, notes: form.notes || null, filament,
      }
      const result = await apiClient.post<SupplyResponse>('/api/supplies', request)
      if (result.ok) { newSupply(); setMessage('Insumo criado com sucesso.'); await load(1) }
      else setMessage(result.error.safeMessage)
    }
  }

  async function toggleActive() {
    if (!selected) return
    const action = selected.active ? 'deactivate' : 'activate'
    const result = await apiClient.post<void>(`/api/supplies/${selected.id}/${action}?version=${selected.version}`)
    if (result.ok) { setMessage(selected.active ? 'Insumo desativado.' : 'Insumo reativado.'); await open(selected.id); await load(page) }
    else setMessage(result.error.safeMessage)
  }

  async function submitInitialBalance(event: FormEvent) {
    event.preventDefault()
    if (!selected) return
    const result = await apiClient.post<SupplyResponse>(`/api/supplies/${selected.id}/inventory/initial-balance`, {
      quantity: Number(initialQuantity || 0), enteredUnit: initialUnit, occurredAt: new Date().toISOString(), reference: null, notes: null, supplyVersion: selected.version,
    })
    if (result.ok) { setMessage('Saldo inicial registrado.'); setInitialQuantity(''); await open(selected.id); await load(page) }
    else setMessage(result.error.safeMessage)
  }

  async function submitPurchaseReceipt(event: FormEvent) {
    event.preventDefault()
    if (!selected) return
    const result = await apiClient.post<SupplyResponse>(`/api/supplies/${selected.id}/inventory/purchase-receipt`, {
      quantity: Number(purchaseQuantity || 0), enteredUnit: purchaseUnit, occurredAt: new Date().toISOString(), unitCost: null,
      totalCost: purchaseTotalCost ? Number(purchaseTotalCost) : null, supplier: purchaseSupplier || null, reference: purchaseReference || null, notes: null, supplyVersion: selected.version,
    })
    if (result.ok) { setMessage('Compra registrada.'); setPurchaseQuantity(''); setPurchaseTotalCost(''); setPurchaseSupplier(''); setPurchaseReference(''); await open(selected.id); await load(page) }
    else setMessage(result.error.safeMessage)
  }

  async function submitAdjustment(event: FormEvent) {
    event.preventDefault()
    if (!selected) return
    const result = await apiClient.post<SupplyResponse>(`/api/supplies/${selected.id}/inventory/adjustment`, {
      kind: adjustmentKind, quantity: Number(adjustmentQuantity || 0), enteredUnit: adjustmentUnit, reason: adjustmentReason, occurredAt: new Date().toISOString(), supplyVersion: selected.version,
    })
    if (result.ok) { setMessage('Ajuste registrado.'); setAdjustmentQuantity(''); setAdjustmentReason(''); await open(selected.id); await load(page) }
    else setMessage(result.error.safeMessage)
  }

  const lastPage = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const movementLastPage = Math.max(1, Math.ceil(movementTotal / MOVEMENT_PAGE_SIZE))
  const enteredUnitOptions = selected ? enteredUnitsFor(selected.baseUnit) : BASE_UNITS

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <h1>Suprimentos e estoque</h1>
          <p>Cadastre insumos, controle estoque e registre movimentações.</p>
        </div>
        {canManage && <button type="button" onClick={newSupply}>Novo insumo</button>}
      </div>

      <div className="split-grid">
        <div className="card">
          <label htmlFor="supply-search">Buscar insumos</label>
          <div className="inline-form">
            <input id="supply-search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Código ou nome" />
            <button type="button" onClick={() => void load(1)}>Buscar</button>
          </div>
          <label htmlFor="supply-category-filter">Categoria</label>
          <select id="supply-category-filter" value={categoryFilter} onChange={(e) => { const value = e.target.value; setCategoryFilter(value); void load(1, { categoryFilter: value }) }}>
            <option value="">Todas</option>
            {categories.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
          </select>
          <label htmlFor="supply-active-filter">Situação</label>
          <select id="supply-active-filter" value={activeFilter} onChange={(e) => { const value = e.target.value as typeof activeFilter; setActiveFilter(value); void load(1, { activeFilter: value }) }}>
            <option value="active">Ativos</option>
            <option value="inactive">Inativos</option>
            <option value="all">Todos</option>
          </select>
          <label>
            <input type="checkbox" checked={lowStockOnly} onChange={(e) => { const value = e.target.checked; setLowStockOnly(value); void load(1, { lowStockOnly: value }) }} /> Somente estoque baixo
          </label>

          <ul className="data-list">
            {items.map((supply) => (
              <li key={supply.id}>
                <button type="button" onClick={() => void open(supply.id)}><strong>{supply.code}</strong> — {supply.name}</button>
                <span>{formatBaseQuantity(supply.currentStockBaseUnit, supply.baseUnit)}</span>
                <span className={supply.isLowStock ? 'badge badge--muted' : 'badge'}>{supply.isLowStock ? 'Estoque baixo' : 'Estoque normal'}</span>
                <span className={supply.active ? 'badge' : 'badge badge--muted'}>{supply.active ? 'Ativo' : 'Inativo'}</span>
              </li>
            ))}
          </ul>
          <nav className="pagination" aria-label="Paginação de insumos">
            <button type="button" disabled={page <= 1} onClick={() => void load(page - 1)}>Anterior</button>
            <span>Página {page} de {lastPage}</span>
            <button type="button" disabled={page >= lastPage} onClick={() => void load(page + 1)}>Próxima</button>
          </nav>
        </div>

        <form className="card form-stack" onSubmit={submit}>
          <h2>{selected ? 'Editar insumo' : 'Novo insumo'}</h2>
          <label htmlFor="supply-code">Código</label>
          <input id="supply-code" required disabled={!!selected} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} placeholder="FIL-PLA-PRETO" />
          <label htmlFor="supply-name">Nome</label>
          <input id="supply-name" required minLength={2} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          <label htmlFor="supply-description">Descrição</label>
          <input id="supply-description" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          <label htmlFor="supply-category">Categoria</label>
          <select id="supply-category" required value={form.categoryCode} onChange={(e) => setForm({ ...form, categoryCode: e.target.value })}>
            <option value="">Selecione</option>
            {categories.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
          </select>
          <label htmlFor="supply-unit">Unidade base</label>
          <select id="supply-unit" required disabled={!!selected} value={form.baseUnit} onChange={(e) => setForm({ ...form, baseUnit: e.target.value as SupplyBaseUnit })}>
            {BASE_UNITS.map((u) => <option key={u} value={u}>{UNIT_LABELS[u]}</option>)}
          </select>
          <label htmlFor="supply-minimum">Estoque mínimo</label>
          <input id="supply-minimum" type="number" min={0} step="any" value={form.minimumStock} onChange={(e) => setForm({ ...form, minimumStock: e.target.value })} />
          <label htmlFor="supply-supplier">Fornecedor preferencial</label>
          <input id="supply-supplier" value={form.preferredSupplier} onChange={(e) => setForm({ ...form, preferredSupplier: e.target.value })} />
          <label htmlFor="supply-notes">Notas</label>
          <input id="supply-notes" value={form.notes} onChange={(e) => setForm({ ...form, notes: e.target.value })} />

          <label>
            <input type="checkbox" checked={form.isFilament} onChange={(e) => setForm({ ...form, isFilament: e.target.checked })} /> Este insumo é filamento
          </label>
          {form.isFilament && (
            <div className="form-stack">
              <label htmlFor="filament-material">Material</label>
              <select id="filament-material" value={form.filamentMaterialType} onChange={(e) => setForm({ ...form, filamentMaterialType: e.target.value as FilamentMaterialType })}>
                {MATERIAL_TYPES.map((m) => <option key={m} value={m}>{MATERIAL_LABELS[m]}</option>)}
              </select>
              <label htmlFor="filament-brand">Marca</label>
              <input id="filament-brand" required={form.isFilament} value={form.filamentBrand} onChange={(e) => setForm({ ...form, filamentBrand: e.target.value })} />
              <label htmlFor="filament-color">Cor</label>
              <input id="filament-color" required={form.isFilament} value={form.filamentColorName} onChange={(e) => setForm({ ...form, filamentColorName: e.target.value })} />
              <label htmlFor="filament-color-code">Código da cor</label>
              <input id="filament-color-code" value={form.filamentColorCode} onChange={(e) => setForm({ ...form, filamentColorCode: e.target.value })} placeholder="#000000" />
              <label htmlFor="filament-diameter">Diâmetro (mm)</label>
              <input id="filament-diameter" type="number" min={0} step="any" required={form.isFilament} value={form.filamentDiameterMm} onChange={(e) => setForm({ ...form, filamentDiameterMm: e.target.value })} />
              <label htmlFor="filament-spool-weight">Peso líquido do carretel (g)</label>
              <input id="filament-spool-weight" type="number" min={0} step="any" required={form.isFilament} value={form.filamentSpoolNetWeightGrams} onChange={(e) => setForm({ ...form, filamentSpoolNetWeightGrams: e.target.value })} />
            </div>
          )}
          {canManage && <button className="button-primary" type="submit">{selected ? 'Salvar alterações' : 'Salvar insumo'}</button>}
          {selected && canManage && (
            <button type="button" onClick={() => void toggleActive()}>{selected.active ? 'Desativar insumo' : 'Reativar insumo'}</button>
          )}
        </form>
      </div>

      {selected && (
        <div className="split-grid">
          <div className="card">
            <h2>Estoque de {selected.name}</h2>
            <p>Saldo atual: <strong>{formatBaseQuantity(selected.currentStockBaseUnit, selected.baseUnit)}</strong></p>
            {selected.minimumStock != null && <p>Estoque mínimo: {formatBaseQuantity(selected.minimumStock, selected.baseUnit)}</p>}
            <p className={selected.isLowStock ? 'badge badge--muted' : 'badge'}>{selected.isLowStock ? 'Estoque baixo' : 'Estoque normal'}</p>
            {selected.latestPurchaseUnitCost != null && <p>Último custo de compra: R$ {Number(selected.latestPurchaseUnitCost).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 6 })}/{UNIT_LABELS[selected.baseUnit]}</p>}

            <h3>Histórico de movimentações</h3>
            <ul className="data-list">
              {movements.map((m) => (
                <li key={m.id}>
                  <span>{new Date(m.occurredAt).toLocaleDateString('pt-BR')}</span>
                  <span>{m.type}</span>
                  <span>Informado: {formatEnteredQuantity(m.enteredQuantity, m.enteredUnit)}</span>
                  <span>Normalizado: {Number(m.quantityDeltaBaseUnit) > 0 ? '+' : ''}{formatBaseQuantity(m.quantityDeltaBaseUnit, m.baseUnit)}</span>
                  {m.reason && <span>{m.reason}</span>}
                  {m.reference && <span>Referência: {m.reference}</span>}
                  {m.supplier && <span>Fornecedor: {m.supplier}</span>}
                  {m.unitCostSnapshot != null && <span>Custo unitário: R$ {Number(m.unitCostSnapshot).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 6 })}/{UNIT_LABELS[m.baseUnit]}</span>}
                  {m.totalCostSnapshot != null && <span>Custo total: R$ {Number(m.totalCostSnapshot).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</span>}
                </li>
              ))}
              {movements.length === 0 && <li>Nenhuma movimentação registrada.</li>}
            </ul>
            {movementTotal > 0 && (
              <div className="pagination" aria-label="Paginação do histórico">
                <button type="button" disabled={movementPage <= 1} onClick={() => selected && void loadMovements(selected.id, movementPage - 1)}>Anterior</button>
                <span>Página {movementPage} de {movementLastPage} ({movementTotal} movimentações)</span>
                <button type="button" disabled={movementPage >= movementLastPage} onClick={() => selected && void loadMovements(selected.id, movementPage + 1)}>Próxima</button>
              </div>
            )}
          </div>

          {canManage && (
            <div className="card form-stack">
              {!selected.hasRecordedMovement && (
                <form className="form-stack" onSubmit={submitInitialBalance}>
                  <h3>Saldo inicial</h3>
                  <label htmlFor="initial-quantity">Quantidade</label>
                  <div className="inline-form">
                    <input id="initial-quantity" type="number" min={0} step="any" required value={initialQuantity} onChange={(e) => setInitialQuantity(e.target.value)} />
                    <select value={initialUnit} onChange={(e) => setInitialUnit(e.target.value as SupplyBaseUnit)}>
                      {enteredUnitOptions.map((u) => <option key={u} value={u}>{UNIT_LABELS[u]}</option>)}
                    </select>
                  </div>
                  <button className="button-primary" type="submit">Registrar saldo inicial</button>
                </form>
              )}

              <form className="form-stack" onSubmit={submitPurchaseReceipt}>
                <h3>Registrar compra</h3>
                <label htmlFor="purchase-quantity">Quantidade</label>
                <div className="inline-form">
                  <input id="purchase-quantity" type="number" min={0} step="any" required value={purchaseQuantity} onChange={(e) => setPurchaseQuantity(e.target.value)} />
                  <select value={purchaseUnit} onChange={(e) => setPurchaseUnit(e.target.value as SupplyBaseUnit)}>
                    {enteredUnitOptions.map((u) => <option key={u} value={u}>{UNIT_LABELS[u]}</option>)}
                  </select>
                </div>
                {purchaseQuantity && purchaseUnit !== selected.baseUnit && (
                  <p>Efeito no estoque: +{formatBaseQuantity(convertPreview(Number(purchaseQuantity), purchaseUnit, selected.baseUnit), selected.baseUnit)}</p>
                )}
                <label htmlFor="purchase-total-cost">Valor total (R$)</label>
                <input id="purchase-total-cost" type="number" min={0} step="any" value={purchaseTotalCost} onChange={(e) => setPurchaseTotalCost(e.target.value)} />
                <label htmlFor="purchase-supplier">Fornecedor</label>
                <input id="purchase-supplier" value={purchaseSupplier} onChange={(e) => setPurchaseSupplier(e.target.value)} />
                <label htmlFor="purchase-reference">Nota fiscal / referência</label>
                <input id="purchase-reference" value={purchaseReference} onChange={(e) => setPurchaseReference(e.target.value)} />
                <button className="button-primary" type="submit">Registrar compra</button>
              </form>

              <form className="form-stack" onSubmit={submitAdjustment}>
                <h3>Ajuste manual</h3>
                <label htmlFor="adjustment-kind">Tipo</label>
                <select id="adjustment-kind" value={adjustmentKind} onChange={(e) => setAdjustmentKind(e.target.value as InventoryAdjustmentKind)}>
                  <option value="Increase">Aumentar</option>
                  <option value="Decrease">Diminuir</option>
                  <option value="Correction">Corrigir por contagem</option>
                </select>
                <label htmlFor="adjustment-quantity">{adjustmentKind === 'Correction' ? 'Quantidade contada' : 'Quantidade'}</label>
                <div className="inline-form">
                  <input id="adjustment-quantity" type="number" min={0} step="any" required value={adjustmentQuantity} onChange={(e) => setAdjustmentQuantity(e.target.value)} />
                  <select value={adjustmentUnit} onChange={(e) => setAdjustmentUnit(e.target.value as SupplyBaseUnit)}>
                    {enteredUnitOptions.map((u) => <option key={u} value={u}>{UNIT_LABELS[u]}</option>)}
                  </select>
                </div>
                <label htmlFor="adjustment-reason">Motivo</label>
                <input id="adjustment-reason" required minLength={2} value={adjustmentReason} onChange={(e) => setAdjustmentReason(e.target.value)} placeholder="Contagem física, amostra, perda..." />
                <button className="button-primary" type="submit">Registrar ajuste</button>
              </form>
            </div>
          )}
        </div>
      )}

      {message && <p role="status" className="form-error">{message}</p>}
    </section>
  )
}

const COMPATIBLE_UNITS: Partial<Record<SupplyBaseUnit, SupplyBaseUnit[]>> = {
  Gram: ['Gram', 'Kilogram'], Kilogram: ['Kilogram', 'Gram'],
  Liter: ['Liter', 'Milliliter'], Milliliter: ['Milliliter', 'Liter'],
  Meter: ['Meter', 'Centimeter'], Centimeter: ['Centimeter', 'Meter'],
  Unit: ['Unit'],
}
function enteredUnitsFor(baseUnit: SupplyBaseUnit): SupplyBaseUnit[] {
  return COMPATIBLE_UNITS[baseUnit] ?? [baseUnit]
}
const CONVERSION_FACTORS: Partial<Record<string, number>> = {
  'Kilogram->Gram': 1000, 'Gram->Kilogram': 0.001, 'Liter->Milliliter': 1000, 'Milliliter->Liter': 0.001, 'Meter->Centimeter': 100, 'Centimeter->Meter': 0.01,
}
function convertPreview(quantity: number, from: SupplyBaseUnit, to: SupplyBaseUnit): number {
  if (from === to) return quantity
  const factor = CONVERSION_FACTORS[`${from}->${to}`]
  return factor ? quantity * factor : quantity
}
