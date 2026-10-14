import { useCallback, useEffect, useRef, useState } from 'react'
import { apiClient, type ApiError } from '../api/client'
import {
  SYNC_REQUEST_MESSAGES, blockedReasonMessage, requiredActionMessage, runStatusLabel, safeResultMessage, syncHealthLabel, syncRequestMessage,
} from './marketplaceCatalog'

/**
 * S8C.2 C4 — the "Sincronização de anúncios" block of Marketplace Accounts (ADR-0025 §J, handoff C4).
 *
 * Everything shown here is server-derived and deliberately separate from the OAuth connection block: health, availability,
 * the three RequiredAction surfaces, run counters and the failed items. The UI never recomputes health, counters or a
 * RequiredAction, and never talks to the provider: the 5 s polling below only re-reads VERCE's own status endpoint, and
 * only while a run is active.
 *
 * The parent mounts this with `key={accountId}`, so a different account is a different instance: its timers, requests and
 * state die with the old one and a late response can never be rendered for the new account.
 */

const POLL_INTERVAL_MS = 5000
const FAILED_ITEMS_PAGE_SIZE = 25

type Counters = { total: number; found: number; blockingRetryable: number; blockingNonRetryable: number; pending: number; byOutcome: Record<string, number> }
type Run = {
  runId: string; accountId: string; kind: string; status: string; phase: string | null; requestedAt: string; startedAt: string | null; finishedAt: string | null
  counters: Counters; safeResultCode: string | null; requiredAction: string
}
type Availability = { canStart: boolean; blockedReason: string | null; requiredAction: string; retryAfterUntil: string | null; activeRunId: string | null }
type SyncStatus = {
  availability: Availability
  summary: { activeRun: Run | null; latestRun: Run | null; latestFullRun: Run | null; lastSucceededFullRunAt: string | null; openBlockingItemCount: number; health: string }
}
type RunItem = { externalListingId: string; origin: string; outcome: string; safeErrorCode: string | null; marketplaceListingId: string | null; retryable: boolean }
type ItemsPage = { items: RunItem[]; page: number; pageSize: number; total: number }
type Feedback = { tone: 'ok' | 'error'; text: string; requiredAction?: string }

const TERMINAL = new Set(['SUCCEEDED', 'PARTIAL', 'FAILED'])
/** The outcomes that block a run's result (ADR-0025 §E.4). The items endpoint filters by exactly one outcome per request. */
const BLOCKING_OUTCOMES = ['NOT_FOUND', 'ACCESS_DENIED', 'TRANSIENT_ERROR', 'SELLER_MISMATCH', 'PERMANENT_ERROR'] as const

const isTerminal = (run: Run | null | undefined): run is Run => run != null && TERMINAL.has(run.status)
const isObject = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)
const isSyncStatus = (value: unknown): value is SyncStatus =>
  isObject(value) && isObject(value.availability) && isObject(value.summary) && typeof value.summary.health === 'string'
const when = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleString('pt-BR') : null)
const count = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : 0)
const text = (value: unknown) => (typeof value === 'string' && value !== '' ? value : undefined)

/** A safe pt-BR sentence for a refused start/retry. Reads the Problem Details extensions the generic client message would hide (409/503). */
function refusal(error: ApiError): Feedback {
  const problem = error.problem as Record<string, unknown> | undefined
  const blockedReason = text(problem?.blockedReason), code = text(problem?.code), retryAfterUntil = text(problem?.retryAfterUntil), requiredAction = text(problem?.requiredAction)
  let message = error.safeMessage
  if (blockedReason) message = blockedReasonMessage(blockedReason, retryAfterUntil)
  else if (code && Object.hasOwn(SYNC_REQUEST_MESSAGES, code)) message = syncRequestMessage(code)
  return { tone: 'error', text: message, requiredAction: requiredAction && requiredAction !== 'NONE' ? requiredAction : undefined }
}

