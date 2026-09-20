import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { apiClient } from '../api/client'
import { useSession } from '../auth/useSession'
import type {
  FeeRuleResponse, FixedFeeApplication, PriceRoundingPolicy, PricingCalculateResponse,
  ProductListItemResponse, ProductListResponse, ProductPriceResponse, SalesChannelCreateRequest, SalesChannelKind, SalesChannelResponse,
} from '../api/types'

const CHANNEL_KINDS: SalesChannelKind[] = ['Direct', 'Marketplace', 'Other']
const KIND_LABELS: Record<SalesChannelKind, string> = { Direct: 'Venda direta', Marketplace: 'Marketplace', Other: 'Outro' }
const ROUNDING_POLICIES: PriceRoundingPolicy[] = ['CENT', 'TEN_CENTS', 'WHOLE', 'NINETY_NINE', 'NONE']
const ROUNDING_LABELS: Record<PriceRoundingPolicy, string> = {
  CENT: 'Centavo (padrão)', TEN_CENTS: 'Dez centavos (arredonda para cima)', WHOLE: 'Inteiro (arredonda para cima)',
  NINETY_NINE: 'Terminado em ,99', NONE: 'Sem arredondamento (trunca)',
}
const FIXED_FEE_APPLICATIONS: FixedFeeApplication[] = ['PerUnit', 'PerOrder']
const FIXED_FEE_LABELS: Record<FixedFeeApplication, string> = { PerUnit: 'Por unidade', PerOrder: 'Por pedido' }

function percent(value: number | string): string {
  return `${(Number(value) * 100).toLocaleString('pt-BR', { maximumFractionDigits: 4 })}%`
}
function money(value: number | string): string {
  return Number(value).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 2 })
}
function numberOrNull(value: string): number | null { return value.trim() === '' ? null : Number(value) }

type ChannelForm = { code: string; name: string; kind: SalesChannelKind; defaultMarginPercent: string; notes: string }
const blankChannelForm: ChannelForm = { code: '', name: '', kind: 'Marketplace', defaultMarginPercent: '', notes: '' }

type VersionForm = { validFrom: string; validUntil: string; commissionPercent: string; fixedFee: string; fixedFeeApplication: FixedFeeApplication; minimumFee: string; maximumFee: string; notes: string; closeCurrentOpenVersion: boolean }
const blankVersionForm: VersionForm = { validFrom: '', validUntil: '', commissionPercent: '', fixedFee: '0', fixedFeeApplication: 'PerUnit', minimumFee: '', maximumFee: '', notes: '', closeCurrentOpenVersion: false }

