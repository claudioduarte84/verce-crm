import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ListingSyncPanel } from './ListingSyncPanel'

// Literals on purpose: the page size (<= 100) and the polling interval are frozen contract values, not something the tests may follow.
const FAILED_ITEMS_PAGE_SIZE = 25

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const START_URL = '/api/commerce/marketplace-accounts/a1/listing-syncs'
const counters = (o: Record<string, unknown> = {}) => ({ total: 0, found: 0, blockingRetryable: 0, blockingNonRetryable: 0, pending: 0, byOutcome: {}, byOrigin: {}, listingsCreated: 0, observationsAppended: 0, ...o })
const run = (o: Record<string, unknown> = {}) => ({
  runId: 'r1', accountId: 'a1', kind: 'FULL', status: 'SUCCEEDED', phase: null, rootRunId: 'r1', retryOfRunId: null, requestedAt: '2026-10-09T12:00:00Z', startedAt: '2026-10-09T12:00:05Z',
  finishedAt: '2026-10-09T12:05:00Z', attemptCount: 1, enumerationCompleted: true, counters: counters(), safeResultCode: 'LISTING_SYNC_COMPLETED', requiredAction: 'NONE', ...o,
})
const availability = (o: Record<string, unknown> = {}) => ({ canStart: true, blockedReason: null, requiredAction: 'NONE', retryAfterUntil: null, activeRunId: null, ...o })
const summary = (o: Record<string, unknown> = {}) => ({ activeRun: null, latestRun: null, latestFullRun: null, lastSucceededFullRunAt: null, openBlockingItemCount: 0, health: 'NEVER_RUN', ...o })
const status = (a: Record<string, unknown> = {}, s: Record<string, unknown> = {}) => ({ availability: availability(a), summary: summary(s) })
const partial = (o: Record<string, unknown> = {}) => run({ runId: 'rp', status: 'PARTIAL', safeResultCode: 'LISTING_SYNC_PARTIAL', counters: counters({ total: 10, found: 7, blockingRetryable: 2, blockingNonRetryable: 1, pending: 0, byOutcome: { FOUND: 7, TRANSIENT_ERROR: 2, PERMANENT_ERROR: 1 } }), ...o })
const partialStatus = () => status({}, { health: 'ATTENTION_REQUIRED', openBlockingItemCount: 3, latestRun: partial(), latestFullRun: partial() })
const item = (id: string, o: Record<string, unknown> = {}) => ({ externalListingId: id, origin: 'ENUMERATED', outcome: 'TRANSIENT_ERROR', safeErrorCode: 'LISTING_READ_FAILED', marketplaceListingId: null, retryable: true, ...o })
const itemsPage = (items: unknown[], o: Record<string, unknown> = {}) => ({ items, page: 1, pageSize: FAILED_ITEMS_PAGE_SIZE, total: items.length, ...o })

const live = vi.hoisted(() => ({ status: null as unknown, items: null as unknown }))
function serve(initial: unknown, items: unknown = { items: [], page: 1, pageSize: 25, total: 0 }) {
  live.status = initial; live.items = items
  api.get.mockImplementation((path: string) => {
    if (path.endsWith('/listing-sync')) return Promise.resolve({ ok: true, data: live.status })
    if (path.includes('/items')) return Promise.resolve({ ok: true, data: live.items })
    return Promise.resolve({ ok: false, error: { status: 404, safeMessage: 'Recurso não encontrado.' } })
  })
}
const statusCalls = () => api.get.mock.calls.filter(([p]) => String(p).endsWith('/listing-sync')).length
const problem = (status: number, safeMessage: string, p: Record<string, unknown>) => ({ ok: false, error: { status, kind: 'problem', safeMessage, problem: { status, title: p.code, ...p } } })
const flush = () => act(async () => { await vi.advanceTimersByTimeAsync(0) })
// The frozen interval is a literal here on purpose (handoff C4 / ADR-0025 §J): the tests must not follow a changed constant.
const FROZEN_POLL_MS = 5000
const tickPoll = () => act(async () => { await vi.advanceTimersByTimeAsync(FROZEN_POLL_MS) })

async function mount(canManage: boolean, accountId = 'a1') {
  const view = render(<ListingSyncPanel key={accountId} accountId={accountId} canManage={canManage} />)
  await screen.findByRole('heading', { name: 'Sincronização de anúncios' })
  await waitFor(() => expect(screen.queryByText('Carregando sincronização…')).not.toBeInTheDocument())
  return view
}

beforeEach(() => { vi.clearAllMocks(); api.post.mockResolvedValue({ ok: true, status: 202, data: run({ status: 'QUEUED' }) }) })
afterEach(() => { vi.useRealTimers() })

