import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { QuoteDetailPage } from './QuoteDetailPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const sessionState = vi.hoisted(() => ({ roles: ['Owner'] as string[] }))
vi.mock('../auth/useSession', () => ({
  useSession: () => ({ session: { status: 'authenticated', user: { id: 'u1', email: 'owner@example.test', displayName: 'Owner', roles: sessionState.roles } } }),
}))

const baseItem = {
  id: 'i1', lineNumber: 1, sourceQuoteItemId: null, productId: null, productNameSnapshot: 'Vaso decorativo',
  description: 'Vaso decorativo', quantity: 2, unitTotalCost: 20, costEngineVersion: 'MANUAL', desiredMarginPercent: 0.4,
  salesChannelId: 'ch1', feeRuleVersionId: null, commissionPercent: 0, fixedFeeApplication: 'PerUnit', rawFixedFee: 0,
  allocatedOrderFee: 0, fixedFeePerUnit: 0, roundingPolicyApplied: 'CENT', suggestedUnitPrice: 33.33, manualPriceOverride: null,
  priceOverridden: false, unitPrice: 33.33, discountKind: 'None', discountValue: 0, discountAmount: 0, netUnitPrice: 33.33,
  lineTotalAmount: 66.66, lineCostAmount: 40, lineFeeAmount: 0, expectedProfitAmount: 26.66, effectiveMarginPercent: 0.4,
  costSnapshot: {
    engineVersion: 'MANUAL', materialCostBeforeWastage: 0, materialWastageCost: 0, materialsTotalCost: 0, laborMinutes: null,
    laborHourlyRate: null, laborRateSource: null, laborCost: 0, machineMinutes: null, machineHourlyRate: null, machineCost: 0,
    additionalDirectCostsTotal: 0, totalEstimatedCost: 20, outputQuantity: 1, estimatedUnitCost: 20, materials: [], additionalCosts: [],
  },
}

const blankProposalContent = {
  title: null, scope: null, technicalHighlights: [], technicalNotes: null, outOfScope: null,
  paymentTerms: null, deliveryTerms: null, warranty: null, notes: null,
}

const currentRevision = {
  id: 'r1', revisionIndex: 1, revisionSuffix: '', displayNumber: '260101-1', status: 'GENERATED', salesChannelId: 'ch1',
  issuedAt: '2026-01-01T00:00:00Z', validUntil: '2099-01-01', supersededByRevisionId: null, sourceRevisionId: null,
  approvedAt: null, approvedBy: null, subtotalAmount: 66.66, discountAmount: 0, totalAmount: 66.66, totalCostAmount: 40,
  expectedProfitAmount: 26.66, effectiveMarginPercent: 0.4, items: [baseItem], proposalContent: blankProposalContent,
}

const quote = {
  id: 'q1', number: '260101-1', customerId: null, version: 1, currentRevision, commercialOutcome: 'OPEN', hasEverWon: false,
  productionOrderStatus: null,
}

function mockLoad(overrides: { quote?: typeof quote; pdfFound?: boolean } = {}) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/quotes/q1') return Promise.resolve({ ok: true, data: overrides.quote ?? quote })
    if (path === '/api/quotes/q1/revisions') return Promise.resolve({ ok: true, data: [currentRevision] })
    if (path.endsWith('/pdf/history')) return Promise.resolve({ ok: true, data: [] })
    if (path.includes('/pdf')) {
      return overrides.pdfFound
        ? Promise.resolve({ ok: true, data: { id: 'doc1', quoteRevisionId: 'r1', documentKind: 'QUOTE_PDF', templateVersion: 'V1', pdfSha256: 'abc', pdfSizeBytes: 100, issuedAt: '2026-01-01T00:00:00Z', isCurrent: true, downloadUrl: '/api/quotes/q1/revisions/r1/pdf/doc1' } })
        : Promise.resolve({ ok: false, error: { status: 404, safeMessage: 'Recurso não encontrado.' } })
    }
    return Promise.resolve({ ok: false, error: { safeMessage: 'unexpected' } })
  })
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/quotes/q1']}>
      <Routes><Route path="/quotes/:quoteId" element={<QuoteDetailPage />} /></Routes>
    </MemoryRouter>,
  )
}

