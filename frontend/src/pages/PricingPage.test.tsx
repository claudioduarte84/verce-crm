import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PricingPage } from './PricingPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const sessionState = vi.hoisted(() => ({ roles: ['Owner'] as string[] }))
vi.mock('../auth/useSession', () => ({
  useSession: () => ({ session: { status: 'authenticated', user: { id: 'u1', email: 'owner@example.test', displayName: 'Owner', roles: sessionState.roles } } }),
}))

const directChannel = { id: 'ch-direct', code: 'DIRECT', name: 'Venda Direta', kind: 'Direct', defaultMarginPercent: null, notes: null, active: true, version: 1 }
const marketplaceChannel = { id: 'ch-mkt', code: 'SHOPEE', name: 'Shopee', kind: 'Marketplace', defaultMarginPercent: 0.30, notes: null, active: true, version: 1 }
const channels = [directChannel, marketplaceChannel]
const products = [{ id: 'p1', code: 'MINI-VASE', name: 'Mini Vaso', active: true, version: 1 }]

const feeRule = {
  id: 'fr1', salesChannelId: 'ch-mkt', name: 'Regra padrão', active: true, version: 1,
  versions: [{ id: 'v1', validFrom: '2020-01-01', validUntil: null, commissionPercent: 0.1, fixedFee: 5, fixedFeeApplication: 'PerUnit', minimumFee: null, maximumFee: null, notes: null }],
}

// Shaped like the real ProductPriceResponse (Terra B-03) — every field the server actually
// returns for a Product Pricing calculation, not the unrelated ad-hoc calculator's response.
const pricingResult = {
  productId: 'p1', productCode: 'MINI-VASE', productName: 'Mini Vaso',
  unitTotalCost: 10,
  salesChannelId: 'ch-mkt', salesChannelCode: 'SHOPEE', salesChannelKind: 'Marketplace', salesChannelName: 'Shopee',
  desiredMargin: 0.3,
  feeRuleId: 'fr1', feeRuleVersionId: 'v1', commissionPercent: 0.1, fixedFee: 5,
  organizationDate: '2026-09-19',
  denominator: 0.6, rawPrice: 25, roundingPolicy: 'CENT', suggestedPrice: 25,
  commissionAmount: 2.5, feeClampApplied: null, warnings: [],
}

function renderPage() { return render(<MemoryRouter><PricingPage /></MemoryRouter>) }

