import { Fragment, useEffect, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { apiClient } from '../api/client'
import type {
  ProductListItemResponse, ProductListResponse, ProposalContentRequest, QuoteCancelRequest, QuoteDiscountKind,
  QuoteItemRequest, QuoteItemResponse, QuotePdfHistoryItemResponse, QuotePdfMetadataResponse, QuotePdfReissueRequest,
  QuoteResponse, QuoteReviseRequest, QuoteRevisionResponse, QuoteRevisionStatus, QuoteVersionedRequest,
  SalesChannelResponse, TechnicalHighlightRequest,
} from '../api/types'
import { useSession } from '../auth/useSession'
import { quoteErrorMessage } from './quoteErrors'

const STATUS_LABELS: Record<QuoteRevisionStatus, string> = {
  GENERATED: 'Gerado', SENT: 'Enviado', NEGOTIATING: 'Em negociação', APPROVED: 'Aprovado',
  CANCELED: 'Cancelado', EXPIRED: 'Expirado', SUPERSEDED: 'Substituído',
}
const OUTCOME_LABELS: Record<string, string> = { WON: 'Ganho', LOST: 'Perdido', OPEN: 'Em aberto' }
const TERMINAL_STATUSES: QuoteRevisionStatus[] = ['APPROVED', 'CANCELED', 'EXPIRED', 'SUPERSEDED']
const DISCOUNT_LABELS: Record<QuoteDiscountKind, string> = { None: 'Sem desconto', Percent: 'Percentual', Amount: 'Valor fixo' }

function money(value: number | string): string {
  return Number(value).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 2 })
}
function percent(value: number | string): string {
  return `${(Number(value) * 100).toLocaleString('pt-BR', { maximumFractionDigits: 2 })}%`
}
function dateTime(value: string): string {
  return new Date(value).toLocaleString('pt-BR')
}
function dateOnly(value: string): string {
  return new Date(`${value}T00:00:00`).toLocaleDateString('pt-BR')
}
function isExpired(validUntil: string): boolean {
  return new Date(`${validUntil}T00:00:00`).getTime() < Date.now()
}
function numberOrNull(value: string): number | null { return value.trim() === '' ? null : Number(value) }

/** For an ad-hoc line the server mirrors AdHocDescription into both ProductNameSnapshot and
 * Description (QuotingEndpoints.ResolveNewLineAsync) — showing both would just repeat the same
 * text, so the description is only appended when it actually adds information. */
function itemLabel(name: string, description: string | null): string {
  return description && description !== name ? `${name} — ${description}` : name
}

type ExistingLineDraft = {
  id: string
  productNameSnapshot: string
  description: string | null
  quantity: string
  desiredMarginPercent: string
  manualPriceOverride: string
  discountKind: QuoteDiscountKind
  discountValue: string
  removed: boolean
}

type NewLineDraft = {
  key: number
  kind: 'product' | 'adhoc'
  productId: string
  adHocDescription: string
  manualUnitCost: string
  quantity: string
  desiredMarginPercent: string
  manualPriceOverride: string
  discountKind: QuoteDiscountKind
  discountValue: string
}

let nextKey = 1
const blankNewLine = (): NewLineDraft => ({
  key: nextKey++, kind: 'product', productId: '', adHocDescription: '', manualUnitCost: '',
  quantity: '1', desiredMarginPercent: '30', manualPriceOverride: '', discountKind: 'None', discountValue: '0',
})

function toExistingDraft(item: QuoteItemResponse): ExistingLineDraft {
  // CLAUDE.md §2.4: server fields are fractions (0.30 = 30%) — displayed to the operator as a
  // percentage, converted back to a fraction on submit (see submitRevise).
  return {
    id: item.id, productNameSnapshot: item.productNameSnapshot, description: item.description,
    quantity: String(item.quantity), desiredMarginPercent: String(Number(item.desiredMarginPercent) * 100),
    manualPriceOverride: item.manualPriceOverride === null ? '' : String(item.manualPriceOverride),
    discountKind: item.discountKind, discountValue: String(item.discountKind === 'Percent' ? Number(item.discountValue) * 100 : item.discountValue), removed: false,
  }
}