/** The result text of a run: only when the code has a frozen text, or the run failed (then the raw code is the only context). */
function resultText(run: Run, retryAfterUntil: string | null): string | null {
  if (!run.safeResultCode) return null
  const message = safeResultMessage(run.safeResultCode, retryAfterUntil)
  return message !== run.safeResultCode || run.status === 'FAILED' ? message : null
}

function RunBlock({ title, run }: { title: string; run: Run }) {
  const c = run.counters
  const result = resultText(run, null), action = requiredActionMessage(run.requiredAction)
  return <div role="group" aria-label={title}>
    <h3>{title}</h3>
    <p>
      <span className={run.status === 'FAILED' ? 'badge badge--danger' : run.status === 'PARTIAL' ? 'badge badge--warning' : 'badge'}>{runStatusLabel(run.status)}</span>
      {run.phase && <> · Fase: {run.phase}</>}
    </p>
    {result && <p>{result}</p>}
    {action && <p role="alert" className="badge badge--muted">Ação necessária (execução): {action}</p>}
    <p>
      Total: {count(c.total)} · Encontrados: {count(c.found)} · Com erro (repetíveis): {count(c.blockingRetryable)} · Com erro (não repetíveis): {count(c.blockingNonRetryable)} · Pendentes: {count(c.pending)}
    </p>
    <p>{run.finishedAt ? `Concluída em ${when(run.finishedAt)}` : run.startedAt ? `Iniciada em ${when(run.startedAt)}` : `Solicitada em ${when(run.requestedAt)}`}</p>
  </div>
}

function FailedItems({ run, accountSignal }: { run: Run; accountSignal: () => AbortSignal | undefined }) {
  const byOutcome = run.counters.byOutcome ?? {}
  const [outcome, setOutcome] = useState<string>(() => BLOCKING_OUTCOMES.find((o) => count(byOutcome[o]) > 0) ?? BLOCKING_OUTCOMES[0])
  const [page, setPage] = useState(1)
  const [data, setData] = useState<ItemsPage | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let current = true
    const query = new URLSearchParams({ outcome, page: String(page), pageSize: String(FAILED_ITEMS_PAGE_SIZE) })
    void (async () => {
      try {
        const r = await apiClient.get<ItemsPage>(`/api/commerce/marketplace-listing-syncs/${run.runId}/items?${query}`, accountSignal())
        if (!current) return
        if (r.ok && isObject(r.data) && Array.isArray(r.data.items)) { setData(r.data); setError('') } else { setData(null); setError(r.ok ? 'Não foi possível carregar os itens com falha.' : r.error.safeMessage) }
      } catch { return /* aborted: the account changed or the panel was closed */ }
      if (current) setLoading(false)
    })()
    return () => { current = false }
  }, [run.runId, outcome, page, accountSignal])

  const pageSize = data?.pageSize || FAILED_ITEMS_PAGE_SIZE, total = count(data?.total), pages = Math.max(1, Math.ceil(total / pageSize))
  function choose(next: string) { setOutcome(next); setPage(1); setData(null); setError(''); setLoading(true) }
  function move(next: number) { setPage(next); setLoading(true) }

  return <div role="group" aria-label="Itens com falha">
    <h3>Itens com falha</h3>
    <div className="inline-form">
      <label htmlFor="failed-outcome">Resultado</label>
      <select id="failed-outcome" value={outcome} onChange={(e) => choose(e.target.value)}>
        {BLOCKING_OUTCOMES.map((o) => <option key={o} value={o}>{o} ({count(byOutcome[o])})</option>)}
      </select>
    </div>
    {loading && <p role="status">Carregando itens…</p>}
    {!loading && error && <p role="alert">{error}</p>}
    {!loading && !error && data && data.items.length === 0 && <p>Nenhum item com este resultado.</p>}
    {!error && data && data.items.length > 0 && <ul className="data-list">
      {data.items.map((item) => <li key={item.externalListingId}>
        <strong>{item.externalListingId}</strong>
        <span>{item.outcome} · {item.retryable ? 'Pode ser tentado novamente' : 'Não pode ser tentado novamente'}</span>
        {item.safeErrorCode && <span>{safeResultMessage(item.safeErrorCode)}</span>}
      </li>)}
    </ul>}
    {total > pageSize && <div className="inline-form">
      <button type="button" disabled={page <= 1 || loading} onClick={() => move(page - 1)}>Anterior</button>
      <span>Página {data?.page ?? page} de {pages}</span>
      <button type="button" disabled={page >= pages || loading} onClick={() => move(page + 1)}>Próxima</button>
    </div>}
  </div>
}