export function PricingPage() {
  const { session } = useSession()
  const roles = session.status === 'authenticated' ? session.user.roles : []
  const canManage = roles.includes('Owner') // PricingManage is Owner-only (mission's authorization matrix)

  const [channels, setChannels] = useState<SalesChannelResponse[]>([])
  const [selected, setSelected] = useState<SalesChannelResponse | null>(null)
  const [feeRule, setFeeRule] = useState<FeeRuleResponse | null>(null)
  const [form, setForm] = useState<ChannelForm>(blankChannelForm)
  const [versionForm, setVersionForm] = useState<VersionForm>(blankVersionForm)
  const [message, setMessage] = useState('')

  const [products, setProducts] = useState<ProductListItemResponse[]>([])
  const [pricingProductId, setPricingProductId] = useState('')
  const [pricingChannelId, setPricingChannelId] = useState('')
  const [pricingMarginOverride, setPricingMarginOverride] = useState('')
  const [pricingResult, setPricingResult] = useState<ProductPriceResponse | null>(null)
  const [pricingMessage, setPricingMessage] = useState('')

  const [adHocCost, setAdHocCost] = useState('')
  const [adHocCommission, setAdHocCommission] = useState('0')
  const [adHocFixedFee, setAdHocFixedFee] = useState('0')
  const [adHocMargin, setAdHocMargin] = useState('')
  const [adHocRounding, setAdHocRounding] = useState<PriceRoundingPolicy | ''>('')
  const [adHocResult, setAdHocResult] = useState<PricingCalculateResponse | null>(null)
  const [adHocMessage, setAdHocMessage] = useState('')

  async function loadChannels() {
    const result = await apiClient.get<SalesChannelResponse[]>('/api/pricing/channels?includeInactive=true')
    if (result.ok) setChannels(result.data)
    else setMessage(result.error.safeMessage)
  }

  async function loadProducts() {
    const result = await apiClient.get<ProductListResponse>('/api/products?status=active&page=1&pageSize=100')
    if (result.ok) setProducts(result.data.items)
  }

  // oxlint-disable-next-line react/set-state-in-effect, react-hooks/exhaustive-deps
  useEffect(() => { void loadChannels(); void loadProducts() }, [])

  async function open(channel: SalesChannelResponse) {
    setSelected(channel)
    setForm({ code: channel.code, name: channel.name, kind: channel.kind, defaultMarginPercent: channel.defaultMarginPercent != null ? (Number(channel.defaultMarginPercent) * 100).toString() : '', notes: channel.notes ?? '' })
    setMessage('')
    const result = await apiClient.get<FeeRuleResponse>(`/api/pricing/channels/${channel.id}/fee-rule`)
    setFeeRule(result.ok ? result.data : null)
  }

  function newChannel() {
    setSelected(null)
    setForm(blankChannelForm)
    setFeeRule(null)
    setMessage('')
  }

  async function submitChannel(event: FormEvent) {
    event.preventDefault()
    const defaultMarginPercent = form.defaultMarginPercent.trim() === '' ? null : Number(form.defaultMarginPercent) / 100
    if (selected) {
      const result = await apiClient.put<SalesChannelResponse>(`/api/pricing/channels/${selected.id}`, { name: form.name, kind: form.kind, defaultMarginPercent, notes: form.notes || null, version: selected.version })
      if (result.ok) { setMessage('Canal atualizado com sucesso.'); await open(result.data); await loadChannels() }
      else setMessage(result.error.safeMessage)
    } else {
      const request: SalesChannelCreateRequest = { code: form.code, name: form.name, kind: form.kind, defaultMarginPercent, notes: form.notes || null }
      const result = await apiClient.post<SalesChannelResponse>('/api/pricing/channels', request)
      if (result.ok) { setMessage('Canal criado com sucesso.'); await open(result.data); await loadChannels() }
      else setMessage(result.error.safeMessage)
    }
  }

  async function toggleActive() {
    if (!selected) return
    const action = selected.active ? 'deactivate' : 'activate'
    const result = await apiClient.post<void>(`/api/pricing/channels/${selected.id}/${action}?version=${selected.version}`)
    if (result.ok) { setMessage(selected.active ? 'Canal desativado.' : 'Canal reativado.'); const updated = await apiClient.get<SalesChannelResponse>(`/api/pricing/channels/${selected.id}`); if (updated.ok) await open(updated.data); await loadChannels() }
    else setMessage(result.error.safeMessage)
  }

  async function createFeeRule() {
    if (!selected) return
    const result = await apiClient.post<FeeRuleResponse>(`/api/pricing/channels/${selected.id}/fee-rule`, { name: `Regra de taxas — ${selected.name}` })
    if (result.ok) { setFeeRule(result.data); setMessage('Regra de taxas criada. Adicione uma versão vigente.') }
    else setMessage(result.error.safeMessage)
  }

  async function submitVersion(event: FormEvent) {
    event.preventDefault()
    if (!selected) return
    const result = await apiClient.post<FeeRuleResponse>(`/api/pricing/channels/${selected.id}/fee-rule/versions`, {
      validFrom: versionForm.validFrom,
      validUntil: versionForm.validUntil || null,
      commissionPercent: Number(versionForm.commissionPercent || 0) / 100,
      fixedFee: Number(versionForm.fixedFee || 0),
      fixedFeeApplication: versionForm.fixedFeeApplication,
      minimumFee: numberOrNull(versionForm.minimumFee),
      maximumFee: numberOrNull(versionForm.maximumFee),
      notes: versionForm.notes || null,
      closeCurrentOpenVersion: versionForm.closeCurrentOpenVersion,
    })
    if (result.ok) { setFeeRule(result.data); setVersionForm(blankVersionForm); setMessage('Versão de taxas criada com sucesso.') }
    else setMessage(result.error.safeMessage)
  }

  async function calculateAdHoc(event: FormEvent) {
    event.preventDefault()
    setAdHocMessage(''); setAdHocResult(null)
    const result = await apiClient.post<PricingCalculateResponse>('/api/pricing/calculate', {
      unitTotalCost: Number(adHocCost || 0),
      commissionPercent: Number(adHocCommission || 0) / 100,
      fixedFee: Number(adHocFixedFee || 0),
      desiredMargin: adHocMargin.trim() === '' ? null : Number(adHocMargin) / 100,
      roundingPolicy: adHocRounding || null,
      minimumFee: null,
      maximumFee: null,
    })
    if (result.ok) setAdHocResult(result.data)
    else setAdHocMessage(result.error.safeMessage)
  }

  async function calculateProductPrice(event: FormEvent) {
    event.preventDefault()
    setPricingMessage(''); setPricingResult(null)
    if (!pricingProductId || !pricingChannelId) { setPricingMessage('Selecione um produto e um canal.'); return }
    const result = await apiClient.post<ProductPriceResponse>(`/api/pricing/products/${pricingProductId}/price`, {
      salesChannelId: pricingChannelId,
      desiredMarginOverride: pricingMarginOverride.trim() === '' ? null : Number(pricingMarginOverride) / 100,
    })
    if (result.ok) setPricingResult(result.data)
    else setPricingMessage(result.error.safeMessage)
  }

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <Link to="/">← Início</Link>
          <h1>Precificação e canais de venda</h1>
          <p>Cadastre canais (venda direta e marketplaces), configure taxas versionadas no tempo e calcule preços sugeridos.</p>
        </div>
        {canManage && <button type="button" onClick={newChannel}>Novo canal</button>}
      </div>

      <div className="split-grid">
        <div className="card">
          <h2>Canais</h2>
          <ul className="data-list">
            {channels.map(channel => (
              <li key={channel.id}>
                <button type="button" onClick={() => void open(channel)}><strong>{channel.code}</strong> — {channel.name}</button>
                <span className="badge">{KIND_LABELS[channel.kind]}</span>
                <span className={channel.active ? 'badge' : 'badge badge--muted'}>{channel.active ? 'Ativo' : 'Inativo'}</span>
              </li>
            ))}
            {channels.length === 0 && <li>Nenhum canal cadastrado.</li>}
          </ul>
        </div>

        <form className="card form-stack" onSubmit={submitChannel}>
          <h2>{selected ? 'Editar canal' : 'Novo canal'}</h2>
          <label htmlFor="channel-code">Código</label>
          <input id="channel-code" required disabled={!!selected} value={form.code} onChange={e => setForm({ ...form, code: e.target.value })} placeholder="SHOPEE" />
          <label htmlFor="channel-name">Nome</label>
          <input id="channel-name" required minLength={2} value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} />
          <label htmlFor="channel-kind">Tipo</label>
          <select id="channel-kind" value={form.kind} onChange={e => setForm({ ...form, kind: e.target.value as SalesChannelKind })}>
            {CHANNEL_KINDS.map(kind => <option key={kind} value={kind}>{KIND_LABELS[kind]}</option>)}
          </select>
          <label htmlFor="channel-margin">Margem desejada padrão (%)</label>
          <input id="channel-margin" type="number" min="0" max="99.999" step="any" value={form.defaultMarginPercent} onChange={e => setForm({ ...form, defaultMarginPercent: e.target.value })} placeholder="Usar configuração do sistema" />
          <label htmlFor="channel-notes">Notas</label>
          <input id="channel-notes" value={form.notes} onChange={e => setForm({ ...form, notes: e.target.value })} />
          {canManage && <button className="button-primary" type="submit">{selected ? 'Salvar alterações' : 'Criar canal'}</button>}
          {selected && canManage && <button type="button" onClick={() => void toggleActive()}>{selected.active ? 'Desativar canal' : 'Reativar canal'}</button>}
        </form>
      </div>

      {selected && (
        <div className="card form-stack">
          <h2>Taxas de {selected.name}</h2>
          {!feeRule && canManage && <button type="button" onClick={() => void createFeeRule()}>Criar regra de taxas</button>}
          {!feeRule && !canManage && <p className="empty-state">Este canal ainda não tem regra de taxas.</p>}
          {feeRule && (
            <ul className="data-list">
              {feeRule.versions.map(version => (
                <li key={version.id}>
                  <span>{version.validFrom} até {version.validUntil ?? 'em aberto'}</span>
                  <span>Comissão: {percent(version.commissionPercent)}</span>
                  <span>Taxa fixa: {money(version.fixedFee)} ({FIXED_FEE_LABELS[version.fixedFeeApplication]})</span>
                  {version.minimumFee != null && <span>Mín.: {money(version.minimumFee)}</span>}
                  {version.maximumFee != null && <span>Máx.: {money(version.maximumFee)}</span>}
                </li>
              ))}
              {feeRule.versions.length === 0 && <li>Nenhuma versão de taxa cadastrada.</li>}
            </ul>
          )}
          {feeRule && canManage && (
            <form className="form-stack" onSubmit={submitVersion}>
              <h3>Nova versão de taxa</h3>
              <div className="inline-form">
                <div><label htmlFor="version-valid-from">Vigente a partir de</label><input id="version-valid-from" type="date" required value={versionForm.validFrom} onChange={e => setVersionForm({ ...versionForm, validFrom: e.target.value })} /></div>
                <div><label htmlFor="version-valid-until">Vigente até (opcional)</label><input id="version-valid-until" type="date" value={versionForm.validUntil} onChange={e => setVersionForm({ ...versionForm, validUntil: e.target.value })} /></div>
              </div>
              <div className="inline-form">
                <div><label htmlFor="version-commission">Comissão (%)</label><input id="version-commission" type="number" min="0" max="99.999" step="any" required value={versionForm.commissionPercent} onChange={e => setVersionForm({ ...versionForm, commissionPercent: e.target.value })} /></div>
                <div><label htmlFor="version-fixed-fee">Taxa fixa (R$)</label><input id="version-fixed-fee" type="number" min="0" step="any" required value={versionForm.fixedFee} onChange={e => setVersionForm({ ...versionForm, fixedFee: e.target.value })} /></div>
              </div>
              <label htmlFor="version-fixed-fee-application">Aplicação da taxa fixa</label>
              <select id="version-fixed-fee-application" value={versionForm.fixedFeeApplication} onChange={e => setVersionForm({ ...versionForm, fixedFeeApplication: e.target.value as FixedFeeApplication })}>
                {FIXED_FEE_APPLICATIONS.map(application => <option key={application} value={application}>{FIXED_FEE_LABELS[application]}</option>)}
              </select>
              <div className="inline-form">
                <div><label htmlFor="version-min-fee">Taxa mínima (R$)</label><input id="version-min-fee" type="number" min="0" step="any" value={versionForm.minimumFee} onChange={e => setVersionForm({ ...versionForm, minimumFee: e.target.value })} placeholder="Sem mínimo" /></div>
                <div><label htmlFor="version-max-fee">Taxa máxima (R$)</label><input id="version-max-fee" type="number" min="0" step="any" value={versionForm.maximumFee} onChange={e => setVersionForm({ ...versionForm, maximumFee: e.target.value })} placeholder="Sem máximo" /></div>
              </div>
              <label><input type="checkbox" checked={versionForm.closeCurrentOpenVersion} onChange={e => setVersionForm({ ...versionForm, closeCurrentOpenVersion: e.target.checked })} /> Encerrar a versão vigente em aberto nesta data</label>
              <button className="button-primary" type="submit">Adicionar versão</button>
            </form>
          )}
        </div>
      )}

      {message && <p role="status" className="form-error">{message}</p>}

      <div className="split-grid">
        <form className="card form-stack" onSubmit={calculateProductPrice} aria-labelledby="product-pricing-heading">
          <h2 id="product-pricing-heading">Precificar um produto</h2>
          <label htmlFor="pricing-product">Produto</label>
          <select id="pricing-product" required value={pricingProductId} onChange={e => setPricingProductId(e.target.value)}>
            <option value="">Selecione</option>
            {products.map(product => <option key={product.id} value={product.id}>{product.code} · {product.name}</option>)}
          </select>
          <label htmlFor="pricing-channel">Canal</label>
          <select id="pricing-channel" required value={pricingChannelId} onChange={e => setPricingChannelId(e.target.value)}>
            <option value="">Selecione</option>
            {channels.filter(c => c.active).map(channel => <option key={channel.id} value={channel.id}>{channel.code} · {channel.name}</option>)}
          </select>
          <label htmlFor="pricing-margin-override">Margem desejada (%) — substitui o padrão do canal</label>
          <input id="pricing-margin-override" type="number" min="0" max="99.999" step="any" value={pricingMarginOverride} onChange={e => setPricingMarginOverride(e.target.value)} placeholder="Usar padrão do canal" />
          <button className="button-primary" type="submit">Calcular preço sugerido</button>
          {pricingMessage && <p role="status" className="form-error">{pricingMessage}</p>}
          {pricingResult && <ProductPriceResultCard result={pricingResult} />}
        </form>

        <form className="card form-stack" onSubmit={calculateAdHoc} aria-labelledby="ad-hoc-pricing-heading">
          <h2 id="ad-hoc-pricing-heading">Simulação avulsa</h2>
          <label htmlFor="ad-hoc-cost">Custo unitário total (R$)</label>
          <input id="ad-hoc-cost" type="number" min="0" step="any" required value={adHocCost} onChange={e => setAdHocCost(e.target.value)} />
          <div className="inline-form">
            <div><label htmlFor="ad-hoc-commission">Comissão (%)</label><input id="ad-hoc-commission" type="number" min="0" max="99.999" step="any" value={adHocCommission} onChange={e => setAdHocCommission(e.target.value)} /></div>
            <div><label htmlFor="ad-hoc-fixed-fee">Taxa fixa (R$)</label><input id="ad-hoc-fixed-fee" type="number" min="0" step="any" value={adHocFixedFee} onChange={e => setAdHocFixedFee(e.target.value)} /></div>
          </div>
          <label htmlFor="ad-hoc-margin">Margem desejada (%)</label>
          <input id="ad-hoc-margin" type="number" min="0" max="99.999" step="any" value={adHocMargin} onChange={e => setAdHocMargin(e.target.value)} placeholder="Usar configuração do sistema" />
          <label htmlFor="ad-hoc-rounding">Política de arredondamento</label>
          <select id="ad-hoc-rounding" value={adHocRounding} onChange={e => setAdHocRounding(e.target.value as PriceRoundingPolicy | '')}>
            <option value="">Usar configuração do sistema</option>
            {ROUNDING_POLICIES.map(policy => <option key={policy} value={policy}>{ROUNDING_LABELS[policy]}</option>)}
          </select>
          <button className="button-primary" type="submit">Calcular</button>
          {adHocMessage && <p role="status" className="form-error">{adHocMessage}</p>}
          {adHocResult && <PricingResultCard result={adHocResult} />}
        </form>
      </div>
    </section>
  )
}

