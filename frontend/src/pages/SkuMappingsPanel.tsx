import { useCallback, useEffect, useRef, useState } from 'react'
import { apiClient } from '../api/client'
import { skuMappingErrorMessage } from './marketplaceCatalog'

/**
 * S8C.2 C4 — "SKUs confirmados" of one Marketplace account (ADR-0025 §D/§J): the explicit SKU → Product authority, read-only for
 * Viewer, with deactivation for Owner/Operator. Confirming a mapping is C5 (Published Items), never here.
 *
 * Deactivation sends the version the list returned (ADR-0028): a stale version is a 409 and the list is reloaded to recover the
 * current one; the UI never invents a version. Mounted with `key={accountId}` by the parent, so a different account is a
 * different instance and a late response cannot cross over.
 */

const SKU_PAGE_SIZE = 25

type Mapping = {
  id: string; marketplaceAccountId: string; externalSku: string; productId: string; productCode: string | null; productName: string | null
  active: boolean; confirmedAt: string; invalidatedAt: string | null; invalidatedReason: string | null; version: number
}
type MappingsPage = { items: Mapping[]; page: number; pageSize: number; total: number }
type Filter = 'true' | 'false' | 'all'

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString('pt-BR') : null)

export function SkuMappingsPanel({ accountId, canManage }: { accountId: string; canManage: boolean }) {
  const [filter, setFilter] = useState<Filter>('true')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<MappingsPage | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [busyId, setBusyId] = useState<string | null>(null)
  const [reloads, setReloads] = useState(0)
  const alive = useRef(false)

  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])

  const reload = useCallback(() => { setReloads((n) => n + 1) }, [])

  useEffect(() => {
    const own = new AbortController()
    const query = new URLSearchParams({ page: String(page), pageSize: String(SKU_PAGE_SIZE) })
    if (filter !== 'all') query.set('active', filter)
    void (async () => {
      try {
        const r = await apiClient.get<MappingsPage>(`/api/commerce/marketplace-accounts/${accountId}/sku-mappings?${query}`, own.signal)
        if (own.signal.aborted) return
        if (r.ok && r.data && Array.isArray(r.data.items)) { setData(r.data); setError('') } else { setData(null); setError(r.ok ? 'Não foi possível carregar os SKUs confirmados.' : r.error.safeMessage) }
      } catch { return /* aborted */ }
      if (!own.signal.aborted) setLoading(false)
    })()
    return () => own.abort()
  }, [accountId, filter, page, reloads])

  async function deactivate(mapping: Mapping) {
    setBusyId(mapping.id); setMessage('')
    const r = await apiClient.post<void>(`/api/commerce/sku-mappings/${mapping.id}/deactivate`, { version: mapping.version })
    if (!alive.current) return
    if (r.ok) setMessage(`Mapeamento do SKU ${mapping.externalSku} desativado.`)
    else {
      const code = typeof r.error.problem?.code === 'string' ? r.error.problem.code : undefined
      // 409 CONCURRENCY_CONFLICT and 404 carry the client's safe generic sentence; a SKU mapping code, if any, has its frozen text.
      setMessage(code && skuMappingErrorMessage(code) !== code ? skuMappingErrorMessage(code) : r.error.safeMessage)
    }
    setBusyId(null)
    reload() // success, conflict or 404: the list is re-read, which is the only source of the current version
  }

  const total = data?.total ?? 0, pageSize = data?.pageSize || SKU_PAGE_SIZE, pages = Math.max(1, Math.ceil(total / pageSize))

  return <section className="card" aria-labelledby="sku-mappings-heading">
    <h2 id="sku-mappings-heading">SKUs confirmados</h2>
    <p>Autoridade explícita &quot;SKU → Produto&quot; desta conta. Ela só afeta vínculos automáticos futuros: nunca altera anúncios já vinculados.</p>
    <div className="inline-form">
      <label htmlFor="sku-filter">Mostrar</label>
      <select id="sku-filter" value={filter} onChange={(e) => { setFilter(e.target.value as Filter); setPage(1); setLoading(true) }}>
        <option value="true">Ativos</option><option value="false">Inativos</option><option value="all">Todos</option>
      </select>
    </div>
    {message && <p role="status">{message}</p>}
    {loading && <p role="status">Carregando SKUs confirmados…</p>}
    {!loading && error && <p role="alert">{error}</p>}
    {!loading && !error && data && data.items.length === 0 && <p>Nenhum SKU confirmado{filter === 'true' ? ' ativo' : filter === 'false' ? ' inativo' : ''} para esta conta.</p>}
    {!error && data && data.items.length > 0 && <ul className="data-list">
      {data.items.map((m) => <li key={m.id}>
        <strong>{m.externalSku}</strong>
        <span>Produto: {m.productCode && m.productName ? `${m.productCode} · ${m.productName}` : m.productId}</span>
        <span className={m.active ? 'badge' : 'badge badge--muted'}>{m.active ? 'Ativo' : 'Inativo'}</span>
        <span>Confirmado em {when(m.confirmedAt)}{m.invalidatedAt && <> · Desativado em {when(m.invalidatedAt)}</>}</span>
        {canManage && m.active && <button type="button" aria-label={`Desativar SKU ${m.externalSku}`} disabled={busyId !== null} onClick={() => void deactivate(m)}>Desativar</button>}
      </li>)}
    </ul>}
    {total > pageSize && <div className="inline-form">
      <button type="button" disabled={page <= 1 || loading} onClick={() => { setPage(page - 1); setLoading(true) }}>Anterior</button>
      <span>Página {data?.page ?? page} de {pages}</span>
      <button type="button" disabled={page >= pages || loading} onClick={() => { setPage(page + 1); setLoading(true) }}>Próxima</button>
    </div>}
  </section>
}
