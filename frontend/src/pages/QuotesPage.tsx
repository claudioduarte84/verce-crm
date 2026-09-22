import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { apiClient } from '../api/client'
import type { CustomerListItemResponse, CustomerListResponse, QuoteListItemResponse, QuoteListResponse, QuoteRevisionStatus } from '../api/types'
import { quoteErrorMessage } from './quoteErrors'

const PAGE_SIZE = 20

const STATUS_LABELS: Record<QuoteRevisionStatus, string> = {
  GENERATED: 'Gerado', SENT: 'Enviado', NEGOTIATING: 'Em negociação', APPROVED: 'Aprovado',
  CANCELED: 'Cancelado', EXPIRED: 'Expirado', SUPERSEDED: 'Substituído',
}
const OUTCOME_LABELS: Record<string, string> = { WON: 'Ganho', LOST: 'Perdido', OPEN: 'Em aberto' }

function money(value: number | string): string {
  return Number(value).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 2 })
}
function date(value: string): string {
  return new Date(`${value}T00:00:00`).toLocaleDateString('pt-BR')
}
function isExpiringSoon(validUntil: string): boolean {
  const days = (new Date(`${validUntil}T00:00:00`).getTime() - Date.now()) / 86_400_000
  return days >= 0 && days <= 3
}
function isExpired(validUntil: string): boolean {
  return new Date(`${validUntil}T00:00:00`).getTime() < Date.now()
}

type Filters = { search: string; status: QuoteRevisionStatus | ''; outcome: string; expired: '' | 'true' | 'false'; customerId: string }
const blankFilters: Filters = { search: '', status: '', outcome: '', expired: '', customerId: '' }