function PricingResultCard({ result }: { result: PricingCalculateResponse }) {
  return <section className="card cost-result" aria-label="Resultado da precificação">
    <div className="section-heading"><h3>Preço sugerido</h3><strong className="unit-cost">{money(result.suggestedPrice)}</strong></div>
    <dl className="totals-grid">
      <div><dt>Custo unitário</dt><dd>{money(result.unitTotalCost)}</dd></div>
      <div><dt>Comissão</dt><dd>{percent(result.commissionPercent)} ({money(result.commissionAmount)}){result.feeClampApplied && ` — limite ${result.feeClampApplied === 'MIN' ? 'mínimo' : 'máximo'} aplicado`}</dd></div>
      <div><dt>Taxa fixa</dt><dd>{money(result.fixedFee)}</dd></div>
      <div><dt>Margem desejada</dt><dd>{percent(result.desiredMargin)}</dd></div>
      <div><dt>Denominador</dt><dd>{percent(result.denominator)}</dd></div>
      <div className="totals-grid__total"><dt>Preço sugerido</dt><dd>{money(result.suggestedPrice)}</dd></div>
    </dl>
    {result.warnings.length > 0 && <p className="stock-warning">{result.warnings.join(' ')}</p>}
  </section>
}

/** Renders ONLY authoritative fields the server returned (mission §44) — never recomputes a
 * value client-side. Every identity/driver here (channel, fee rule/version, rounding policy,
 * organization date) is the exact snapshot-safe contract Terra's B-03 correction introduced. */
