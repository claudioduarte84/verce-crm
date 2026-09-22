import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { apiClient } from '../api/client'
import type {
  CustomerListItemResponse, CustomerListResponse, ProductListItemResponse, ProductListResponse,
  ProposalContentRequest, QuoteCreateRequest, QuoteDiscountKind, QuoteItemRequest, QuoteResponse,
  SalesChannelResponse, TechnicalHighlightRequest,
} from '../api/types'
import { quoteErrorMessage } from './quoteErrors'

const DISCOUNT_LABELS: Record<QuoteDiscountKind, string> = { None: 'Sem desconto', Percent: 'Percentual', Amount: 'Valor fixo' }

type ItemDraft = {
  key: number
  kind: 'product' | 'adhoc'
  productId: string
  productLabel: string
  adHocDescription: string
  manualUnitCost: string
  quantity: string
  desiredMarginPercent: string
  manualPriceOverride: string
  discountKind: QuoteDiscountKind
  discountValue: string
}

let nextKey = 1
const blankItem = (): ItemDraft => ({
  key: nextKey++, kind: 'product', productId: '', productLabel: '', adHocDescription: '', manualUnitCost: '',
  quantity: '1', desiredMarginPercent: '30', manualPriceOverride: '', discountKind: 'None', discountValue: '0',
})

function numberOrNull(value: string): number | null { return value.trim() === '' ? null : Number(value) }