export function QuoteDetailPage() {
  const { quoteId } = useParams<{ quoteId: string }>()
  const { session } = useSession()
  const roles = session.status === 'authenticated' ? session.user.roles : []
  const canManage = roles.includes('Owner') || roles.includes('Operator')

  const [quote, setQuote] = useState<QuoteResponse | null>(null)
  const [revisions, setRevisions] = useState<QuoteRevisionResponse[]>([])
  const [pdf, setPdf] = useState<QuotePdfMetadataResponse | null>(null)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)

  const [reviseMode, setReviseMode] = useState(false)
  const [channels, setChannels] = useState<SalesChannelResponse[]>([])
  const [reviseSalesChannelId, setReviseSalesChannelId] = useState('')
  const [reviseValidityOverride, setReviseValidityOverride] = useState('')
  const [existingLines, setExistingLines] = useState<ExistingLineDraft[]>([])
  const [newLines, setNewLines] = useState<NewLineDraft[]>([])
  const [productSearch, setProductSearch] = useState('')
  const [products, setProducts] = useState<ProductListItemResponse[]>([])

  // S7 proposal-content snapshot (ADR-0016 §7): editable draft for the revise form, cloned from
  // the current revision — an omitted field on submit means "keep the current value", never
  // "clear it" (matches the server's own clone-then-override contract).
  const [proposalTitle, setProposalTitle] = useState('')
  const [proposalScope, setProposalScope] = useState('')
  const [proposalHighlights, setProposalHighlights] = useState<TechnicalHighlightRequest[]>([])
  const [proposalTechnicalNotes, setProposalTechnicalNotes] = useState('')
  const [proposalOutOfScope, setProposalOutOfScope] = useState('')
  const [proposalPaymentTerms, setProposalPaymentTerms] = useState('')
  const [proposalDeliveryTerms, setProposalDeliveryTerms] = useState('')
  const [proposalWarranty, setProposalWarranty] = useState('')
  const [proposalNotes, setProposalNotes] = useState('')

  // Historical revision PDF (mission §61-64/§115-116): every document ever issued per revision,
  // loaded lazily per revision id — never assumed to be only the current one.
  const [historyByRevision, setHistoryByRevision] = useState<Record<string, QuotePdfHistoryItemResponse[]>>({})
  const [expandedRevisionId, setExpandedRevisionId] = useState<string | null>(null)

  async function load() {
    if (!quoteId) return
    const result = await apiClient.get<QuoteResponse>(`/api/quotes/${quoteId}`)
    if (!result.ok) { setMessage(quoteErrorMessage(result.error)); return }
    setQuote(result.data)
    setMessage('')

    const revisionsResult = await apiClient.get<QuoteRevisionResponse[]>(`/api/quotes/${quoteId}/revisions`)
    if (revisionsResult.ok) setRevisions(revisionsResult.data)

    const pdfResult = await apiClient.get<QuotePdfMetadataResponse>(`/api/quotes/${quoteId}/revisions/${result.data.currentRevision.id}/pdf`)
    setPdf(pdfResult.ok ? pdfResult.data : null)
  }

  // oxlint-disable-next-line react/set-state-in-effect, react-hooks/exhaustive-deps
  useEffect(() => { void load() }, [quoteId])

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const term = productSearch.trim()
      const params = new URLSearchParams({ status: 'active', page: '1', pageSize: '10' })
      if (term) params.set('search', term)
      void apiClient.get<ProductListResponse>(`/api/products?${params}`, controller.signal)
        .then(response => { if (response.ok) setProducts(response.data.items) })
    }, 250)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [productSearch])

  function startRevise() {
    if (!quote) return
    setChannels([])
    void apiClient.get<SalesChannelResponse[]>('/api/pricing/channels?includeInactive=false')
      .then(result => { if (result.ok) setChannels(result.data) })
    setReviseSalesChannelId(quote.currentRevision.salesChannelId)
    setReviseValidityOverride('')
    setExistingLines(quote.currentRevision.items.map(toExistingDraft))
    setNewLines([])
    // STATE-MACHINES §2 step 2: the form starts pre-filled with the CURRENT revision's proposal
    // content — the operator edits from there, and only what actually changes is sent (server
    // clones the rest verbatim; see submitRevise).
    const content = quote.currentRevision.proposalContent
    setProposalTitle(content.title ?? '')
    setProposalScope(content.scope ?? '')
    setProposalHighlights(content.technicalHighlights.map(h => ({ label: h.label, value: h.value })))
    setProposalTechnicalNotes(content.technicalNotes ?? '')
    setProposalOutOfScope(content.outOfScope ?? '')
    setProposalPaymentTerms(content.paymentTerms ?? '')
    setProposalDeliveryTerms(content.deliveryTerms ?? '')
    setProposalWarranty(content.warranty ?? '')
    setProposalNotes(content.notes ?? '')
    setReviseMode(true)
  }

  async function loadHistory(revisionId: string) {
    const result = await apiClient.get<QuotePdfHistoryItemResponse[]>(`/api/quotes/${quoteId}/revisions/${revisionId}/pdf/history`)
    if (result.ok) setHistoryByRevision(cur => ({ ...cur, [revisionId]: result.data }))
  }

  function toggleHistory(revisionId: string) {
    if (expandedRevisionId === revisionId) { setExpandedRevisionId(null); return }
    setExpandedRevisionId(revisionId)
    if (!historyByRevision[revisionId]) void loadHistory(revisionId)
  }

  async function generatePdfForRevision(revisionId: string) {
    const result = await apiClient.post<QuotePdfMetadataResponse>(`/api/quotes/${quoteId}/revisions/${revisionId}/pdf`)
    if (result.ok) { if (revisionId === quote?.currentRevision.id) setPdf(result.data); await loadHistory(revisionId); window.open(result.data.downloadUrl, '_blank') }
    else setMessage(quoteErrorMessage(result.error))
  }

  /** ADR-0016 §5: reissuing an ALREADY-issued revision creates a new, additional document — the
   * customer may already hold the previous one. Requires explicit operator confirmation AND a
   * non-blank reason (F-03): the API rejects a missing/blank reason outright, but a blank reason
   * must be impossible to even SUBMIT from this UI — the prompt re-asks until the operator either
   * types something or cancels the whole action; there is no auto-generated placeholder. */
  async function reissuePdfForRevision(revisionId: string) {
    const existing = historyByRevision[revisionId]
    if (existing && existing.length > 0) {
      const confirmed = window.confirm('Esta revisão já possui um documento emitido, que pode estar em posse do cliente. Reemitir cria uma NOVA versão do documento. Continuar?')
      if (!confirmed) return
    }
    const reason = promptForReissueReason()
    if (reason === null) return
    const result = await apiClient.post<QuotePdfMetadataResponse>(`/api/quotes/${quoteId}/revisions/${revisionId}/pdf/reissue`, { reason } satisfies QuotePdfReissueRequest)
    if (result.ok) { if (revisionId === quote?.currentRevision.id) setPdf(result.data); await loadHistory(revisionId); window.open(result.data.downloadUrl, '_blank') }
    else setMessage(quoteErrorMessage(result.error))
  }

  /** Loops until the operator either types a non-blank reason or explicitly cancels the prompt —
   * returns `null` only on an explicit cancel, never on a blank submission. */
  function promptForReissueReason(): string | null {
    let value = window.prompt('Motivo da reemissão (obrigatório):')
    while (value !== null && value.trim().length === 0) {
      value = window.prompt('O motivo é obrigatório. Motivo da reemissão:')
    }
    return value === null ? null : value.trim()
  }

  async function runAction(action: () => Promise<void>) {
    setBusy(true)
    setMessage('')
    try { await action() }
    finally { setBusy(false) }
  }

  async function send() {
    if (!quote) return
    const result = await apiClient.post<QuoteResponse>(`/api/quotes/${quote.id}/send`, { quoteVersion: quote.version } satisfies QuoteVersionedRequest)
    if (result.ok) await load()
    else setMessage(quoteErrorMessage(result.error))
  }

  async function negotiate() {
    if (!quote) return
    const result = await apiClient.post<QuoteResponse>(`/api/quotes/${quote.id}/negotiate`, { quoteVersion: quote.version } satisfies QuoteVersionedRequest)
    if (result.ok) await load()
    else setMessage(quoteErrorMessage(result.error))
  }

  async function cancel() {
    if (!quote) return
    const reason = window.prompt('Motivo do cancelamento:')
    if (reason === null) return
    const result = await apiClient.post<QuoteResponse>(`/api/quotes/${quote.id}/cancel`, { reason, quoteVersion: quote.version } satisfies QuoteCancelRequest)
    if (result.ok) await load()
    else setMessage(quoteErrorMessage(result.error))
  }

  async function approve() {
    if (!quote) return
    const result = await apiClient.post<QuoteResponse>(`/api/quotes/${quote.id}/approve`, { quoteVersion: quote.version } satisfies QuoteVersionedRequest)
    if (result.ok) await load()
    else setMessage(quoteErrorMessage(result.error))
  }

  async function generatePdf() {
    if (!quote) return
    const result = await apiClient.post<QuotePdfMetadataResponse>(`/api/quotes/${quote.id}/revisions/${quote.currentRevision.id}/pdf`)
    if (result.ok) { setPdf(result.data); window.open(result.data.downloadUrl, '_blank') }
    else setMessage(quoteErrorMessage(result.error))
  }

  async function submitRevise(event: FormEvent) {
    event.preventDefault()
    if (!quote) return
    setMessage('')
    for (const line of newLines) {
      if (line.kind === 'product' && !line.productId) { setMessage('Selecione um produto em cada novo item, ou marque como item avulso.'); return }
      if (line.kind === 'adhoc' && !line.manualUnitCost) { setMessage('Informe o custo manual de cada item avulso.'); return }
    }

    const items: QuoteItemRequest[] = [
      // CLAUDE.md §2.4: percentages are stored as fractions — divide by 100 once, here, right
      // before each line leaves the browser (both margin, and discount value when it is a %).
      ...existingLines.filter(line => !line.removed).map((line): QuoteItemRequest => ({
        sourceQuoteItemId: line.id, productId: null, adHocDescription: null, manualUnitCost: null,
        quantity: Number(line.quantity), desiredMarginPercent: Number(line.desiredMarginPercent || 0) / 100,
        manualPriceOverride: numberOrNull(line.manualPriceOverride), discountKind: line.discountKind,
        discountValue: line.discountKind === 'Percent' ? Number(line.discountValue || 0) / 100 : Number(line.discountValue || 0),
      })),
      ...newLines.map((line): QuoteItemRequest => ({
        sourceQuoteItemId: null,
        productId: line.kind === 'product' ? line.productId : null,
        adHocDescription: line.kind === 'adhoc' ? (line.adHocDescription || null) : null,
        manualUnitCost: line.kind === 'adhoc' ? numberOrNull(line.manualUnitCost) : null,
        quantity: Number(line.quantity), desiredMarginPercent: Number(line.desiredMarginPercent || 0) / 100,
        manualPriceOverride: numberOrNull(line.manualPriceOverride), discountKind: line.discountKind,
        discountValue: line.discountKind === 'Percent' ? Number(line.discountValue || 0) / 100 : Number(line.discountValue || 0),
      })),
    ]

    // STATE-MACHINES §2 step 2/4: every field is sent — the form was pre-filled from the current
    // revision in startRevise, so this is the complete edited candidate, never a sparse patch. An
    // empty string clears a field (the server treats "" as null via Trim/IsNullOrWhiteSpace).
    const proposalContent: ProposalContentRequest = {
      title: proposalTitle || null, scope: proposalScope || null,
      technicalHighlights: proposalHighlights.filter(h => h.label.trim() || h.value.trim()),
      technicalNotes: proposalTechnicalNotes || null, outOfScope: proposalOutOfScope || null,
      paymentTerms: proposalPaymentTerms || null, deliveryTerms: proposalDeliveryTerms || null,
      warranty: proposalWarranty || null, notes: proposalNotes || null,
    }

    const request: QuoteReviseRequest = {
      customerId: quote.customerId, salesChannelId: reviseSalesChannelId, items,
      validityDaysOverride: numberOrNull(reviseValidityOverride), quoteVersion: quote.version, proposalContent,
    }

    const result = await apiClient.post<QuoteResponse>(`/api/quotes/${quote.id}/revise`, request)
    if (result.ok) { setReviseMode(false); await load() }
    else setMessage(quoteErrorMessage(result.error))
  }

  if (!quote) {
    return (
      <section className="product-page">
        <Link to="/quotes">← Orçamentos</Link>
        {message && <p role="status" className="form-error">{message}</p>}
      </section>
    )
  }

  const current = quote.currentRevision
  const canApprove = !TERMINAL_STATUSES.includes(current.status)
  const canSend = current.status === 'GENERATED' || current.status === 'NEGOTIATING'
  const canNegotiate = current.status === 'GENERATED' || current.status === 'SENT'
  const canCancel = current.status === 'GENERATED' || current.status === 'SENT' || current.status === 'NEGOTIATING'

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <Link to="/quotes">← Orçamentos</Link>
          <h1>Orçamento {quote.number}{current.revisionSuffix}</h1>
          <p>{quote.customerId ? 'Cliente cadastrado' : 'Orçamento avulso'} · {OUTCOME_LABELS[quote.commercialOutcome] ?? quote.commercialOutcome}</p>
        </div>
      </div>

      <div className="split-grid">
        <div className="card">
          <h2>Situação atual</h2>
          <dl className="totals-grid">
            <div><dt>Status</dt><dd><span className="badge">{STATUS_LABELS[current.status]}</span></dd></div>
            <div><dt>Validade</dt><dd>{dateOnly(current.validUntil)}{isExpired(current.validUntil) && <span className="badge badge--danger"> expirado</span>}</dd></div>
            <div><dt>Emitido em</dt><dd>{dateTime(current.issuedAt)}</dd></div>
            <div><dt>Aprovado em</dt><dd>{current.approvedAt ? dateTime(current.approvedAt) : '—'}</dd></div>
            <div><dt>Ordem de produção</dt><dd>{quote.productionOrderStatus ?? 'Nenhuma'}</dd></div>
          </dl>

          {canManage && (
            <div className="actions-row">
              <button type="button" disabled={busy || !canSend} onClick={() => void runAction(send)}>Enviar</button>
              <button type="button" disabled={busy || !canNegotiate} onClick={() => void runAction(negotiate)}>Marcar em negociação</button>
              <button type="button" disabled={busy || !canApprove} onClick={() => void runAction(approve)}>Aprovar</button>
              <button type="button" className="button-danger" disabled={busy || !canCancel} onClick={() => void runAction(cancel)}>Cancelar</button>
              <button type="button" disabled={busy || current.status === 'CANCELED' || current.status === 'EXPIRED'} onClick={startRevise}>Criar nova revisão</button>
            </div>
          )}

          <div className="actions-row">
            {/* mission §65/§109: a Viewer may only download an already-materialized PDF — never
             * see an enabled generate action, even though the backend also rejects it (403). */}
            {canManage && <button type="button" disabled={busy} onClick={() => void runAction(generatePdf)}>{pdf ? 'Gerar novamente (PDF)' : 'Gerar PDF'}</button>}
            {pdf && <a className="button-primary" href={pdf.downloadUrl} target="_blank" rel="noreferrer">Baixar PDF</a>}
          </div>

          {message && (
            <p role="status" className="form-error">
              {message} <button type="button" onClick={() => void load()}>Atualizar</button>
            </p>
          )}
        </div>

        <div className="card">
          <h2>Totais da revisão atual</h2>
          <dl className="totals-grid">
            <div><dt>Subtotal</dt><dd>{money(current.subtotalAmount)}</dd></div>
            <div><dt>Descontos</dt><dd>{money(current.discountAmount)}</dd></div>
            <div className="totals-grid__total"><dt>Total</dt><dd>{money(current.totalAmount)}</dd></div>
            <div><dt>Custo total</dt><dd>{money(current.totalCostAmount)}</dd></div>
            <div><dt>Lucro esperado</dt><dd>{money(current.expectedProfitAmount)}</dd></div>
            <div><dt>Margem efetiva</dt><dd>{percent(current.effectiveMarginPercent)}</dd></div>
          </dl>
        </div>
      </div>

      <div className="card">
        <h2>Itens</h2>
        <table className="line-items">
          <thead>
            <tr><th>#</th><th>Item</th><th>Qtd.</th><th>Preço unit.</th><th>Desconto</th><th>Total</th><th>Margem</th><th></th></tr>
          </thead>
          <tbody>
            {current.items.map(item => (
              <Fragment key={item.id}>
                <tr>
                  <td>{item.lineNumber}</td>
                  <td>{itemLabel(item.productNameSnapshot, item.description)}</td>
                  <td>{item.quantity}</td>
                  <td>{money(item.unitPrice)}</td>
                  <td>{item.discountKind === 'None' ? '—' : money(item.discountAmount)}</td>
                  <td>{money(item.lineTotalAmount)}</td>
                  <td>{percent(item.effectiveMarginPercent)}</td>
                  <td></td>
                </tr>
                <tr>
                  <td colSpan={8} style={{ padding: 0 }}>
                    {/* "Explain this price" (ADR-0003 layer 2): the stored breakdown, read
                     * verbatim — this panel computes no money, it only formats what the server
                     * already froze on the revision. Internal-operator only; never printed on the
                     * customer PDF (that renderer never even receives this data). */}
                    <details className="explain-price">
                      <summary>Explicar preço — item {item.lineNumber}</summary>
                      <dl className="totals-grid">
                        <div><dt>Motor de custo</dt><dd>{item.costSnapshot.engineVersion}</dd></div>
                        <div><dt>Custo de materiais (antes de perdas)</dt><dd>{money(item.costSnapshot.materialCostBeforeWastage)}</dd></div>
                        <div><dt>Custo de perdas de material</dt><dd>{money(item.costSnapshot.materialWastageCost)}</dd></div>
                        <div><dt>Custo total de materiais</dt><dd>{money(item.costSnapshot.materialsTotalCost)}</dd></div>
                        {item.costSnapshot.laborMinutes !== null && (
                          <div><dt>Mão de obra</dt><dd>{item.costSnapshot.laborMinutes} min × {money(item.costSnapshot.laborHourlyRate ?? 0)}/h ({item.costSnapshot.laborRateSource}) = {money(item.costSnapshot.laborCost)}</dd></div>
                        )}
                        {item.costSnapshot.machineMinutes !== null && (
                          <div><dt>Máquina</dt><dd>{item.costSnapshot.machineMinutes} min × {money(item.costSnapshot.machineHourlyRate ?? 0)}/h = {money(item.costSnapshot.machineCost)}</dd></div>
                        )}
                        <div><dt>Custos diretos adicionais</dt><dd>{money(item.costSnapshot.additionalDirectCostsTotal)}</dd></div>
                        <div className="totals-grid__total"><dt>Custo total estimado</dt><dd>{money(item.costSnapshot.totalEstimatedCost)}</dd></div>
                        <div><dt>Quantidade de saída</dt><dd>{item.costSnapshot.outputQuantity}</dd></div>
                        <div><dt>Custo unitário estimado</dt><dd>{money(item.costSnapshot.estimatedUnitCost)}</dd></div>
                      </dl>
                      {item.costSnapshot.materials.length > 0 && (
                        <>
                          <h4>Materiais</h4>
                          <table className="line-items">
                            <thead><tr><th>Insumo</th><th>Qtd. informada</th><th>Qtd. normalizada</th><th>Perda</th><th>Custo/un.</th><th>Antes da perda</th><th>Após a perda</th></tr></thead>
                            <tbody>
                              {item.costSnapshot.materials.map((material, index) => (
                                <tr key={index}>
                                  <td>{material.supplyName} ({material.supplyCode})</td>
                                  <td>{material.enteredQuantity} {material.enteredUnit}</td>
                                  <td>{material.normalizedQuantityBaseUnit} {material.baseUnit}</td>
                                  <td>{percent(material.wastagePercent)}</td>
                                  <td>{money(material.unitCostBaseUnit)}</td>
                                  <td>{money(material.costBeforeWastage)}</td>
                                  <td>{money(material.costAfterWastage)}{material.exceededCurrentStockAtIssue && <span className="badge badge--danger"> estoque insuficiente na emissão</span>}</td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </>
                      )}
                      {item.costSnapshot.additionalCosts.length > 0 && (
                        <>
                          <h4>Custos adicionais</h4>
                          <ul>
                            {item.costSnapshot.additionalCosts.map((cost, index) => <li key={index}>{cost.description}: {money(cost.amount)}</li>)}
                          </ul>
                        </>
                      )}
                    </details>
                  </td>
                </tr>
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>

      {(current.proposalContent.title || current.proposalContent.scope || current.proposalContent.technicalHighlights.length > 0
        || current.proposalContent.technicalNotes || current.proposalContent.outOfScope || current.proposalContent.paymentTerms
        || current.proposalContent.deliveryTerms || current.proposalContent.warranty || current.proposalContent.notes) && (
        <div className="card">
          <h2>Conteúdo da proposta</h2>
          <dl className="totals-grid">
            {current.proposalContent.title && <div><dt>Título</dt><dd>{current.proposalContent.title}</dd></div>}
            {current.proposalContent.scope && <div><dt>Escopo</dt><dd>{current.proposalContent.scope}</dd></div>}
            {current.proposalContent.paymentTerms && <div><dt>Condições de pagamento</dt><dd>{current.proposalContent.paymentTerms}</dd></div>}
            {current.proposalContent.deliveryTerms && <div><dt>Condições de entrega</dt><dd>{current.proposalContent.deliveryTerms}</dd></div>}
            {current.proposalContent.warranty && <div><dt>Garantia</dt><dd>{current.proposalContent.warranty}</dd></div>}
          </dl>
          {current.proposalContent.technicalHighlights.length > 0 && (
            <dl className="totals-grid">
              {current.proposalContent.technicalHighlights.map((h, i) => <div key={i}><dt>{h.label}</dt><dd>{h.value}</dd></div>)}
            </dl>
          )}
          {current.proposalContent.technicalNotes && <p>{current.proposalContent.technicalNotes}</p>}
          {current.proposalContent.outOfScope && <p><strong>Não incluso:</strong> {current.proposalContent.outOfScope}</p>}
          {current.proposalContent.notes && <p><strong>Observações:</strong> {current.proposalContent.notes}</p>}
        </div>
      )}

      {reviseMode && (
        <form className="card form-stack" onSubmit={submitRevise} aria-labelledby="revise-heading">
          <div className="section-heading">
            <div><h2 id="revise-heading">Nova revisão</h2><p>Itens omitidos são removidos. Para trocar o produto de um item, remova-o e adicione um novo.</p></div>
            <button type="button" onClick={() => setReviseMode(false)}>Cancelar edição</button>
          </div>

          <label htmlFor="revise-channel">Canal de vendas</label>
          <select id="revise-channel" required value={reviseSalesChannelId} onChange={e => setReviseSalesChannelId(e.target.value)}>
            <option value="">Selecione</option>
            {channels.map(channel => <option key={channel.id} value={channel.id}>{channel.name}</option>)}
          </select>

          <label htmlFor="revise-validity">Validade personalizada (dias, opcional)</label>
          <input id="revise-validity" type="number" min="1" step="1" value={reviseValidityOverride} onChange={e => setReviseValidityOverride(e.target.value)} placeholder="Usar padrão do sistema" />

          <h3>Conteúdo da proposta</h3>
          <p>Campos opcionais — usados no PDF do orçamento (DEFAULT-PROPOSAL-TEMPLATE).</p>
          <label htmlFor="proposal-title">Título do projeto</label>
          <input id="proposal-title" value={proposalTitle} onChange={e => setProposalTitle(e.target.value)} placeholder="Ex.: Peças para drone" />

          <label htmlFor="proposal-scope">Escopo</label>
          <textarea id="proposal-scope" value={proposalScope} onChange={e => setProposalScope(e.target.value)} rows={2} />

          <div className="section-heading">
            <label id="proposal-highlights-label">Destaques técnicos</label>
            <button type="button" onClick={() => setProposalHighlights(cur => [...cur, { label: '', value: '' }])}>Adicionar destaque</button>
          </div>
          {proposalHighlights.map((highlight, index) => (
            <div className="inline-form" key={index}>
              <div>
                <label htmlFor={`highlight-label-${index}`}>Rótulo</label>
                <input id={`highlight-label-${index}`} value={highlight.label} placeholder="Ex.: Material"
                  onChange={e => setProposalHighlights(cur => cur.map((h, i) => i === index ? { ...h, label: e.target.value } : h))} />
              </div>
              <div>
                <label htmlFor={`highlight-value-${index}`}>Valor</label>
                <input id={`highlight-value-${index}`} value={highlight.value} placeholder="Ex.: PETG"
                  onChange={e => setProposalHighlights(cur => cur.map((h, i) => i === index ? { ...h, value: e.target.value } : h))} />
              </div>
              <button type="button" className="button-danger" onClick={() => setProposalHighlights(cur => cur.filter((_, i) => i !== index))}>Remover</button>
            </div>
          ))}

          <label htmlFor="proposal-technical-notes">Notas técnicas</label>
          <textarea id="proposal-technical-notes" value={proposalTechnicalNotes} onChange={e => setProposalTechnicalNotes(e.target.value)} rows={2} />

          <div className="inline-form">
            <div><label htmlFor="proposal-payment-terms">Condições de pagamento</label><input id="proposal-payment-terms" value={proposalPaymentTerms} onChange={e => setProposalPaymentTerms(e.target.value)} /></div>
            <div><label htmlFor="proposal-delivery-terms">Condições de entrega</label><input id="proposal-delivery-terms" value={proposalDeliveryTerms} onChange={e => setProposalDeliveryTerms(e.target.value)} /></div>
            <div><label htmlFor="proposal-warranty">Garantia</label><input id="proposal-warranty" value={proposalWarranty} onChange={e => setProposalWarranty(e.target.value)} /></div>
          </div>

          <label htmlFor="proposal-out-of-scope">Não incluso</label>
          <textarea id="proposal-out-of-scope" value={proposalOutOfScope} onChange={e => setProposalOutOfScope(e.target.value)} rows={2} />

          <label htmlFor="proposal-notes">Observações</label>
          <textarea id="proposal-notes" value={proposalNotes} onChange={e => setProposalNotes(e.target.value)} rows={2} />

          <h3>Itens da revisão anterior</h3>
          <div className="cost-lines">
            {existingLines.map((line, index) => (
              <fieldset className="cost-line" key={line.id} disabled={line.removed}>
                <legend>{itemLabel(line.productNameSnapshot, line.description)}</legend>
                <div className="inline-form">
                  <div><label htmlFor={`existing-quantity-${line.id}`}>Quantidade</label><input id={`existing-quantity-${line.id}`} type="number" min="0" step="any" value={line.quantity} onChange={e => setExistingLines(cur => cur.map((l, i) => i === index ? { ...l, quantity: e.target.value } : l))} /></div>
                  <div><label htmlFor={`existing-margin-${line.id}`}>Margem desejada (%)</label><input id={`existing-margin-${line.id}`} type="number" step="any" value={line.desiredMarginPercent} onChange={e => setExistingLines(cur => cur.map((l, i) => i === index ? { ...l, desiredMarginPercent: e.target.value } : l))} /></div>
                </div>
                <div className="inline-form">
                  <div><label htmlFor={`existing-override-${line.id}`}>Preço manual (opcional)</label><input id={`existing-override-${line.id}`} type="number" min="0" step="any" value={line.manualPriceOverride} onChange={e => setExistingLines(cur => cur.map((l, i) => i === index ? { ...l, manualPriceOverride: e.target.value } : l))} /></div>
                  <div><label htmlFor={`existing-discount-kind-${line.id}`}>Desconto</label>
                    <select id={`existing-discount-kind-${line.id}`} value={line.discountKind} onChange={e => setExistingLines(cur => cur.map((l, i) => i === index ? { ...l, discountKind: e.target.value as QuoteDiscountKind } : l))}>
                      {(Object.keys(DISCOUNT_LABELS) as QuoteDiscountKind[]).map(kind => <option key={kind} value={kind}>{DISCOUNT_LABELS[kind]}</option>)}
                    </select>
                  </div>
                  {line.discountKind !== 'None' && (
                    <div><label htmlFor={`existing-discount-value-${line.id}`}>Valor do desconto</label><input id={`existing-discount-value-${line.id}`} type="number" min="0" step="any" value={line.discountValue} onChange={e => setExistingLines(cur => cur.map((l, i) => i === index ? { ...l, discountValue: e.target.value } : l))} /></div>
                  )}
                </div>
                <button type="button" className="button-danger" onClick={() => setExistingLines(cur => cur.map((l, i) => i === index ? { ...l, removed: !l.removed } : l))}>
                  {line.removed ? 'Manter este item' : 'Remover este item'}
                </button>
              </fieldset>
            ))}
          </div>

          <div className="section-heading">
            <h3>Novos itens</h3>
            <button type="button" onClick={() => setNewLines(cur => [...cur, blankNewLine()])}>Adicionar item</button>
          </div>
          <label htmlFor="revise-product-search">Buscar produto</label>
          <input id="revise-product-search" value={productSearch} onChange={e => setProductSearch(e.target.value)} placeholder="Código ou nome" />

          <div className="cost-lines">
            {newLines.map((line, index) => (
              <fieldset className="cost-line" key={line.key}>
                <legend>Novo item {index + 1}</legend>
                <div className="inline-form">
                  <label><input type="radio" name={`new-kind-${line.key}`} checked={line.kind === 'product'} onChange={() => setNewLines(cur => cur.map(l => l.key === line.key ? { ...l, kind: 'product' } : l))} /> Produto cadastrado</label>
                  <label><input type="radio" name={`new-kind-${line.key}`} checked={line.kind === 'adhoc'} onChange={() => setNewLines(cur => cur.map(l => l.key === line.key ? { ...l, kind: 'adhoc' } : l))} /> Item avulso</label>
                </div>
                {line.kind === 'product' ? (
                  <select value={line.productId} onChange={e => setNewLines(cur => cur.map(l => l.key === line.key ? { ...l, productId: e.target.value } : l))} required>
                    <option value="">Selecione</option>
                    {products.map(product => <option key={product.id} value={product.id}>{product.code} · {product.name}</option>)}
                  </select>
                ) : (
                  <>
                    <input value={line.adHocDescription} onChange={e => setNewLines(cur => cur.map(l => l.key === line.key ? { ...l, adHocDescription: e.target.value } : l))} placeholder="Descrição" />
                    <input type="number" min="0" step="any" required value={line.manualUnitCost} onChange={e => setNewLines(cur => cur.map(l => l.key === line.key ? { ...l, manualUnitCost: e.target.value } : l))} placeholder="Custo manual (R$)" />
                  </>
                )}
                <div className="inline-form">
                  <div><label>Quantidade</label><input type="number" min="0" step="any" required value={line.quantity} onChange={e => setNewLines(cur => cur.map(l => l.key === line.key ? { ...l, quantity: e.target.value } : l))} /></div>
                  <div><label>Margem desejada (%)</label><input type="number" step="any" value={line.desiredMarginPercent} onChange={e => setNewLines(cur => cur.map(l => l.key === line.key ? { ...l, desiredMarginPercent: e.target.value } : l))} /></div>
                </div>
                <button type="button" className="button-danger" onClick={() => setNewLines(cur => cur.filter(l => l.key !== line.key))}>Remover</button>
              </fieldset>
            ))}
          </div>

          <button className="button-primary" type="submit">Criar revisão</button>
        </form>
      )}

      <div className="card">
        <h2>Histórico de revisões</h2>
        <ol className="timeline">
          {revisions.map(revision => {
            const documents = historyByRevision[revision.id]
            const isExpanded = expandedRevisionId === revision.id
            return (
              <li key={revision.id} className={revision.id === current.id ? 'timeline__current' : ''}>
                <strong>{revision.displayNumber}</strong> — {STATUS_LABELS[revision.status]}
                {revision.id === current.id && <span className="badge"> atual</span>}
                <div>Emitido em {dateTime(revision.issuedAt)} · Total {money(revision.totalAmount)}</div>
                {revision.approvedAt && <div>Aprovado em {dateTime(revision.approvedAt)}</div>}
                {revision.supersededByRevisionId && <div>Substituído por outra revisão</div>}

                {/* mission §61-64/§115-116: rendering/downloading is never a lifecycle
                 * transition — every revision, current OR superseded, gets its own
                 * generate/reissue/download surface and its own document history. */}
                <div className="actions-row">
                  <button type="button" onClick={() => toggleHistory(revision.id)}>
                    {isExpanded ? 'Ocultar documentos' : 'Ver documentos'}
                  </button>
                  {canManage && (
                    <>
                      <button type="button" disabled={busy} onClick={() => void runAction(() => generatePdfForRevision(revision.id))}>Emitir PDF desta revisão</button>
                      {documents && documents.length > 0 && (
                        <button type="button" disabled={busy} onClick={() => void runAction(() => reissuePdfForRevision(revision.id))}>Reemitir PDF desta revisão</button>
                      )}
                    </>
                  )}
                </div>

                {isExpanded && (
                  <ul className="timeline">
                    {documents === undefined && <li>Carregando…</li>}
                    {documents?.length === 0 && <li>Nenhum documento gerado ainda para esta revisão.</li>}
                    {documents?.map(doc => (
                      <li key={doc.id}>
                        {dateTime(doc.issuedAt)} {doc.isCurrent && <span className="badge"> atual</span>}
                        {' '}<a href={doc.downloadUrl} target="_blank" rel="noreferrer">Baixar</a>
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            )
          })}
        </ol>
      </div>
    </section>
  )
}