describe('ListingSyncPanel — roles', () => {
  it.each([['Owner/Operator', true]])('%s see Start and the status', async (_label, canManage) => {
    serve(status({}, { health: 'NEVER_RUN' }))
    await mount(canManage)
    expect(screen.getByRole('button', { name: 'Sincronizar anúncios' })).toBeEnabled()
    expect(screen.getByText('Nunca sincronizada')).toBeInTheDocument()
    expect(screen.getByText(/Nenhuma sincronização foi executada/)).toBeInTheDocument()
  })

  it('Viewer reads status, runs and failures but gets no Start or Retry control', async () => {
    serve(partialStatus(), itemsPage([item('MLB1')]))
    await mount(false)
    expect(screen.getByText('Concluída com pendências')).toBeInTheDocument()
    expect(await screen.findByText('MLB1')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sincronizar anúncios' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Tentar novamente itens com erro' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Atualizar' })).toBeInTheDocument() // a read
  })

  it('never renders a credential or authorization control (those stay on the connection block)', async () => {
    serve(status())
    await mount(true)
    for (const name of [/Reautorizar/, /Sondar/, /Desconectar/, /Conectar/, /Reconectar/]) expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
  })
})

describe('ListingSyncPanel — start', () => {
  it('POSTs without a body, shows the accepted run immediately from the refreshed status, and does not auto-start on open', async () => {
    serve(status())
    const user = userEvent.setup(); await mount(true)
    expect(api.post).not.toHaveBeenCalled()
    live.status = status({ canStart: false, blockedReason: 'SYNC_ACTIVE', activeRunId: 'r2' }, { health: 'IN_PROGRESS', activeRun: run({ runId: 'r2', status: 'QUEUED', finishedAt: null, startedAt: null, safeResultCode: null }), latestRun: run({ runId: 'r2', status: 'QUEUED', finishedAt: null, startedAt: null, safeResultCode: null }) })
    await user.click(screen.getByRole('button', { name: 'Sincronizar anúncios' }))
    expect(api.post).toHaveBeenCalledTimes(1)
    expect(api.post).toHaveBeenCalledWith(START_URL) // no body, no external ids, nothing else
    expect(await screen.findByRole('group', { name: 'Execução em andamento' })).toHaveTextContent('Na fila')
    expect(screen.getByText('Sincronização solicitada.')).toBeInTheDocument()
    expect(screen.getByText('Em andamento', { selector: '.badge' })).toBeInTheDocument() // health IN_PROGRESS
  })

  it('disables Start and explains why when the server says it cannot start', async () => {
    serve(status({ canStart: false, blockedReason: 'GRANT_UNKNOWN' }))
    await mount(true)
    expect(screen.getByRole('button', { name: 'Sincronizar anúncios' })).toBeDisabled()
    expect(screen.getByText(/Permissão de leitura de anúncios ainda não verificada\. Use "Sondar disponibilidade"\./)).toBeInTheDocument()
  })

  it.each([
    ['ACCOUNT_INACTIVE', 'Conta inativa. Ative a conta para sincronizar.'], ['CHANNEL_INVALID', 'O canal de vendas desta conta está inativo ou inválido.'],
    ['PROVIDER_NOT_CONFIGURED', 'Este provedor não está configurado neste ambiente.'], ['CAPABILITY_UNSUPPORTED', 'Leitura de anúncios não é suportada para este provedor.'],
    ['GRANT_DENIED', 'O provedor negou a leitura de anúncios. Verifique a conta no provedor.'], ['AUTHORIZATION_NOT_USABLE', 'A autorização desta conta não está utilizável. Siga a ação indicada na conta.'],
    ['RUNTIME_UNAVAILABLE', 'O provedor está indisponível para esta conta. Use "Sondar disponibilidade".'], ['SYNC_ACTIVE', 'Já existe uma sincronização em andamento.'],
  ])('shows the C3 text for blockedReason %s', async (reason, message) => {
    serve(status({ canStart: false, blockedReason: reason }))
    await mount(true)
    expect(screen.getByText(message, { exact: false })).toBeInTheDocument()
  })

  it('formats RATE_LIMITED with retryAfterUntil and never leaks the placeholder', async () => {
    serve(status({ canStart: false, blockedReason: 'RATE_LIMITED', retryAfterUntil: '2026-10-09T14:05:00Z' }))
    await mount(true)
    expect(screen.getByText(/Limite de requisições do provedor atingido\. Tente novamente após \d{2}\/\d{2}\/\d{4} \d{2}:\d{2}\./)).toBeInTheDocument()
    expect(document.body.textContent).not.toMatch(/\{hora\}|Invalid Date|undefined/)
  })

  it('RATE_LIMITED without a usable time shows no invented time', async () => {
    serve(status({ canStart: false, blockedReason: 'RATE_LIMITED', retryAfterUntil: null }))
    await mount(true)
    expect(screen.getByText(/Limite de requisições do provedor atingido\./)).toBeInTheDocument()
    expect(document.body.textContent).not.toMatch(/Tente novamente após|\{hora\}/)
  })
})

describe('ListingSyncPanel — health, status and counters', () => {
  it('PARTIAL is "Concluída com pendências" with the server counters and an attention health, never healthy', async () => {
    serve(partialStatus(), itemsPage([item('MLB1')]))
    await mount(true)
    const group = screen.getByRole('group', { name: 'Última execução' })
    expect(within(group).getByText('Concluída com pendências')).toBeInTheDocument()
    expect(group).toHaveTextContent('Total: 10 · Encontrados: 7 · Com erro (repetíveis): 2 · Com erro (não repetíveis): 1 · Pendentes: 0')
    expect(screen.getByText('Requer atenção')).toBeInTheDocument(); expect(screen.queryByText('Anúncios em dia')).not.toBeInTheDocument()
    expect(screen.getByText(/Itens bloqueantes em aberto: 3/)).toBeInTheDocument()
  })

  it('renders zero counters instead of hiding them', async () => {
    serve(status({}, { health: 'HEALTHY', latestRun: run({ counters: counters({ total: 5, found: 5 }) }), latestFullRun: run() }))
    await mount(true)
    expect(screen.getByRole('group', { name: 'Última execução' })).toHaveTextContent('Com erro (repetíveis): 0 · Com erro (não repetíveis): 0 · Pendentes: 0')
    expect(screen.getByText('Anúncios em dia')).toBeInTheDocument()
  })

  it('a HEALTHY claim with open blocking items is never shown as "em dia"', async () => {
    serve(status({}, { health: 'HEALTHY', openBlockingItemCount: 2, latestRun: partial(), latestFullRun: partial() }), itemsPage([]))
    await mount(true)
    expect(screen.queryByText('Anúncios em dia')).not.toBeInTheDocument(); expect(screen.getByText('Requer atenção')).toBeInTheDocument()
  })

  it('health FAILED has no invented translation: the code is shown, the run status says "Falhou", and the result code gives context', async () => {
    serve(status({}, { health: 'FAILED', latestRun: run({ status: 'FAILED', safeResultCode: 'LISTING_ENUMERATION_INCOMPLETE', counters: counters() }), latestFullRun: run({ status: 'FAILED' }) }))
    await mount(true)
    expect(screen.getByText('FAILED', { selector: '.badge' })).toBeInTheDocument()
    expect(screen.getByText('Falhou')).toBeInTheDocument()
    expect(screen.getByText('Não foi possível listar todos os anúncios do provedor.')).toBeInTheDocument()
    for (const forbidden of ['Anúncios em dia', 'Concluída com pendências', 'Requer atenção']) expect(screen.queryByText(forbidden)).not.toBeInTheDocument()
  })

  it('shows the phase of a running run and an unknown result code as the code itself', async () => {
    serve(status({ canStart: false, blockedReason: 'SYNC_ACTIVE' }, { health: 'IN_PROGRESS', activeRun: run({ status: 'RUNNING', phase: 'DETAILING', safeResultCode: null, finishedAt: null }), latestRun: run({ runId: 'r0', status: 'FAILED', safeResultCode: 'SOMETHING_NEW' }) }))
    await mount(false)
    expect(screen.getByRole('group', { name: 'Execução em andamento' })).toHaveTextContent('Em andamento · Fase: DETAILING')
    expect(screen.getByText('SOMETHING_NEW')).toBeInTheDocument()
  })

  it('keeps the three RequiredAction surfaces separate and never recomputes them', async () => {
    serve(status({ canStart: false, blockedReason: 'AUTHORIZATION_NOT_USABLE', requiredAction: 'REAUTHORIZE' }, { health: 'FAILED', latestRun: run({ status: 'FAILED', safeResultCode: 'PROVIDER_ACCESS_FORBIDDEN', requiredAction: 'CHECK_PROVIDER_ACCOUNT' }), latestFullRun: run({ status: 'FAILED' }) }))
    await mount(false)
    expect(screen.getByText('Ação necessária (disponibilidade): Autorização não é mais válida. Reautorize com o provedor.')).toBeInTheDocument()
    expect(screen.getByText('Ação necessária (execução): O provedor negou uma capacidade ou uma operação falhou. Verifique a conta diretamente no provedor.')).toBeInTheDocument()
    expect(screen.queryByText(/Ação necessária \(conexão\)/)).not.toBeInTheDocument() // the connection surface belongs to the page's authorization block
  })

  it('renders nothing for RequiredAction NONE', async () => {
    serve(status({}, { health: 'HEALTHY', latestRun: run(), latestFullRun: run() }))
    await mount(true)
    expect(screen.queryByText(/Ação necessária/)).not.toBeInTheDocument()
  })

  it('shows a load error without inventing a status', async () => {
    api.get.mockResolvedValue({ ok: false, error: { status: 0, kind: 'network', safeMessage: 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.' } })
    render(<ListingSyncPanel accountId="a1" canManage />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Não foi possível conectar ao servidor.')
    expect(screen.queryByRole('button', { name: 'Sincronizar anúncios' })).not.toBeInTheDocument()
  })

  it('treats a malformed status payload as an error, not as an empty state', async () => {
    api.get.mockResolvedValue({ ok: true, data: [{ id: 'x' }] })
    render(<ListingSyncPanel accountId="a1" canManage />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Não foi possível carregar o estado da sincronização.')
  })
})

describe('ListingSyncPanel — retry', () => {
  it('is offered for a terminal run with retryable items and POSTs to that run without a body', async () => {
    serve(partialStatus(), itemsPage([item('MLB1')]))
    const user = userEvent.setup(); await mount(true)
    live.status = status({ canStart: false, blockedReason: 'SYNC_ACTIVE' }, { health: 'IN_PROGRESS', activeRun: run({ runId: 'rr', kind: 'FAILED_ONLY', status: 'QUEUED', finishedAt: null }), latestRun: run({ runId: 'rr', kind: 'FAILED_ONLY', status: 'QUEUED', finishedAt: null }), latestFullRun: partial() })
    await user.click(screen.getByRole('button', { name: 'Tentar novamente itens com erro' }))
    expect(api.post).toHaveBeenCalledTimes(1)
    expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-listing-syncs/rp/retry') // the terminal run's id only
    expect(await screen.findByRole('group', { name: 'Execução em andamento' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Tentar novamente itens com erro' })).not.toBeInTheDocument()
  })

  it.each([
    ['no retryable item', status({}, { health: 'ATTENTION_REQUIRED', latestRun: partial({ counters: counters({ blockingRetryable: 0, blockingNonRetryable: 1, byOutcome: { PERMANENT_ERROR: 1 } }) }), latestFullRun: partial() }), true],
    ['a succeeded run', status({}, { health: 'HEALTHY', latestRun: run(), latestFullRun: run() }), true],
    ['an active run', status({ canStart: false, blockedReason: 'SYNC_ACTIVE' }, { health: 'IN_PROGRESS', activeRun: run({ status: 'RUNNING', finishedAt: null }), latestRun: partial(), latestFullRun: partial() }), true],
    ['the server saying it cannot start', status({ canStart: false, blockedReason: 'RUNTIME_UNAVAILABLE' }, { health: 'ATTENTION_REQUIRED', latestRun: partial(), latestFullRun: partial() }), true],
    ['a Viewer', partialStatus(), false],
  ])('is not offered for %s', async (_label, payload, canManage) => {
    serve(payload, itemsPage([]))
    await mount(canManage)
    expect(screen.queryByRole('button', { name: 'Tentar novamente itens com erro' })).not.toBeInTheDocument()
  })

  it('has no way to type or pick external ids', async () => {
    serve(partialStatus(), itemsPage([item('MLB1')]))
    await mount(true)
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
  })

  it('shows the C3 text when the backend refuses with nothing to retry, and refreshes', async () => {
    serve(partialStatus(), itemsPage([item('MLB1')]))
    const user = userEvent.setup(); await mount(true)
    api.post.mockResolvedValueOnce(problem(409, 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.', { code: 'LISTING_SYNC_NOTHING_TO_RETRY' }))
    const before = statusCalls()
    await user.click(screen.getByRole('button', { name: 'Tentar novamente itens com erro' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Não há itens com erro que possam ser tentados novamente.')
    expect(statusCalls()).toBe(before + 1)
  })
})

describe('ListingSyncPanel — refused start (409/503 Problem Details)', () => {
  it('an already active run (409) uses blockedReason, not the generic 409 sentence, and refreshes to show the active run', async () => {
    serve(status())
    const user = userEvent.setup(); await mount(true)
    live.status = status({ canStart: false, blockedReason: 'SYNC_ACTIVE', activeRunId: 'r9' }, { health: 'IN_PROGRESS', activeRun: run({ runId: 'r9', status: 'RUNNING', finishedAt: null }), latestRun: run({ runId: 'r9', status: 'RUNNING', finishedAt: null }) })
    api.post.mockResolvedValueOnce(problem(409, 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.', { code: 'LISTING_SYNC_ALREADY_ACTIVE', blockedReason: 'SYNC_ACTIVE', requiredAction: 'NONE', activeRunId: 'r9' }))
    await user.click(screen.getByRole('button', { name: 'Sincronizar anúncios' }))
    const alerts = await screen.findAllByRole('alert')
    expect(alerts[0]).toHaveTextContent('Já existe uma sincronização em andamento.')
    expect(document.body.textContent).not.toContain('os dados mudaram')
    expect(await screen.findByRole('group', { name: 'Execução em andamento' })).toBeInTheDocument()
  })

  it('a 503 uses retryAfterUntil from the problem body and shows the server RequiredAction guidance', async () => {
    serve(status())
    const user = userEvent.setup(); await mount(true)
    api.post.mockResolvedValueOnce(problem(503, 'Não foi possível concluir a operação. Tente novamente em instantes.', { code: 'COMMERCE_OPERATION_UNAVAILABLE', blockedReason: 'RUNTIME_UNAVAILABLE', requiredAction: 'CHECK_PROVIDER_ACCOUNT' }))
    await user.click(screen.getByRole('button', { name: 'Sincronizar anúncios' }))
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('O provedor está indisponível para esta conta. Use "Sondar disponibilidade".')
    expect(alert).toHaveTextContent('Verifique a conta diretamente no provedor.')
    api.post.mockResolvedValueOnce(problem(503, 'generic', { code: 'COMMERCE_OPERATION_UNAVAILABLE', blockedReason: 'RATE_LIMITED', requiredAction: 'NONE', retryAfterUntil: '2026-10-09T14:05:00Z' }))
    await user.click(screen.getByRole('button', { name: 'Sincronizar anúncios' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/Tente novamente após \d{2}\/\d{2}\/\d{4} \d{2}:\d{2}\./))
  })

  it('an unknown refusal falls back to the safe client message and never to exception text', async () => {
    serve(status())
    const user = userEvent.setup(); await mount(true)
    api.post.mockResolvedValueOnce({ ok: false, error: { status: 500, kind: 'unexpected', safeMessage: 'Não foi possível concluir a operação. Tente novamente em instantes.', debugDetail: 'System.NullReferenceException at Verce' } })
    await user.click(screen.getByRole('button', { name: 'Sincronizar anúncios' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Não foi possível concluir a operação. Tente novamente em instantes.')
    expect(document.body.textContent).not.toMatch(/NullReference|Verce\b.*at /)
  })
})

describe('ListingSyncPanel — polling lifecycle (VERCE status only)', () => {

  const activeStatus = () => status({ canStart: false, blockedReason: 'SYNC_ACTIVE', activeRunId: 'r1' }, { health: 'IN_PROGRESS', activeRun: run({ status: 'RUNNING', finishedAt: null }), latestRun: run({ status: 'RUNNING', finishedAt: null }) })

  async function mountFake(payload: unknown, accountId = 'a1') {
    vi.useFakeTimers(); serve(payload)
    const view = render(<ListingSyncPanel key={accountId} accountId={accountId} canManage />)
    await flush()
    return view
  }

  it('does not poll when nothing is active', async () => {
    await mountFake(status({}, { health: 'HEALTHY', latestRun: run(), latestFullRun: run() }))
    expect(statusCalls()).toBe(1)
    await tickPoll(); await tickPoll(); await tickPoll()
    expect(statusCalls()).toBe(1)
  })

  it('polls every 5 s exactly while a run is active and stops after it turns terminal', async () => {
    await mountFake(activeStatus())
    expect(statusCalls()).toBe(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(FROZEN_POLL_MS - 1) })
    expect(statusCalls()).toBe(1) // nothing before 5 s
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(statusCalls()).toBe(2)
    await tickPoll()
    expect(statusCalls()).toBe(3) // one read per interval, no pile-up
    live.status = status({}, { health: 'HEALTHY', latestRun: run(), latestFullRun: run() })
    await tickPoll()
    expect(statusCalls()).toBe(4)
    expect(screen.getByText('Concluída')).toBeInTheDocument()
    await tickPoll(); await tickPoll(); await tickPoll()
    expect(statusCalls()).toBe(4) // terminal: polling ended
    expect(screen.queryByText(/Acompanhando a execução/)).not.toBeInTheDocument()
  })

  it('never issues overlapping status reads: a slow read delays the next one instead of stacking', async () => {
    vi.useFakeTimers()
    let release: (() => void) | undefined; let inFlight = 0, maxInFlight = 0
    api.get.mockImplementation((path: string) => {
      if (!path.endsWith('/listing-sync')) return Promise.resolve({ ok: true, data: itemsPage([]) })
      inFlight++; maxInFlight = Math.max(maxInFlight, inFlight)
      const finish = () => { inFlight--; return { ok: true, data: activeStatus() } }
      if (statusCalls() === 2) return new Promise((resolve) => { release = () => resolve(finish()) })
      return Promise.resolve(finish())
    })
    render(<ListingSyncPanel accountId="a1" canManage />)
    await flush()
    await tickPoll() // second read starts and hangs
    expect(statusCalls()).toBe(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(FROZEN_POLL_MS * 3) })
    expect(statusCalls()).toBe(2) // the next read is not scheduled until the slow one returns
    await act(async () => { release?.(); await vi.advanceTimersByTimeAsync(0) })
    await tickPoll()
    expect(statusCalls()).toBe(3)
    expect(maxInFlight).toBe(1)
  })

  it('stops polling after an error and resumes through "Atualizar" without duplicating timers', async () => {
    await mountFake(activeStatus())
    api.get.mockImplementation((path: string) => path.endsWith('/listing-sync') ? Promise.resolve({ ok: false, error: { status: 0, kind: 'network', safeMessage: 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.' } }) : Promise.resolve({ ok: true, data: itemsPage([]) }))
    await tickPoll()
    expect(statusCalls()).toBe(2)
    await tickPoll(); await tickPoll()
    expect(statusCalls()).toBe(2) // terminal error: no further polling
    expect(screen.getByRole('alert')).toHaveTextContent('Não foi possível conectar ao servidor.')
    serve(activeStatus())
    await act(async () => { screen.getByRole('button', { name: 'Atualizar' }).click(); await vi.advanceTimersByTimeAsync(0) })
    const afterManual = statusCalls()
    await tickPoll()
    expect(statusCalls()).toBe(afterManual + 1) // exactly one chain
    await tickPoll()
    expect(statusCalls()).toBe(afterManual + 2)
  })

  it('stops on unmount: no read after the page is gone', async () => {
    const view = await mountFake(activeStatus())
    await tickPoll()
    const before = statusCalls()
    view.unmount()
    await tickPoll(); await tickPoll()
    expect(statusCalls()).toBe(before)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('stops when the account changes and ignores a late response from the previous account', async () => {
    vi.useFakeTimers()
    let lateA: ((value: unknown) => void) | undefined
    api.get.mockImplementation((path: string) => {
      if (path.includes('/a1/listing-sync')) return new Promise((resolve) => { lateA = resolve })
      if (path.includes('/a2/listing-sync')) return Promise.resolve({ ok: true, data: status({}, { health: 'HEALTHY', latestRun: run({ accountId: 'a2', counters: counters({ total: 42, found: 42 }) }), latestFullRun: run() }) })
      return Promise.resolve({ ok: true, data: itemsPage([]) })
    })
    const view = render(<ListingSyncPanel key="a1" accountId="a1" canManage />)
    await flush()
    expect(lateA).toBeDefined()
    view.rerender(<ListingSyncPanel key="a2" accountId="a2" canManage />)
    await flush()
    expect(screen.getByRole('group', { name: 'Última execução' })).toHaveTextContent('Total: 42')
    await act(async () => { lateA?.({ ok: true, data: activeStatus() }); await vi.advanceTimersByTimeAsync(0) })
    expect(screen.getByRole('group', { name: 'Última execução' })).toHaveTextContent('Total: 42')
    expect(screen.queryByRole('group', { name: 'Execução em andamento' })).not.toBeInTheDocument()
    await tickPoll(); await tickPoll()
    expect(api.get.mock.calls.filter(([p]) => String(p).includes('/a1/listing-sync')).length).toBe(1) // account a1 is never polled
    expect(api.get.mock.calls.filter(([p]) => String(p).includes('/a2/listing-sync')).length).toBe(1)
  })
})

describe('ListingSyncPanel — failed items', () => {
  it('lists one outcome at a time with pageSize <= 100, defaults to an outcome that has items, and uses catalog texts with a code fallback', async () => {
    serve(partialStatus(), itemsPage([item('MLB1'), item('MLB2', { safeErrorCode: 'BRAND_NEW_CODE', retryable: false })]))
    await mount(true)
    expect(await screen.findByText('MLB1')).toBeInTheDocument()
    expect(screen.getByText('Falha ao ler este anúncio.')).toBeInTheDocument()
    expect(screen.getByText('BRAND_NEW_CODE')).toBeInTheDocument()
    expect(screen.getByText(/Não pode ser tentado novamente/)).toBeInTheDocument()
    const url = String(api.get.mock.calls.find(([p]) => String(p).includes('/items'))?.[0])
    expect(url).toContain('/api/commerce/marketplace-listing-syncs/rp/items?')
    const query = new URL(url, 'https://x.test').searchParams
    expect(query.getAll('outcome')).toEqual(['TRANSIENT_ERROR']); expect(query.get('page')).toBe('1'); expect(Number(query.get('pageSize'))).toBeLessThanOrEqual(100)
  })

  it('changing the outcome sends exactly one other outcome and restarts at page 1', async () => {
    serve(partialStatus(), itemsPage([item('MLB1')]))
    const user = userEvent.setup(); await mount(true)
    await screen.findByText('MLB1')
    await user.selectOptions(screen.getByLabelText('Resultado'), 'PERMANENT_ERROR')
    await waitFor(() => expect(api.get).toHaveBeenCalledWith(expect.stringContaining('outcome=PERMANENT_ERROR'), expect.anything()))
    const last = String(api.get.mock.calls.filter(([p]) => String(p).includes('/items')).at(-1)?.[0])
    expect(new URL(last, 'https://x.test').searchParams.getAll('outcome')).toEqual(['PERMANENT_ERROR'])
    expect(last).toContain('page=1')
  })

  it('paginates across pages', async () => {
    const many = Array.from({ length: FAILED_ITEMS_PAGE_SIZE }, (_, i) => item(`MLB${i}`))
    serve(partialStatus(), itemsPage(many, { total: 60 }))
    const user = userEvent.setup(); await mount(true)
    await screen.findByText('MLB0')
    expect(screen.getByText('Página 1 de 3')).toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    live.items = itemsPage([item('MLB-P2')], { page: 2, total: 60 })
    await user.click(screen.getByRole('button', { name: 'Próxima' }))
    expect(await screen.findByText('MLB-P2')).toBeInTheDocument()
    expect(api.get).toHaveBeenCalledWith(expect.stringContaining('page=2'), expect.anything())
    expect(screen.getByText('Página 2 de 3')).toBeInTheDocument()
  })

  it('shows an empty state and a load error without inventing rows', async () => {
    serve(partialStatus(), itemsPage([]))
    await mount(true)
    expect(await screen.findByText('Nenhum item com este resultado.')).toBeInTheDocument()
  })

  it('shows a safe error when the items request fails', async () => {
    serve(partialStatus())
    api.get.mockImplementation((path: string) => path.endsWith('/listing-sync') ? Promise.resolve({ ok: true, data: partialStatus() }) : Promise.resolve({ ok: false, error: { status: 404, safeMessage: 'Recurso não encontrado.' } }))
    await mount(true)
    expect(await screen.findByText('Recurso não encontrado.')).toBeInTheDocument()
  })

  it('does not show the failed-items section when the latest run left no blocking items, and never uses totals from the first page', async () => {
    serve(status({}, { health: 'HEALTHY', latestRun: run({ counters: counters({ total: 500, found: 500 }) }), latestFullRun: run() }))
    await mount(true)
    expect(screen.queryByRole('group', { name: 'Itens com falha' })).not.toBeInTheDocument()
    expect(api.get.mock.calls.some(([p]) => String(p).includes('/items'))).toBe(false)
    expect(screen.getByRole('group', { name: 'Última execução' })).toHaveTextContent('Total: 500')
  })
})

// ---- single-flight status reads (Terra 042): polling, "Atualizar", and the re-reads after Start/Retry share one authority ----
describe('ListingSyncPanel — at most one status GET in flight', () => {
  const START = '/api/commerce/marketplace-accounts/a1/listing-syncs'
  const activeRunning = () => status({ canStart: false, blockedReason: 'SYNC_ACTIVE', activeRunId: 'r1' }, { health: 'IN_PROGRESS', activeRun: run({ status: 'RUNNING', finishedAt: null }), latestRun: run({ status: 'RUNNING', finishedAt: null }) })
  const terminal = () => status({}, { health: 'HEALTHY', latestRun: run(), latestFullRun: run() })
  type Reply = { ok: true; data: unknown } | { ok: false; error: unknown }
  const networkError: Reply = { ok: false, error: { status: 0, kind: 'network', safeMessage: 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.' } }

  /** Every status GET is a controlled promise; concurrency is measured at the transport, not inferred from call counts. */
  function gated() {
    const pending: Array<{ account: string; settle: (reply: Reply) => void }> = []
    const meter = { inFlight: 0, max: 0, started: 0, byAccount: {} as Record<string, number> }
    api.get.mockImplementation((path: string) => {
      if (!path.endsWith('/listing-sync')) return Promise.resolve({ ok: true, data: itemsPage([]) })
      const account = /accounts\/([^/]+)\//.exec(path)![1]
      meter.started++; meter.inFlight++; meter.max = Math.max(meter.max, meter.inFlight); meter.byAccount[account] = (meter.byAccount[account] ?? 0) + 1
      return new Promise((resolve) => { pending.push({ account, settle: (reply) => { meter.inFlight--; resolve(reply) } }) })
    })
    return { pending, meter, settle: async (index: number, reply: Reply) => { await act(async () => { pending[index].settle(reply); await vi.advanceTimersByTimeAsync(0) }) } }
  }
  const ok = (data: unknown): Reply => ({ ok: true, data })
  const click = (name: string) => act(async () => { screen.getByRole('button', { name }).click(); await vi.advanceTimersByTimeAsync(0) })
  async function mountGated(first: unknown, accountId = 'a1') {
    vi.useFakeTimers()
    const probe = gated()
    const view = render(<ListingSyncPanel key={accountId} accountId={accountId} canManage />)
    await flush()
    await probe.settle(0, ok(first))
    return { ...probe, view }
  }

  it('A: "Atualizar" during a pending polling GET starts no second GET, then runs exactly one consolidated read', async () => {
    const g = await mountGated(activeRunning())
    await tickPoll()
    expect(g.meter.started).toBe(2) // the polling read is pending
    await click('Atualizar')
    expect(g.meter.started).toBe(2); expect(g.meter.inFlight).toBe(1) // no parallel GET
    await g.settle(1, ok(activeRunning()))
    expect(g.meter.started).toBe(3) // one follow-up, started only after the first settled
    await g.settle(2, ok(activeRunning()))
    expect(g.meter.max).toBe(1)
    expect(screen.getByRole('button', { name: 'Atualizar' })).toBeEnabled() // never blocked permanently
    // exactly one automatic chain remains: one read per 5 s
    await tickPoll(); expect(g.meter.started).toBe(4); await g.settle(3, ok(activeRunning()))
    await tickPoll(); expect(g.meter.started).toBe(5)
    expect(g.meter.max).toBe(1)
  })

  it('B: repeated manual clicks while a read is pending coalesce into at most one extra read', async () => {
    const g = await mountGated(activeRunning())
    await tickPoll()
    for (let i = 0; i < 5; i++) await click('Atualizar')
    expect(g.meter.started).toBe(2)
    await g.settle(1, ok(activeRunning()))
    expect(g.meter.started).toBe(3)
    await g.settle(2, ok(activeRunning()))
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(g.meter.started).toBe(3) // nothing left queued
    expect(g.meter.max).toBe(1)
  })

  it('C: Start accepted (202) while a polling GET is pending: the re-read is not lost and never runs in parallel', async () => {
    const payload = status({ canStart: true }, { health: 'IN_PROGRESS', activeRun: run({ status: 'RUNNING', finishedAt: null }), latestRun: run({ status: 'RUNNING', finishedAt: null }) }) // canStart true only so Start is clickable
    const g = await mountGated(payload)
    await tickPoll(); expect(g.meter.started).toBe(2)
    await click('Sincronizar anúncios')
    expect(api.post).toHaveBeenCalledWith(START)
    expect(g.meter.started).toBe(2); expect(g.meter.inFlight).toBe(1)
    await g.settle(1, ok(payload))
    expect(g.meter.started).toBe(3) // the post-mutation read ran after the pending one
    await g.settle(2, ok(status({}, { health: 'IN_PROGRESS', activeRun: run({ runId: 'r-new', status: 'QUEUED', finishedAt: null, startedAt: null }), latestRun: run({ runId: 'r-new', status: 'QUEUED', finishedAt: null, startedAt: null }) })))
    expect(screen.getByRole('group', { name: 'Execução em andamento' })).toHaveTextContent('Na fila') // fresh state, not the earlier snapshot
    expect(g.meter.max).toBe(1)
  })

  it('D: Retry accepted (202) triggers exactly one re-read and shows the retry run it returns', async () => {
    const g = await mountGated(partialStatus())
    await click('Tentar novamente itens com erro')
    expect(api.post).toHaveBeenCalledWith('/api/commerce/marketplace-listing-syncs/rp/retry')
    expect(g.meter.started).toBe(2)
    await g.settle(1, ok(status({ canStart: false, blockedReason: 'SYNC_ACTIVE' }, { health: 'IN_PROGRESS', activeRun: run({ runId: 'rr', kind: 'FAILED_ONLY', status: 'QUEUED', finishedAt: null }), latestRun: run({ runId: 'rr', kind: 'FAILED_ONLY', status: 'QUEUED', finishedAt: null }), latestFullRun: partial() })))
    expect(screen.getByRole('group', { name: 'Execução em andamento' })).toBeInTheDocument()
    expect(g.meter.max).toBe(1); expect(g.meter.started).toBe(2)
  })

  it('E: a failed read releases the lock, queues no loop, ends polling, and a manual action recovers', async () => {
    const g = await mountGated(activeRunning())
    await tickPoll(); await click('Atualizar')
    await g.settle(1, networkError) // the polling read fails; the explicit manual request still gets its single read
    expect(g.meter.started).toBe(3)
    await g.settle(2, networkError)
    expect(screen.getByRole('alert')).toHaveTextContent('Não foi possível conectar ao servidor.')
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(g.meter.started).toBe(3) // no loop, polling stays ended
    await click('Atualizar'); expect(g.meter.started).toBe(4) // the lock was released
    await g.settle(3, ok(activeRunning()))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(g.meter.max).toBe(1)
  })

  it('F: switching account with a read and a queued refresh pending in A emits no more reads for A and lets B read on its own', async () => {
    const g = await mountGated(activeRunning(), 'a1')
    await tickPoll(); await click('Atualizar')
    expect(g.meter.byAccount.a1).toBe(2)
    g.view.rerender(<ListingSyncPanel key="a2" accountId="a2" canManage />)
    await flush()
    expect(g.meter.byAccount.a2).toBe(1) // B is not blocked by A's pending read
    await g.settle(1, ok(activeRunning())) // A's late answer
    await act(async () => { await vi.advanceTimersByTimeAsync(20000) })
    expect(g.meter.byAccount.a1).toBe(2) // the queued follow-up of A never ran
    await g.settle(g.pending.findIndex((p) => p.account === 'a2'), ok(status({}, { health: 'HEALTHY', latestRun: run({ accountId: 'a2', counters: counters({ total: 7, found: 7 }) }), latestFullRun: run() })))
    expect(screen.getByRole('group', { name: 'Última execução' })).toHaveTextContent('Total: 7')
    expect(screen.queryByRole('group', { name: 'Execução em andamento' })).not.toBeInTheDocument()
  })

  it('G: unmounting with a read and a queued refresh pending emits no further read and leaves no timer', async () => {
    const g = await mountGated(activeRunning())
    await tickPoll(); await click('Atualizar')
    g.view.unmount()
    await g.settle(1, ok(activeRunning()))
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(g.meter.started).toBe(2); expect(vi.getTimerCount()).toBe(0)
  })

  it('H: a terminal answer ends polling and a manual refresh does not restart or duplicate it', async () => {
    const g = await mountGated(activeRunning())
    await tickPoll()
    await g.settle(1, ok(terminal()))
    expect(screen.getByText('Concluída')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(g.meter.started).toBe(2)
    await click('Atualizar'); await g.settle(2, ok(terminal()))
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(g.meter.started).toBe(3); expect(vi.getTimerCount()).toBe(0); expect(g.meter.max).toBe(1)
  })
})