describe('PricingPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset(); api.put.mockReset()
    sessionState.roles = ['Owner']
    api.get.mockImplementation((path: string) => {
      if (path.startsWith('/api/pricing/channels?includeInactive=true')) return Promise.resolve({ ok: true, data: channels })
      if (path.startsWith('/api/products?')) return Promise.resolve({ ok: true, data: { items: products, page: 1, pageSize: 100, total: 1 } })
      if (path === '/api/pricing/channels/ch-mkt/fee-rule') return Promise.resolve({ ok: true, data: feeRule })
      if (path.endsWith('/fee-rule')) return Promise.resolve({ ok: false, error: { status: 404, kind: 'problem', safeMessage: 'Recurso não encontrado.' } })
      return Promise.resolve({ ok: true, data: [] })
    })
  })

  it('loads channels and active products on mount', async () => {
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalledWith('/api/pricing/channels?includeInactive=true'))
    await waitFor(() => expect(api.get).toHaveBeenCalledWith(expect.stringContaining('/api/products?')))
    expect(await screen.findByRole('button', { name: /Shopee/ })).toBeInTheDocument()
  })

  it('creates a marketplace channel with the margin converted from a human percentage to a fraction', async () => {
    const user = userEvent.setup()
    renderPage()
    await screen.findByRole('button', { name: /Shopee/ })
    api.post.mockResolvedValueOnce({ ok: true, data: { ...marketplaceChannel, id: 'ch-new', code: 'MELI', name: 'Mercado Livre' } })
    await user.type(screen.getByLabelText('Código'), 'MELI')
    await user.type(screen.getByLabelText('Nome'), 'Mercado Livre')
    await user.type(screen.getByLabelText('Margem desejada padrão (%)'), '25')
    await user.click(screen.getByRole('button', { name: 'Criar canal' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/pricing/channels',
      expect.objectContaining({ code: 'MELI', name: 'Mercado Livre', defaultMarginPercent: 0.25 })))
  })

  it('opens a channel, shows its fee-rule versions and creates a new version with a fraction commission', async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole('button', { name: /Shopee/ }))
    expect(await screen.findByText(/Comissão: 10%/)).toBeInTheDocument()

    api.post.mockResolvedValueOnce({ ok: true, data: { ...feeRule, versions: [...feeRule.versions, { id: 'v2', validFrom: '2027-01-01', validUntil: null, commissionPercent: 0.12, fixedFee: 6, fixedFeeApplication: 'PerUnit', minimumFee: null, maximumFee: null, notes: null }] } })
    await user.type(screen.getByLabelText('Vigente a partir de'), '2027-01-01')
    await user.type(screen.getByLabelText('Comissão (%)', { selector: '#version-commission' }), '12')
    await user.clear(screen.getByLabelText('Taxa fixa (R$)', { selector: '#version-fixed-fee' }))
    await user.type(screen.getByLabelText('Taxa fixa (R$)', { selector: '#version-fixed-fee' }), '6')
    await user.click(screen.getByRole('button', { name: 'Adicionar versão' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/pricing/channels/ch-mkt/fee-rule/versions',
      expect.objectContaining({ validFrom: '2027-01-01', commissionPercent: 0.12, fixedFee: 6 })))
  })

  it('offers to create a fee rule for a channel that has none yet', async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole('button', { name: /DIRECT/ }))
    expect(await screen.findByRole('button', { name: 'Criar regra de taxas' })).toBeInTheDocument()
  })

  it('calculates a product price against a channel and shows the suggested price', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: pricingResult })
    renderPage()
    await screen.findByRole('button', { name: /Shopee/ })

    await user.selectOptions(screen.getByLabelText('Produto'), 'p1')
    await user.selectOptions(screen.getByLabelText('Canal'), 'ch-mkt')
    await user.click(screen.getByRole('button', { name: 'Calcular preço sugerido' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/pricing/products/p1/price', { salesChannelId: 'ch-mkt', desiredMarginOverride: null }))
    const form = screen.getByRole('heading', { name: 'Precificar um produto' }).closest('form')!
    await waitFor(() => expect(within(form).getAllByText('R$ 25,00').length).toBeGreaterThan(0))
    // Terra B-03: the panel renders the AUTHORITATIVE server-resolved snapshot fields — channel
    // identity, rounding policy and the resolution date — never a client-side recomputation.
    expect(within(form).getByText(/SHOPEE · Shopee \(Marketplace\)/)).toBeInTheDocument()
    expect(within(form).getByText('Centavo (padrão)')).toBeInTheDocument()
    expect(within(form).getByText('2026-09-19')).toBeInTheDocument()
  })

  it('renders a Direct-channel pricing result with zero commission and zero fixed fee', async () => {
    const user = userEvent.setup()
    const directResult = {
      ...pricingResult, salesChannelId: 'ch-direct', salesChannelCode: 'DIRECT', salesChannelKind: 'Direct', salesChannelName: 'Venda Direta',
      commissionPercent: 0, fixedFee: 0, commissionAmount: 0, unitTotalCost: 10, desiredMargin: 0.35, denominator: 0.65, suggestedPrice: 15.38,
    }
    api.post.mockResolvedValue({ ok: true, data: directResult })
    renderPage()
    await screen.findByRole('button', { name: /Shopee/ })

    await user.selectOptions(screen.getByLabelText('Produto'), 'p1')
    await user.selectOptions(screen.getByLabelText('Canal'), 'ch-direct')
    await user.click(screen.getByRole('button', { name: 'Calcular preço sugerido' }))

    const form = screen.getByRole('heading', { name: 'Precificar um produto' }).closest('form')!
    await waitFor(() => expect(within(form).getAllByText('R$ 15,38').length).toBeGreaterThan(0))
    expect(within(form).getByText(/DIRECT · Venda Direta \(Venda direta\)/)).toBeInTheDocument()
    expect(within(form).getByText(/^0% \(R\$ 0,00\)$/)).toBeInTheDocument()
    expect(within(form).getByText('R$ 0,00')).toBeInTheDocument()
  })

  it('requires both a product and a channel before calculating a product price', async () => {
    const user = userEvent.setup()
    renderPage()
    await screen.findByRole('button', { name: /Shopee/ })
    const form = screen.getByRole('heading', { name: 'Precificar um produto' }).closest('form')!
    const submit = within(form).getByRole('button', { name: 'Calcular preço sugerido' })
    // Browser-native required validation blocks submission with empty selects; simulate a
    // programmatic submit bypassing that to exercise the component's own guard.
    form.noValidate = true
    await user.click(submit)
    expect(await screen.findByText('Selecione um produto e um canal.')).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('runs the ad-hoc direct-pricing simulation with the golden formula inputs', async () => {
    const user = userEvent.setup()
    const directResult = { ...pricingResult, commissionPercent: 0, commissionAmount: 0, fixedFee: 0, unitTotalCost: 80, desiredMargin: 0.2, denominator: 0.8, suggestedPrice: 100 }
    api.post.mockResolvedValue({ ok: true, data: directResult })
    renderPage()
    await screen.findByRole('button', { name: /Shopee/ })

    await user.type(screen.getByLabelText('Custo unitário total (R$)'), '80')
    await user.clear(screen.getByLabelText('Comissão (%)'))
    await user.type(screen.getByLabelText('Margem desejada (%)'), '20')
    await user.click(screen.getByRole('button', { name: 'Calcular' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/pricing/calculate', expect.objectContaining({
      unitTotalCost: 80, commissionPercent: 0, fixedFee: 0, desiredMargin: 0.2,
    })))
    const form = screen.getByRole('heading', { name: 'Simulação avulsa' }).closest('form')!
    await waitFor(() => expect(within(form).getAllByText('R$ 100,00').length).toBeGreaterThan(0))
  })

  it('hides channel management controls for a non-Owner', async () => {
    sessionState.roles = ['Operator']
    renderPage()
    await waitFor(() => expect(api.get).toHaveBeenCalled())
    expect(screen.queryByRole('button', { name: 'Novo canal' })).not.toBeInTheDocument()
  })
})