export function QuoteNewPage() {
  const navigate = useNavigate()
  const [customerSearch, setCustomerSearch] = useState('')
  const [customers, setCustomers] = useState<CustomerListItemResponse[]>([])
  const [customerId, setCustomerId] = useState('')
  const [customerLabel, setCustomerLabel] = useState('')

  const [channels, setChannels] = useState<SalesChannelResponse[]>([])
  const [salesChannelId, setSalesChannelId] = useState('')

  const [productSearch, setProductSearch] = useState('')
  const [products, setProducts] = useState<ProductListItemResponse[]>([])

  const [items, setItems] = useState<ItemDraft[]>([blankItem()])
  const [validityDaysOverride, setValidityDaysOverride] = useState('')
  const [message, setMessage] = useState('')
  const [submitting, setSubmitting] = useState(false)

  // S7 proposal-content snapshot (ADR-0016 §7): all optional. Payment/delivery terms and
  // warranty default from Settings on the server ONLY when left blank here — never typed by the
  // frontend and never re-read after the revision is created.
  const [proposalTitle, setProposalTitle] = useState('')
  const [proposalScope, setProposalScope] = useState('')
  const [proposalHighlights, setProposalHighlights] = useState<TechnicalHighlightRequest[]>([])
  const [proposalTechnicalNotes, setProposalTechnicalNotes] = useState('')
  const [proposalOutOfScope, setProposalOutOfScope] = useState('')
  const [proposalPaymentTerms, setProposalPaymentTerms] = useState('')
  const [proposalDeliveryTerms, setProposalDeliveryTerms] = useState('')
  const [proposalWarranty, setProposalWarranty] = useState('')
  const [proposalNotes, setProposalNotes] = useState('')

  useEffect(() => {
    void apiClient.get<SalesChannelResponse[]>('/api/pricing/channels?includeInactive=false')
      .then(result => { if (result.ok) setChannels(result.data) })
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const term = customerSearch.trim()
      const params = new URLSearchParams({ page: '1', pageSize: '10' })
      if (term) params.set('search', term)
      void apiClient.get<CustomerListResponse>(`/api/customers?${params}`, controller.signal)
        .then(result => { if (result.ok) setCustomers(result.data.items) })
    }, 250)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [customerSearch])

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const term = productSearch.trim()
      const params = new URLSearchParams({ status: 'active', page: '1', pageSize: '10' })
      if (term) params.set('search', term)
      void apiClient.get<ProductListResponse>(`/api/products?${params}`, controller.signal)
        .then(result => { if (result.ok) setProducts(result.data.items) })
    }, 250)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [productSearch])

  function updateItem(key: number, change: Partial<ItemDraft>) {
    setItems(current => current.map(item => (item.key === key ? { ...item, ...change } : item)))
  }

  function addItem() {
    setItems(current => [...current, blankItem()])
  }

  function removeItem(key: number) {
    setItems(current => current.filter(item => item.key !== key))
  }

  function validate(): string | null {
    if (!salesChannelId) return 'Selecione um canal de vendas.'
    if (items.length === 0) return 'Adicione ao menos um item.'
    for (const item of items) {
      if (item.kind === 'product' && !item.productId) return 'Selecione um produto em cada linha, ou marque como item avulso.'
      if (item.kind === 'adhoc' && !item.manualUnitCost) return 'Informe o custo manual de cada item avulso.'
      if (!(Number(item.quantity) > 0)) return 'A quantidade de cada item deve ser maior que zero.'
    }
    return null
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setMessage('')
    const invalid = validate()
    if (invalid) { setMessage(invalid); return }

    const requestItems: QuoteItemRequest[] = items.map(item => ({
      sourceQuoteItemId: null,
      productId: item.kind === 'product' ? item.productId : null,
      adHocDescription: item.kind === 'adhoc' ? (item.adHocDescription || null) : null,
      manualUnitCost: item.kind === 'adhoc' ? numberOrNull(item.manualUnitCost) : null,
      quantity: Number(item.quantity),
      // CLAUDE.md §2.4: percentages are stored as fractions (0.30 = 30%) — the operator types a
      // percentage, so it is divided by 100 here, once, right before it leaves the browser.
      desiredMarginPercent: Number(item.desiredMarginPercent || 0) / 100,
      manualPriceOverride: numberOrNull(item.manualPriceOverride),
      discountKind: item.discountKind,
      discountValue: item.discountKind === 'Percent' ? Number(item.discountValue || 0) / 100 : Number(item.discountValue || 0),
    }))

    const proposalContent: ProposalContentRequest = {
      title: proposalTitle || null, scope: proposalScope || null,
      technicalHighlights: proposalHighlights.filter(h => h.label.trim() || h.value.trim()),
      technicalNotes: proposalTechnicalNotes || null, outOfScope: proposalOutOfScope || null,
      paymentTerms: proposalPaymentTerms || null, deliveryTerms: proposalDeliveryTerms || null,
      warranty: proposalWarranty || null, notes: proposalNotes || null,
    }

    const request: QuoteCreateRequest = {
      customerId: customerId || null,
      salesChannelId,
      items: requestItems,
      validityDaysOverride: numberOrNull(validityDaysOverride),
      proposalContent,
    }

    setSubmitting(true)
    const result = await apiClient.post<QuoteResponse>('/api/quotes', request)
    setSubmitting(false)
    if (result.ok) navigate(`/quotes/${result.data.id}`)
    else setMessage(quoteErrorMessage(result.error))
  }

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <Link to="/quotes">← Orçamentos</Link>
          <h1>Novo orçamento</h1>
          <p>Preços, custos e taxas são sempre calculados pelo servidor a partir do canal de vendas selecionado.</p>
        </div>
      </div>

      <form className="card form-stack" onSubmit={submit}>
        <label htmlFor="quote-customer-search">Cliente (opcional — deixe em branco para orçamento avulso)</label>
        <input
          id="quote-customer-search"
          value={customerId ? customerLabel : customerSearch}
          onChange={e => { setCustomerId(''); setCustomerSearch(e.target.value) }}
          placeholder="Buscar cliente por nome ou documento"
        />
        {!customerId && customers.length > 0 && customerSearch && (
          <ul className="data-list">
            {customers.map(customer => (
              <li key={customer.id}>
                <button type="button" onClick={() => { setCustomerId(customer.id); setCustomerLabel(customer.name); setCustomerSearch('') }}>
                  {customer.name}{customer.document ? ` · ${customer.document}` : ''}
                </button>
              </li>
            ))}
          </ul>
        )}
        {customerId && <button type="button" onClick={() => { setCustomerId(''); setCustomerLabel('') }}>Remover cliente selecionado</button>}

        <label htmlFor="quote-channel">Canal de vendas</label>
        <select id="quote-channel" required value={salesChannelId} onChange={e => setSalesChannelId(e.target.value)}>
          <option value="">Selecione</option>
          {channels.map(channel => <option key={channel.id} value={channel.id}>{channel.name}</option>)}
        </select>

        <label htmlFor="quote-validity-override">Validade personalizada (dias, opcional)</label>
        <input id="quote-validity-override" type="number" min="1" step="1" value={validityDaysOverride} onChange={e => setValidityDaysOverride(e.target.value)} placeholder="Usar padrão do sistema" />

        <h2>Conteúdo da proposta (opcional)</h2>
        <label htmlFor="quote-proposal-title">Título do projeto</label>
        <input id="quote-proposal-title" value={proposalTitle} onChange={e => setProposalTitle(e.target.value)} placeholder="Ex.: Peças para drone" />

        <label htmlFor="quote-proposal-scope">Escopo</label>
        <textarea id="quote-proposal-scope" value={proposalScope} onChange={e => setProposalScope(e.target.value)} rows={2} />

        <div className="section-heading">
          <label id="quote-proposal-highlights-label">Destaques técnicos</label>
          <button type="button" onClick={() => setProposalHighlights(cur => [...cur, { label: '', value: '' }])}>Adicionar destaque</button>
        </div>
        {proposalHighlights.map((highlight, index) => (
          <div className="inline-form" key={index}>
            <div>
              <label htmlFor={`quote-highlight-label-${index}`}>Rótulo</label>
              <input id={`quote-highlight-label-${index}`} value={highlight.label} placeholder="Ex.: Material"
                onChange={e => setProposalHighlights(cur => cur.map((h, i) => i === index ? { ...h, label: e.target.value } : h))} />
            </div>
            <div>
              <label htmlFor={`quote-highlight-value-${index}`}>Valor</label>
              <input id={`quote-highlight-value-${index}`} value={highlight.value} placeholder="Ex.: PETG"
                onChange={e => setProposalHighlights(cur => cur.map((h, i) => i === index ? { ...h, value: e.target.value } : h))} />
            </div>
            <button type="button" className="button-danger" onClick={() => setProposalHighlights(cur => cur.filter((_, i) => i !== index))}>Remover</button>
          </div>
        ))}

        <label htmlFor="quote-proposal-technical-notes">Notas técnicas</label>
        <textarea id="quote-proposal-technical-notes" value={proposalTechnicalNotes} onChange={e => setProposalTechnicalNotes(e.target.value)} rows={2} />

        <div className="inline-form">
          <div><label htmlFor="quote-proposal-payment-terms">Condições de pagamento</label><input id="quote-proposal-payment-terms" value={proposalPaymentTerms} onChange={e => setProposalPaymentTerms(e.target.value)} placeholder="Usar padrão do sistema" /></div>
          <div><label htmlFor="quote-proposal-delivery-terms">Condições de entrega</label><input id="quote-proposal-delivery-terms" value={proposalDeliveryTerms} onChange={e => setProposalDeliveryTerms(e.target.value)} placeholder="Usar padrão do sistema" /></div>
          <div><label htmlFor="quote-proposal-warranty">Garantia</label><input id="quote-proposal-warranty" value={proposalWarranty} onChange={e => setProposalWarranty(e.target.value)} placeholder="Usar padrão do sistema" /></div>
        </div>

        <label htmlFor="quote-proposal-out-of-scope">Não incluso</label>
        <textarea id="quote-proposal-out-of-scope" value={proposalOutOfScope} onChange={e => setProposalOutOfScope(e.target.value)} rows={2} />

        <label htmlFor="quote-proposal-notes">Observações</label>
        <textarea id="quote-proposal-notes" value={proposalNotes} onChange={e => setProposalNotes(e.target.value)} rows={2} />

        <div className="section-heading">
          <div><h2>Itens</h2><p>Selecione um produto cadastrado ou informe um item avulso com custo manual.</p></div>
          <button type="button" onClick={addItem}>Adicionar item</button>
        </div>

        <label htmlFor="quote-product-search">Buscar produto</label>
        <input id="quote-product-search" value={productSearch} onChange={e => setProductSearch(e.target.value)} placeholder="Código ou nome" />

        <div className="cost-lines">
          {items.map((item, index) => (
            <fieldset className="cost-line" key={item.key}>
              <legend>Item {index + 1}</legend>
              <div className="inline-form">
                <label>
                  <input type="radio" name={`item-kind-${item.key}`} checked={item.kind === 'product'} onChange={() => updateItem(item.key, { kind: 'product' })} /> Produto cadastrado
                </label>
                <label>
                  <input type="radio" name={`item-kind-${item.key}`} checked={item.kind === 'adhoc'} onChange={() => updateItem(item.key, { kind: 'adhoc' })} /> Item avulso
                </label>
              </div>

              {item.kind === 'product' ? (
                <>
                  <label htmlFor={`item-product-${item.key}`}>Produto</label>
                  <select id={`item-product-${item.key}`} value={item.productId} onChange={e => {
                    const selected = products.find(p => p.id === e.target.value)
                    // mission §72: retain a non-blank label for the selected product even after
                    // the search list changes (e.g. further typing narrows it out of `products`)
                    // — never reset the identity to a blank-labelled option.
                    updateItem(item.key, { productId: e.target.value, productLabel: selected ? `${selected.code} · ${selected.name}` : item.productLabel })
                  }} required>
                    <option value="">Selecione</option>
                    {item.productId && !products.some(p => p.id === item.productId) && <option value={item.productId}>{item.productLabel || item.productId}</option>}
                    {products.map(product => <option key={product.id} value={product.id}>{product.code} · {product.name}</option>)}
                  </select>
                </>
              ) : (
                <>
                  <label htmlFor={`item-description-${item.key}`}>Descrição</label>
                  <input id={`item-description-${item.key}`} value={item.adHocDescription} onChange={e => updateItem(item.key, { adHocDescription: e.target.value })} placeholder="Item avulso" />
                  <label htmlFor={`item-manual-cost-${item.key}`}>Custo manual por unidade (R$)</label>
                  <input id={`item-manual-cost-${item.key}`} type="number" min="0" step="any" required value={item.manualUnitCost} onChange={e => updateItem(item.key, { manualUnitCost: e.target.value })} />
                </>
              )}

              <div className="inline-form">
                <div><label htmlFor={`item-quantity-${item.key}`}>Quantidade</label><input id={`item-quantity-${item.key}`} type="number" min="0" step="any" required value={item.quantity} onChange={e => updateItem(item.key, { quantity: e.target.value })} /></div>
                <div><label htmlFor={`item-margin-${item.key}`}>Margem desejada (%)</label><input id={`item-margin-${item.key}`} type="number" step="any" value={item.desiredMarginPercent} onChange={e => updateItem(item.key, { desiredMarginPercent: e.target.value })} /></div>
              </div>
              <div className="inline-form">
                <div><label htmlFor={`item-override-${item.key}`}>Preço manual (opcional)</label><input id={`item-override-${item.key}`} type="number" min="0" step="any" value={item.manualPriceOverride} onChange={e => updateItem(item.key, { manualPriceOverride: e.target.value })} placeholder="Usar preço sugerido" /></div>
                <div><label htmlFor={`item-discount-kind-${item.key}`}>Desconto</label>
                  <select id={`item-discount-kind-${item.key}`} value={item.discountKind} onChange={e => updateItem(item.key, { discountKind: e.target.value as QuoteDiscountKind })}>
                    {(Object.keys(DISCOUNT_LABELS) as QuoteDiscountKind[]).map(kind => <option key={kind} value={kind}>{DISCOUNT_LABELS[kind]}</option>)}
                  </select>
                </div>
                {item.discountKind !== 'None' && (
                  <div><label htmlFor={`item-discount-value-${item.key}`}>Valor do desconto</label><input id={`item-discount-value-${item.key}`} type="number" min="0" step="any" value={item.discountValue} onChange={e => updateItem(item.key, { discountValue: e.target.value })} /></div>
                )}
              </div>

              {items.length > 1 && <button type="button" className="button-danger" onClick={() => removeItem(item.key)}>Remover item {index + 1}</button>}
            </fieldset>
          ))}
        </div>

        <button className="button-primary" type="submit" disabled={submitting}>{submitting ? 'Criando…' : 'Criar orçamento'}</button>
      </form>

      {message && <p role="status" className="form-error">{message}</p>}
    </section>
  )
}
