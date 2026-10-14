import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { SkuMappingsPanel } from './SkuMappingsPanel'

const SKU_PAGE_SIZE = 25 // literal on purpose: the frozen page size, never read back from the component

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const mapping = (o: Record<string, unknown> = {}) => ({
  id: 'm1', marketplaceAccountId: 'a1', externalSku: 'SKU-1', productId: 'p1', productCode: 'P-001', productName: 'Caneca', active: true, confirmedAt: '2026-10-01T10:00:00Z',
  invalidatedAt: null, invalidatedReason: null, version: 7, ...o,
})
const page = (items: unknown[], o: Record<string, unknown> = {}) => ({ items, page: 1, pageSize: SKU_PAGE_SIZE, total: items.length, ...o })
const LIST = '/api/commerce/marketplace-accounts/a1/sku-mappings'
const listCalls = () => api.get.mock.calls.map(([p]) => String(p)).filter((p) => p.startsWith(LIST))
const queryOf = (url: string) => new URL(url, 'https://x.test').searchParams
const refusal = (status: number, safeMessage: string, problem: Record<string, unknown> = {}) => ({ ok: false, error: { status, kind: 'problem', safeMessage, problem: { status, ...problem } } })
const CONFLICT = 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.'

const state = vi.hoisted(() => ({ data: null as unknown }))
function serve(data: unknown) { state.data = data; api.get.mockImplementation(() => Promise.resolve({ ok: true, data: state.data })) }

async function mount(canManage: boolean, accountId = 'a1') {
  cleanup() // one mounted panel per test step
  render(<SkuMappingsPanel key={accountId} accountId={accountId} canManage={canManage} />)
  await screen.findByRole('heading', { name: 'SKUs confirmados' })
  await waitFor(() => expect(screen.queryByText('Carregando SKUs confirmados…')).not.toBeInTheDocument())
}

beforeEach(() => { vi.clearAllMocks(); api.post.mockResolvedValue({ ok: true, status: 204, data: undefined }) })