export function QuotesPage() {
  const [items, setItems] = useState<QuoteListItemResponse[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [filters, setFilters] = useState<Filters>(blankFilters)
  const [message, setMessage] = useState('')
  const [loading, setLoading] = useState(false)

  const [customerSearch, setCustomerSearch] = useState('')
  const [customerLabel, setCustomerLabel] = useState('')
  const [customerOptions, setCustomerOptions] = useState<CustomerListItemResponse[]>([])

  // mission §71/§110-style stale-request protection: only the RESULT of the most recently
  // ISSUED request is ever applied to state — a slow older response can never overwrite a
  // newer one that already landed, regardless of arrival order.
  const requestSequence = useRef(0)
  const abortController = useRef<AbortController | null>(null)

  async function load(targetPage: number, overrides: Partial<Filters> = {}) {
    const effective = { ...filters, ...overrides }
    const params = new URLSearchParams({ page: String(targetPage), pageSize: String(PAGE_SIZE) })
    if (effective.search) params.set('search', effective.search)
    if (effective.status) params.set('status', effective.status)
    if (effective.outcome) params.set('outcome', effective.outcome)
    if (effective.expired) params.set('expired', effective.expired)
    if (effective.customerId) params.set('customerId', effective.customerId)

    abortController.current?.abort()
    const controller = new AbortController()
    abortController.current = controller
    const sequence = ++requestSequence.current

    setLoading(true)
    const result = await apiClient.get<QuoteListResponse>(`/api/quotes?${params}`, controller.signal)
    if (sequence !== requestSequence.current) return // a newer request has already superseded this one

    setLoading(false)
    if (result.ok) { setItems(result.data.items); setTotal(Number(result.data.total)); setPage(Number(result.data.page)); setMessage('') }
    else setMessage(quoteErrorMessage(result.error))
  }

  // oxlint-disable-next-line react/set-state-in-effect, react-hooks/exhaustive-deps
  useEffect(() => { void load(1) }, [])

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const term = customerSearch.trim()
      if (!term) { setCustomerOptions([]); return }
      const params = new URLSearchParams({ page: '1', pageSize: '10', search: term })
      void apiClient.get<CustomerListResponse>(`/api/customers?${params}`, controller.signal)
        .then(result => { if (result.ok) setCustomerOptions(result.data.items) })
    }, 250)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [customerSearch])

  function selectCustomer(customer: CustomerListItemResponse) {
    setFilters(cur => ({ ...cur, customerId: customer.id }))
    setCustomerLabel(customer.name)
    setCustomerSearch('')
    setCustomerOptions([])
    void load(1, { customerId: customer.id })
  }

  function clearCustomer() {
    setFilters(cur => ({ ...cur, customerId: '' }))
    setCustomerLabel('')
    void load(1, { customerId: '' })
  }

  function applyFilters() {
    void load(1)
  }

  function clearFilters() {
    setFilters(blankFilters)
    setCustomerLabel('')
    setCustomerSearch('')
    void load(1, blankFilters)
  }

  const lastPage = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <Link to="/">← Início</Link>
          <h1>Orçamentos</h1>
          <p>Busque, acompanhe e crie orçamentos comerciais.</p>
        </div>
        <Link to="/quotes/new" className="button-primary">Novo orçamento</Link>
      </div>

      <div className="card">
        <div className="inline-form">
          <input
            aria-label="Buscar por número ou cliente"
            placeholder="Número ou cliente"
            value={filters.search}
            onChange={e => setFilters({ ...filters, search: e.target.value })}
          />
          <div>
            <input
              aria-label="Filtrar por cliente"
              placeholder="Filtrar por cliente cadastrado"
              value={filters.customerId ? customerLabel : customerSearch}
              onChange={e => { if (filters.customerId) clearCustomer(); setCustomerSearch(e.target.value) }}
            />
            {!filters.customerId && customerOptions.length > 0 && customerSearch && (
              <ul className="data-list">
                {customerOptions.map(customer => (
                  <li key={customer.id}>
                    <button type="button" onClick={() => selectCustomer(customer)}>
                      {customer.name}{customer.document ? ` · ${customer.document}` : ''}
                    </button>
                  </li>
                ))}
              </ul>
            )}
            {filters.customerId && <button type="button" onClick={clearCustomer}>Remover filtro de cliente</button>}
          </div>
          <select aria-label="Status" value={filters.status} onChange={e => setFilters({ ...filters, status: e.target.value as QuoteRevisionStatus | '' })}>
            <option value="">Todos os status</option>
            {(Object.keys(STATUS_LABELS) as QuoteRevisionStatus[]).map(status => <option key={status} value={status}>{STATUS_LABELS[status]}</option>)}
          </select>
          <select aria-label="Resultado comercial" value={filters.outcome} onChange={e => setFilters({ ...filters, outcome: e.target.value })}>
            <option value="">Qualquer resultado</option>
            <option value="OPEN">Em aberto</option>
            <option value="WON">Ganho</option>
            <option value="LOST">Perdido</option>
          </select>
          <select aria-label="Vencimento" value={filters.expired} onChange={e => setFilters({ ...filters, expired: e.target.value as Filters['expired'] })}>
            <option value="">Qualquer vencimento</option>
            <option value="false">Não expirados</option>
            <option value="true">Expirados</option>
          </select>
          <button type="button" onClick={applyFilters}>Buscar</button>
          <button type="button" onClick={clearFilters}>Limpar</button>
        </div>

        <table className="data-table" aria-busy={loading}>
          <thead>
            <tr>
              <th>Número</th><th>Cliente</th><th>Status</th><th>Validade</th><th>Total</th><th>Resultado</th><th>Produção</th>
            </tr>
          </thead>
          <tbody>
            {items.map(quote => (
              <tr key={quote.id}>
                <td><Link to={`/quotes/${quote.id}`}>{quote.number}{quote.currentRevisionSuffix}</Link></td>
                <td>{quote.customerName ?? 'Cliente avulso'}</td>
                <td><span className="badge">{STATUS_LABELS[quote.currentStatus]}</span></td>
                <td>
                  {date(quote.validUntil)}
                  {isExpired(quote.validUntil) && <span className="badge badge--danger"> expirado</span>}
                  {!isExpired(quote.validUntil) && isExpiringSoon(quote.validUntil) && <span className="badge badge--warning"> vence em breve</span>}
                </td>
                <td>{money(quote.currentTotalAmount)}</td>
                <td>{OUTCOME_LABELS[quote.commercialOutcome] ?? quote.commercialOutcome}</td>
                <td>{quote.productionOrderStatus ?? '—'}</td>
              </tr>
            ))}
            {!loading && items.length === 0 && <tr><td colSpan={7}>Nenhum orçamento encontrado.</td></tr>}
          </tbody>
        </table>

        <nav className="pagination" aria-label="Paginação de orçamentos">
          <button type="button" disabled={page <= 1} onClick={() => void load(page - 1)}>Anterior</button>
          <span>Página {page} de {lastPage}</span>
          <button type="button" disabled={page >= lastPage} onClick={() => void load(page + 1)}>Próxima</button>
        </nav>
      </div>

      {/* One stable status region — never two independently-mounted role="status" elements
       * racing each other, so a test (or a screen reader) always observes exactly one settled
       * announcement instead of a transient "Carregando…" node getting swapped for the error. */}
      {(loading || message) && <p role="status" className={message ? 'form-error' : undefined}>{loading ? 'Carregando…' : message}</p>}
    </section>
  )
}