function ProductPriceResultCard({ result }: { result: ProductPriceResponse }) {
  return <section className="card cost-result" aria-label="Resultado da precificação">
    <div className="section-heading"><h3>Preço sugerido</h3><strong className="unit-cost">{money(result.suggestedPrice)}</strong></div>
    <dl className="totals-grid">
      <div><dt>Canal</dt><dd>{result.salesChannelCode} · {result.salesChannelName} ({KIND_LABELS[result.salesChannelKind]})</dd></div>
      <div><dt>Custo unitário</dt><dd>{money(result.unitTotalCost)}</dd></div>
      <div><dt>Comissão</dt><dd>{percent(result.commissionPercent)} ({money(result.commissionAmount)}){result.feeClampApplied && ` — limite ${result.feeClampApplied === 'MIN' ? 'mínimo' : 'máximo'} aplicado`}</dd></div>
      <div><dt>Taxa fixa</dt><dd>{money(result.fixedFee)}</dd></div>
      <div><dt>Margem desejada</dt><dd>{percent(result.desiredMargin)}</dd></div>
      <div><dt>Denominador</dt><dd>{percent(result.denominator)}</dd></div>
      <div><dt>Política de arredondamento</dt><dd>{ROUNDING_LABELS[result.roundingPolicy ?? 'CENT']}</dd></div>
      <div><dt>Data de referência</dt><dd>{result.organizationDate}</dd></div>
      <div className="totals-grid__total"><dt>Preço sugerido</dt><dd>{money(result.suggestedPrice)}</dd></div>
    </dl>
    {result.warnings.length > 0 && <p className="stock-warning">{result.warnings.join(' ')}</p>}
  </section>
}