describe('SkuMappingsPanel — listing', () => {
  it('lists active mappings with SKU, product code and name, state and dates, requesting active=true by default', async () => {
    serve(page([mapping()]))
    await mount(true)
    expect(screen.getByText('SKU-1')).toBeInTheDocument()
    expect(screen.getByText('Produto: P-001 · Caneca')).toBeInTheDocument()
    expect(screen.getByText('Ativo')).toBeInTheDocument()
    expect(screen.getByText(/Confirmado em/)).toBeInTheDocument()
    const query = queryOf(listCalls()[0])
    expect(query.get('active')).toBe('true'); expect(query.get('page')).toBe('1'); expect(Number(query.get('pageSize'))).toBeLessThanOrEqual(100)
  })

  it('falls back to the product id when the composition has no product, and never shows actor ids', async () => {
    serve(page([mapping({ productCode: null, productName: null })]))
    await mount(true)
    expect(screen.getByText('Produto: p1')).toBeInTheDocument()
    expect(document.body.textContent).not.toMatch(/confirmedBy|invalidatedBy|userId/i)
  })

  it('shows inactive mappings with their invalidation time and no Desativar action', async () => {
    serve(page([mapping({ id: 'm2', active: false, invalidatedAt: '2026-10-02T10:00:00Z', invalidatedReason: 'OPERATOR_DEACTIVATED', version: 2 })]))
    const user = userEvent.setup(); await mount(true)
    await user.selectOptions(screen.getByLabelText('Mostrar'), 'false')
    await waitFor(() => expect(queryOf(listCalls().at(-1)!).get('active')).toBe('false'))
    expect(await screen.findByText('Inativo')).toBeInTheDocument()
    expect(screen.getByText(/Desativado em/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Desativar/ })).not.toBeInTheDocument()
  })

  it('"Todos" omits the active filter; empty states name the filter', async () => {
    serve(page([]))
    const user = userEvent.setup(); await mount(true)
    expect(screen.getByText('Nenhum SKU confirmado ativo para esta conta.')).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Mostrar'), 'all')
    await waitFor(() => expect(queryOf(listCalls().at(-1)!).has('active')).toBe(false))
    expect(await screen.findByText('Nenhum SKU confirmado para esta conta.')).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Mostrar'), 'false')
    expect(await screen.findByText('Nenhum SKU confirmado inativo para esta conta.')).toBeInTheDocument()
  })

  it('paginates with the server page and page size', async () => {
    serve(page(Array.from({ length: SKU_PAGE_SIZE }, (_, i) => mapping({ id: `m${i}`, externalSku: `SKU-${i}` })), { total: 60 }))
    const user = userEvent.setup(); await mount(false)
    expect(screen.getByText('Página 1 de 3')).toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    state.data = page([mapping({ id: 'mx', externalSku: 'SKU-PAGE2' })], { page: 2, total: 60 })
    await user.click(screen.getByRole('button', { name: 'Próxima' }))
    expect(await screen.findByText('SKU-PAGE2')).toBeInTheDocument()
    expect(queryOf(listCalls().at(-1)!).get('page')).toBe('2')
  })

  it('shows a safe error and no rows when the list cannot be loaded', async () => {
    api.get.mockResolvedValue({ ok: false, error: { status: 0, kind: 'network', safeMessage: 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.' } })
    render(<SkuMappingsPanel accountId="a1" canManage />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Não foi possível conectar ao servidor.')
    expect(screen.queryByRole('list')).not.toBeInTheDocument()
  })
})

describe('SkuMappingsPanel — roles', () => {
  it('Owner and Operator (canManage) see Desativar on active mappings; Viewer only reads', async () => {
    serve(page([mapping()]))
    await mount(true)
    expect(screen.getByRole('button', { name: 'Desativar SKU SKU-1' })).toBeEnabled()
    serve(page([mapping()]))
    await mount(false)
    expect(screen.getByText('SKU-1')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Desativar/ })).not.toBeInTheDocument()
  })

  it('has no confirmation form: confirming a SKU belongs to Published Items (C5)', async () => {
    serve(page([mapping()]))
    await mount(true)
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Confirmar/ })).not.toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })
})