export function ListingSyncPanel({ accountId, canManage }: { accountId: string; canManage: boolean }) {
  const [status, setStatus] = useState<SyncStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState('')
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  const [busy, setBusy] = useState(false)
  const [pollEpoch, setPollEpoch] = useState(0)
  const controller = useRef<AbortController | null>(null)
  const alive = useRef(false)
  const base = `/api/commerce/marketplace-accounts/${accountId}`

  const signal = useCallback(() => controller.current?.signal, [])

  // Single authority for status reads of THIS instance: polling, "Atualizar" and the re-reads after Start/Retry all go through refresh().
  // At most one GET is ever in flight. A call made while one is running is coalesced into ONE follow-up read that starts when the current
  // one settles, so a re-read requested after a mutation is never lost, repeated requests never pile up, and nothing runs after the
  // panel is gone. The guard is a synchronous ref (React state updates are asynchronous and cannot serialize reads).
  const flight = useRef<Promise<boolean> | null>(null)
  const followUp = useRef<Promise<boolean> | null>(null)

  const read = useCallback(async (current: AbortController): Promise<boolean> => {
    try {
      const r = await apiClient.get<SyncStatus>(`${base}/listing-sync`, current.signal)
      if (current.signal.aborted || !alive.current) return false
      if (r.ok && isSyncStatus(r.data)) { setStatus(r.data); setLoadError(''); return true }
      setLoadError(r.ok ? 'Não foi possível carregar o estado da sincronização.' : r.error.safeMessage)
      return false
    } catch { return false /* aborted */ }
  }, [base])

  const begin = useCallback((current: AbortController): Promise<boolean> => {
    const started = read(current).finally(() => { flight.current = null })
    flight.current = started
    return started
  }, [read])

  /** Resolves true on a fresh, valid status; false on any failure or when the panel is gone. */
  const refresh = useCallback((): Promise<boolean> => {
    const current = controller.current
    if (!current || current.signal.aborted) return Promise.resolve(false)
    if (flight.current === null) return begin(current)
    followUp.current ??= flight.current.then(() => {
      followUp.current = null // the settled read cleared `flight` first, so the follow-up starts a fresh single flight
      return current.signal.aborted || !alive.current ? false : begin(current)
    })
    return followUp.current
  }, [begin])

  useEffect(() => {
    const own = new AbortController()
    controller.current = own; alive.current = true
    void refresh().then(() => { if (alive.current && !own.signal.aborted) setLoading(false) })
    return () => { alive.current = false; own.abort() }
  }, [refresh])

  const active = status?.summary.activeRun != null
  // One serial chain: the next read is scheduled only after the previous one finished, so reads never pile up. It runs only while a run is
  // active, stops on any failure (the "Atualizar" button restarts it), and dies with the account change or unmount.
  useEffect(() => {
    if (!active) return
    let stopped = false
    let timer: number | undefined
    const tick = async () => {
      const ok = await refresh()
      if (!stopped && ok) timer = window.setTimeout(() => void tick(), POLL_INTERVAL_MS)
    }
    timer = window.setTimeout(() => void tick(), POLL_INTERVAL_MS)
    return () => { stopped = true; window.clearTimeout(timer) }
  }, [active, pollEpoch, refresh])

  async function manualRefresh() { setBusy(true); const ok = await refresh(); if (alive.current) { setBusy(false); if (ok) setPollEpoch((n) => n + 1) } }

  async function request(path: string, success: string) {
    setBusy(true); setFeedback(null)
    const r = await apiClient.post<Run>(path)
    if (!alive.current) return
    setFeedback(r.ok ? { tone: 'ok', text: success } : refusal(r.error))
    await refresh() // a refusal also means the state changed (for example an already active run)
    if (alive.current) setBusy(false)
  }

  const summary = status?.summary, availability = status?.availability
  const activeRun = summary?.activeRun ?? null
  const terminalRun = isTerminal(summary?.latestRun) ? summary!.latestRun : isTerminal(summary?.latestFullRun) ? summary!.latestFullRun : null
  const latestToShow = summary?.latestRun && summary.latestRun.runId !== activeRun?.runId ? summary.latestRun : null
  const openItems = count(summary?.openBlockingItemCount)
  // The server owns health. The one guard here: a payload that says "em dia" while blocking items remain open is never shown as healthy.
  const healthCode = summary ? (summary.health === 'HEALTHY' && openItems > 0 ? 'ATTENTION_REQUIRED' : summary.health) : ''
  const canRetry = canManage && !!availability?.canStart && !activeRun && terminalRun != null && ['PARTIAL', 'FAILED'].includes(terminalRun.status) && count(terminalRun.counters.blockingRetryable) > 0
  const blockingTotal = terminalRun ? count(terminalRun.counters.blockingRetryable) + count(terminalRun.counters.blockingNonRetryable) : 0
  const availabilityAction = requiredActionMessage(availability?.requiredAction)

  return <section className="card" aria-labelledby="listing-sync-heading">
    <h2 id="listing-sync-heading">Sincronização de anúncios</h2>
    <p>Leitura dos anúncios do provedor, iniciada manualmente. A autorização da conta é tratada acima e não indica o estado desta sincronização.</p>
    {loading && <p role="status">Carregando sincronização…</p>}
    {!loading && loadError && <p role="alert">{loadError}</p>}
    {feedback && <p role={feedback.tone === 'error' ? 'alert' : 'status'}>{feedback.text}{feedback.requiredAction && <> {requiredActionMessage(feedback.requiredAction)}</>}</p>}

    {status && summary && availability && <>
      <p>
        Saúde: <span className={healthCode === 'HEALTHY' ? 'badge' : 'badge badge--warning'}>{syncHealthLabel(healthCode)}</span>
        {' '}· Itens bloqueantes em aberto: {openItems}
        {summary.lastSucceededFullRunAt && <> · Última sincronização completa bem-sucedida em {when(summary.lastSucceededFullRunAt)}</>}
      </p>
      <p>
        Disponibilidade: {availability.canStart ? 'pronta para sincronizar' : 'indisponível no momento'}
        {availability.blockedReason && <> — {blockedReasonMessage(availability.blockedReason, availability.retryAfterUntil)}</>}
      </p>
      {availabilityAction && <p role="alert" className="badge badge--muted">Ação necessária (disponibilidade): {availabilityAction}</p>}

      {activeRun && <RunBlock title="Execução em andamento" run={activeRun} />}
      {latestToShow && <RunBlock title="Última execução" run={latestToShow} />}
      {!activeRun && !latestToShow && <p>Nenhuma sincronização foi executada para esta conta.</p>}

      <div className="inline-form">
        {canManage && <button type="button" disabled={busy || !availability.canStart} onClick={() => void request(`${base}/listing-syncs`, 'Sincronização solicitada.')}>Sincronizar anúncios</button>}
        {canRetry && terminalRun && <button type="button" disabled={busy} onClick={() => void request(`/api/commerce/marketplace-listing-syncs/${terminalRun.runId}/retry`, 'Nova tentativa solicitada para os itens com erro.')}>Tentar novamente itens com erro</button>}
        <button type="button" disabled={busy} onClick={() => void manualRefresh()}>Atualizar</button>
      </div>
      {activeRun && <p role="status">Acompanhando a execução a cada {POLL_INTERVAL_MS / 1000} s (somente o estado interno do VERCE).</p>}

      {terminalRun && blockingTotal > 0 && !activeRun && <FailedItems key={terminalRun.runId} run={terminalRun} accountSignal={signal} />}
    </>}
  </section>
}