describe('QuoteDetailPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset()
    sessionState.roles = ['Owner']
    mockLoad()
  })

  it('loads and renders the current revision, totals and items', async () => {
    renderPage()
    expect(await screen.findByRole('heading', { name: /Orçamento 260101-1/ })).toBeInTheDocument()
    expect(screen.getByText('Gerado')).toBeInTheDocument()
    expect(screen.getByText('Vaso decorativo')).toBeInTheDocument()
    expect(screen.getAllByText('R$ 66,66').length).toBeGreaterThan(0)
  })

  it('sends a lifecycle action with the aggregate version and reloads afterwards', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: { ...quote, currentRevision: { ...currentRevision, status: 'SENT' } } })
    renderPage()
    await screen.findByRole('heading', { name: /Orçamento/ })

    // The component never trusts the POST response for display — it always reloads via GET
    // afterwards (single source of truth), so the mock's second GET reflects the new status.
    mockLoad({ quote: { ...quote, currentRevision: { ...currentRevision, status: 'SENT' } } })
    await user.click(screen.getByRole('button', { name: 'Enviar' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/quotes/q1/send', { quoteVersion: 1 }))
    expect(await screen.findByText('Enviado')).toBeInTheDocument()
  })

  it('shows a specific message for a concurrency conflict and offers a manual refresh', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: false, error: { status: 409, safeMessage: 'Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.', problem: { code: 'CONCURRENCY_CONFLICT' } } })
    renderPage()
    await screen.findByRole('heading', { name: /Orçamento/ })

    await user.click(screen.getByRole('button', { name: 'Enviar' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Este orçamento foi alterado por outra pessoa')
    expect(screen.getByRole('button', { name: 'Atualizar' })).toBeInTheDocument()
  })

  it('generates a PDF and opens the download in a new tab', async () => {
    const user = userEvent.setup()
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)
    api.post.mockResolvedValue({ ok: true, data: { id: 'doc1', quoteRevisionId: 'r1', documentKind: 'QUOTE_PDF', templateVersion: 'V1', pdfSha256: 'abc', pdfSizeBytes: 100, issuedAt: '2026-01-01T00:00:00Z', downloadUrl: '/api/quotes/q1/revisions/r1/pdf' } })
    renderPage()
    await screen.findByRole('heading', { name: /Orçamento/ })

    await user.click(screen.getByRole('button', { name: 'Gerar PDF' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/quotes/q1/revisions/r1/pdf'))
    expect(openSpy).toHaveBeenCalledWith('/api/quotes/q1/revisions/r1/pdf', '_blank')
    expect(await screen.findByRole('link', { name: 'Baixar PDF' })).toBeInTheDocument()
  })

  it('reissue: prompts for a reason and sends the trimmed value to the API', async () => {
    const user = userEvent.setup()
    const existingDoc = { id: 'doc0', issuedAt: '2026-01-01T00:00:00Z', isCurrent: true, pdfSha256: 'abc', downloadUrl: '/api/quotes/q1/revisions/r1/pdf/doc0' }
    api.get.mockImplementation((path: string) => {
      if (path === '/api/quotes/q1') return Promise.resolve({ ok: true, data: quote })
      if (path === '/api/quotes/q1/revisions') return Promise.resolve({ ok: true, data: [currentRevision] })
      if (path.endsWith('/pdf/history')) return Promise.resolve({ ok: true, data: [existingDoc] })
      if (path.includes('/pdf')) return Promise.resolve({ ok: false, error: { status: 404, safeMessage: 'Recurso não encontrado.' } })
      return Promise.resolve({ ok: false, error: { safeMessage: 'unexpected' } })
    })
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const promptSpy = vi.spyOn(window, 'prompt').mockReturnValue('  Endereço de entrega corrigido.  ')
    api.post.mockResolvedValue({
      ok: true,
      data: { id: 'doc1', quoteRevisionId: 'r1', documentTypeCode: 'QUOTE', documentTemplateVersionId: 'v1', pdfSha256: 'def', pdfSizeBytes: 100, issuedAt: '2026-01-02T00:00:00Z', isCurrent: true, downloadUrl: '/api/quotes/q1/revisions/r1/pdf/doc1' },
    })

    renderPage()
    await screen.findByRole('heading', { name: /Orçamento/ })
    await user.click(screen.getByRole('button', { name: 'Ver documentos' }))
    await screen.findByRole('button', { name: 'Reemitir PDF desta revisão' })

    await user.click(screen.getByRole('button', { name: 'Reemitir PDF desta revisão' }))

    expect(promptSpy).toHaveBeenCalled()
    await waitFor(() => expect(api.post).toHaveBeenCalledWith(
      '/api/quotes/q1/revisions/r1/pdf/reissue',
      { reason: 'Endereço de entrega corrigido.' },
    ))
    expect(openSpy).toHaveBeenCalledWith('/api/quotes/q1/revisions/r1/pdf/doc1', '_blank')
  })

  it('reissue: a blank reason is impossible to submit — the prompt re-asks until non-blank or cancelled', async () => {
    const user = userEvent.setup()
    const existingDoc = { id: 'doc0', issuedAt: '2026-01-01T00:00:00Z', isCurrent: true, pdfSha256: 'abc', downloadUrl: '/api/quotes/q1/revisions/r1/pdf/doc0' }
    api.get.mockImplementation((path: string) => {
      if (path === '/api/quotes/q1') return Promise.resolve({ ok: true, data: quote })
      if (path === '/api/quotes/q1/revisions') return Promise.resolve({ ok: true, data: [currentRevision] })
      if (path.endsWith('/pdf/history')) return Promise.resolve({ ok: true, data: [existingDoc] })
      if (path.includes('/pdf')) return Promise.resolve({ ok: false, error: { status: 404, safeMessage: 'Recurso não encontrado.' } })
      return Promise.resolve({ ok: false, error: { safeMessage: 'unexpected' } })
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const promptSpy = vi.spyOn(window, 'prompt')
      .mockReturnValueOnce('   ') // blank — must re-prompt, never submit
      .mockReturnValueOnce(null) // operator gives up — must abort, never submit

    renderPage()
    await screen.findByRole('heading', { name: /Orçamento/ })
    await user.click(screen.getByRole('button', { name: 'Ver documentos' }))
    await screen.findByRole('button', { name: 'Reemitir PDF desta revisão' })

    await user.click(screen.getByRole('button', { name: 'Reemitir PDF desta revisão' }))

    expect(promptSpy).toHaveBeenCalledTimes(2)
    expect(api.post).not.toHaveBeenCalledWith('/api/quotes/q1/revisions/r1/pdf/reissue', expect.anything())
  })

  it('reissue: a Viewer sees no reissue (or generate) button at all', async () => {
    sessionState.roles = ['Viewer']
    const existingDoc = { id: 'doc0', issuedAt: '2026-01-01T00:00:00Z', isCurrent: true, pdfSha256: 'abc', downloadUrl: '/api/quotes/q1/revisions/r1/pdf/doc0' }
    api.get.mockImplementation((path: string) => {
      if (path === '/api/quotes/q1') return Promise.resolve({ ok: true, data: quote })
      if (path === '/api/quotes/q1/revisions') return Promise.resolve({ ok: true, data: [currentRevision] })
      if (path.endsWith('/pdf/history')) return Promise.resolve({ ok: true, data: [existingDoc] })
      if (path.includes('/pdf')) return Promise.resolve({ ok: false, error: { status: 404, safeMessage: 'Recurso não encontrado.' } })
      return Promise.resolve({ ok: false, error: { safeMessage: 'unexpected' } })
    })

    renderPage()
    await screen.findByRole('heading', { name: /Orçamento/ })
    expect(screen.queryByRole('button', { name: 'Reemitir PDF desta revisão' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Emitir PDF desta revisão' })).not.toBeInTheDocument()
  })

  it('disables lifecycle actions that are not valid for the current status', async () => {
    mockLoad({ quote: { ...quote, currentRevision: { ...currentRevision, status: 'CANCELED' } } })
    renderPage()
    await screen.findByRole('heading', { name: /Orçamento/ })
    expect(screen.getByRole('button', { name: 'Enviar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Aprovar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
  })
})