describe('SkuMappingsPanel — deactivation (ADR-0028)', () => {
  it('POSTs the real version returned by the API, then reloads the list on 204', async () => {
    serve(page([mapping({ version: 7 })]))
    const user = userEvent.setup(); await mount(true)
    state.data = page([mapping({ active: false, invalidatedAt: '2026-10-02T10:00:00Z', version: 8 })].filter((m) => m.active))
    const before = listCalls().length
    await user.click(screen.getByRole('button', { name: 'Desativar SKU SKU-1' }))
    expect(api.post).toHaveBeenCalledTimes(1)
    expect(api.post).toHaveBeenCalledWith('/api/commerce/sku-mappings/m1/deactivate', { version: 7 })
    expect(await screen.findByText('Mapeamento do SKU SKU-1 desativado.')).toBeInTheDocument()
    await waitFor(() => expect(listCalls().length).toBe(before + 1))
    expect(await screen.findByText('Nenhum SKU confirmado ativo para esta conta.')).toBeInTheDocument()
  })

  it('a stale version (409 CONCURRENCY_CONFLICT) shows the safe sentence, reloads, and the next attempt uses the reloaded version', async () => {
    serve(page([mapping({ version: 7 })]))
    const user = userEvent.setup(); await mount(true)
    api.post.mockResolvedValueOnce(refusal(409, CONFLICT, { code: 'CONCURRENCY_CONFLICT', title: 'CONCURRENCY_CONFLICT' }))
    state.data = page([mapping({ version: 9 })])
    await user.click(screen.getByRole('button', { name: 'Desativar SKU SKU-1' }))
    expect(await screen.findByText(CONFLICT)).toBeInTheDocument()
    await waitFor(() => expect(listCalls().length).toBe(2))
    await user.click(await screen.findByRole('button', { name: 'Desativar SKU SKU-1' }))
    expect(api.post).toHaveBeenLastCalledWith('/api/commerce/sku-mappings/m1/deactivate', { version: 9 })
  })

  it('a 404 shows the safe sentence and reloads', async () => {
    serve(page([mapping()]))
    const user = userEvent.setup(); await mount(true)
    api.post.mockResolvedValueOnce(refusal(404, 'Recurso não encontrado.'))
    state.data = page([])
    await user.click(screen.getByRole('button', { name: 'Desativar SKU SKU-1' }))
    expect(await screen.findByText('Recurso não encontrado.')).toBeInTheDocument()
    expect(await screen.findByText('Nenhum SKU confirmado ativo para esta conta.')).toBeInTheDocument()
  })

  it('SKU_MAPPING_NOT_ACTIVE is not a public code: it has no special text and falls back to the safe client sentence', async () => {
    serve(page([mapping()]))
    const user = userEvent.setup(); await mount(true)
    api.post.mockResolvedValueOnce(refusal(409, CONFLICT, { code: 'SKU_MAPPING_NOT_ACTIVE' }))
    await user.click(screen.getByRole('button', { name: 'Desativar SKU SKU-1' }))
    expect(await screen.findByText(CONFLICT)).toBeInTheDocument()
    expect(document.body.textContent).not.toContain('SKU_MAPPING_NOT_ACTIVE')
  })

  it('a SKU mapping error code with a frozen text uses it', async () => {
    serve(page([mapping()]))
    const user = userEvent.setup(); await mount(true)
    api.post.mockResolvedValueOnce(refusal(409, CONFLICT, { code: 'SKU_MAPPING_CONFLICT' }))
    await user.click(screen.getByRole('button', { name: 'Desativar SKU SKU-1' }))
    expect(await screen.findByText('Já existe um mapeamento ativo para este SKU.')).toBeInTheDocument()
  })

  it('blocks a second click while a deactivation is in flight', async () => {
    serve(page([mapping(), mapping({ id: 'm2', externalSku: 'SKU-2' })]))
    const user = userEvent.setup(); await mount(true)
    let finish: ((value: unknown) => void) | undefined
    api.post.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve }))
    await user.click(screen.getByRole('button', { name: 'Desativar SKU SKU-1' }))
    expect(screen.getByRole('button', { name: 'Desativar SKU SKU-1' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Desativar SKU SKU-2' })).toBeDisabled()
    await act(async () => { finish?.({ ok: true, status: 204 }) })
    expect(api.post).toHaveBeenCalledTimes(1)
  })
})

describe('SkuMappingsPanel — account isolation', () => {
  it('a response for the previous account is never shown for the new one', async () => {
    let late: ((value: unknown) => void) | undefined
    api.get.mockImplementation((path: string) => {
      if (path.includes('/a1/sku-mappings')) return new Promise((resolve) => { late = resolve })
      return Promise.resolve({ ok: true, data: page([mapping({ id: 'b1', marketplaceAccountId: 'a2', externalSku: 'SKU-OF-A2' })]) })
    })
    const view = render(<SkuMappingsPanel key="a1" accountId="a1" canManage />)
    await waitFor(() => expect(late).toBeDefined())
    view.rerender(<SkuMappingsPanel key="a2" accountId="a2" canManage />)
    expect(await screen.findByText('SKU-OF-A2')).toBeInTheDocument()
    await act(async () => { late?.({ ok: true, data: page([mapping({ externalSku: 'SKU-OF-A1' })]) }) })
    expect(screen.queryByText('SKU-OF-A1')).not.toBeInTheDocument()
    expect(screen.getByText('SKU-OF-A2')).toBeInTheDocument()
  })
})
