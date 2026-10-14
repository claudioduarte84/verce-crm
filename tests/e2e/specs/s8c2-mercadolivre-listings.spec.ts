import { expect, test, type APIRequestContext, type Locator, type Page } from '@playwright/test'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { assertDisposableE2eDatabase, resolveE2eConnectionString, runE2ePsql } from '../e2e-env.cjs'

/**
 * S8C.2 D11 — the integrated Mercado Livre listings journey, in a real browser:
 *
 *   Browser -> frontend (vite preview) -> real Verce.Api composition (tests/Verce.Marketplaces.E2EHost) -> real MercadoLivreConnector, credential store, fenced
 *   Quartz-dispatched runs -> disposable PostgreSQL -> in-process FakeMercadoLivreHttpHandler (the ONLY substitute: the two named provider HTTP clients).
 *
 * Nothing here talks to Mercado Livre, and nothing is simulated in the business layer: runs, health, counters, observations, retries, SKU mappings and
 * linkage are all produced by the real services and read back from the browser, the API and the disposable database. This is NOT the live validation
 * (L-01..L-14), which stays a manual human gate.
 *
 * The control plane (POST /__e2e__/mercadolivre/state, GET .../calls, GET .../observations/{externalId}) exists only in the E2E host.
 */

const HOST = 'https://localhost:7246'
const APP = 'https://localhost:4173'
const CALLBACK = `${APP}/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback`
const CONTEXT_FILE = join(__dirname, '..', '.playwright', 's8c2-context.json')
const RUN_TIMEOUT = 90_000

// Unique, numeric, collision-free identities for this run (listing ids must match ^MLB[0-9]{1,20}$).
const TAG = String(Date.now()).slice(-9)
const SELLER_ID = Number(`9${TAG}`)
const ids = { a: `MLB${TAG}01`, b: `MLB${TAG}02`, f1: `MLB${TAG}03`, f2: `MLB${TAG}04`, c: `MLB${TAG}05`, d: `MLB${TAG}06` }
const SKU = { a: `E2E-A-${TAG}`, b: `E2E-B-${TAG}`, f1: `E2E-F1-${TAG}`, f2: `E2E-F2-${TAG}`, confirmed: `E2E-CONFIRMED-${TAG}` }
const DISPLAY_NAME = `ML E2E ${TAG}`
const PRODUCT_CODE = `E2EML${TAG}`

type Item = { id: string; sku?: string; title: string; price: number; status?: string; variations?: number; fail?: boolean }
type Calls = { tokenExchanges: number; tokenRefreshes: number; usersMe: number; grantProbes: number; scans: number; bulkRequests: number; bulkIds: string[][]; nonGetOutsideOAuth: number; stockOrWriteLike: number; unexpectedRoutes: number; rejected: number }
type Run = { runId: string; kind: string; status: string; counters: { total: number; found: number; blockingRetryable: number; blockingNonRetryable: number; pending: number } }
type SyncStatus = { availability: { canStart: boolean }; summary: { activeRun: Run | null; latestRun: Run | null; health: string; openBlockingItemCount: number } }

// ---- disposable-database evidence (refuses anything but the disposable E2E database) ----
function sql(query: string): string {
  const database = assertDisposableE2eDatabase(resolveE2eConnectionString())
  return runE2ePsql(database, ['-tA', '-c', query])
}

// ---- fake provider control plane ----
const catalog: Item[] = []
async function setProvider(request: APIRequestContext, items: Item[], resetCalls = false) {
  const response = await request.post(`${HOST}/__e2e__/mercadolivre/state`, { data: { sellerId: SELLER_ID, resetCalls, items } })
  expect(response.status(), 'fake provider state accepted').toBe(204)
}
async function providerCalls(request: APIRequestContext): Promise<Calls> {
  const response = await request.get(`${HOST}/__e2e__/mercadolivre/calls`)
  expect(response.status()).toBe(200)
  return await response.json() as Calls
}
async function observations(request: APIRequestContext, externalId: string) {
  const response = await request.get(`${HOST}/__e2e__/mercadolivre/observations/${externalId}`)
  expect(response.status()).toBe(200)
  return await response.json() as { listings: number; listingIds: string[]; observations: { title: string; price: number | null; status: string; provenance: string; externalSku: string | null }[] }
}

// ---- UI helpers ----
function formFor(page: Page, heading: string | RegExp): Locator { return page.locator('form').filter({ has: page.getByRole('heading', { name: heading }) }) }
async function selectByText(select: Locator, text: string | RegExp) {
  const value = await select.locator('option').filter({ hasText: text }).first().getAttribute('value')
  expect(value, `expected option matching ${text}`).not.toBeNull()
  await select.selectOption(value!)
}
const syncPanel = (page: Page) => page.getByRole('region', { name: 'Sincronização de anúncios' })
const skuPanel = (page: Page) => page.getByRole('region', { name: 'SKUs confirmados' })

async function accountId(page: Page): Promise<string> {
  const response = await page.request.get('/api/commerce/marketplace-accounts')
  expect(response.status()).toBe(200)
  const accounts = await response.json() as { id: string; displayName: string }[]
  const account = accounts.find(x => x.displayName === DISPLAY_NAME)
  expect(account, 'the connected Mercado Livre account exists').toBeTruthy()
  return account!.id
}
async function syncStatus(page: Page, account: string): Promise<SyncStatus> {
  const response = await page.request.get(`/api/commerce/marketplace-accounts/${account}/listing-sync`)
  expect(response.status()).toBe(200)
  return await response.json() as SyncStatus
}
/** Polls VERCE's own status (never the provider) until a run other than `previousRunId` is terminal. */
async function waitForNewTerminalRun(page: Page, account: string, previousRunId: string | null): Promise<Run> {
  let latest: Run | null = null
  await expect.poll(async () => {
    const s = (await syncStatus(page, account)).summary
    latest = s.activeRun === null && s.latestRun && s.latestRun.runId !== previousRunId ? s.latestRun : null
    return latest?.status ?? 'WAITING'
  }, { message: 'a new run reaches a terminal status', timeout: RUN_TIMEOUT, intervals: [500, 1000, 2000] }).toMatch(/SUCCEEDED|PARTIAL|FAILED/)
  return latest!
}
async function openAccountPanel(page: Page) {
  await page.goto('/marketplace-accounts')
  await page.getByRole('button', { name: DISPLAY_NAME, exact: true }).click()
  await expect(syncPanel(page)).toBeVisible()
  await expect(syncPanel(page).getByText('Carregando sincronização…')).toHaveCount(0)
}
async function startSync(page: Page, account: string): Promise<Run> {
  const previous = (await syncStatus(page, account)).summary.latestRun?.runId ?? null
  await syncPanel(page).getByRole('button', { name: 'Sincronizar anúncios' }).click()
  return await waitForNewTerminalRun(page, account, previous)
}
async function openPublishedItems(page: Page, account: string, search?: string) {
  await page.goto('/published-items')
  await page.getByLabel('Conta').selectOption(account)
  if (search) await page.getByLabel('Buscar').fill(search)
  await page.getByRole('button', { name: 'Filtrar' }).click()
}
const row = (page: Page, externalId: string) => page.getByRole('listitem').filter({ hasText: `Anúncio: ${externalId}` })

async function createMarketplaceChannel(page: Page): Promise<string> {
  const code = `E2EML${TAG}`
  await page.goto('/pricing')
  const form = formFor(page, 'Novo canal')
  await form.getByLabel('Código').fill(code)
  await form.getByLabel('Nome').fill(`Canal ${code}`)
  await form.getByLabel('Tipo').selectOption('Marketplace')
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/pricing/channels' && r.request().method() === 'POST'),
    form.getByRole('button', { name: 'Criar canal' }).click(),
  ])
  expect(response.status()).toBe(201)
  return code
}
async function createProduct(page: Page): Promise<string> {
  await page.goto('/products')
  const form = formFor(page, 'Novo produto')
  await form.getByLabel('Código').fill(PRODUCT_CODE)
  await form.getByLabel('Nome').fill(`Produto ${PRODUCT_CODE}`)
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/products' && r.request().method() === 'POST'),
    form.getByRole('button', { name: 'Criar produto' }).click(),
  ])
  expect(response.status()).toBe(201)
  return (await response.json() as { id: string }).id
}

function ownerOnly() { test.skip(test.info().project.name !== 'marketplace-ml', 'owner journey runs in the marketplace-ml project') }

test.describe('S8C.2 D11 — Mercado Livre listings journey (real browser, fake provider, disposable PostgreSQL)', () => {
  test.describe.configure({ mode: 'serial' })
  test.setTimeout(240_000)

  let channelCode = '', productId = '', account = '', productCodeLabel = ''
  let firstRun: Run, secondRun: Run, partialRun: Run, retryRun: Run
  const startPosts: string[] = []

  test.beforeEach(async ({ page }) => {
    page.on('request', r => { if (r.method() === 'POST' && /\/listing-syncs$/.test(new URL(r.url()).pathname)) startPosts.push(r.url()) })
  })

  test('0. fixtures: marketplace channel, product, fake provider seeded with listings A and B', async ({ page, request }) => {
    ownerOnly()
    channelCode = await createMarketplaceChannel(page)
    productId = await createProduct(page)
    productCodeLabel = PRODUCT_CODE
    catalog.splice(0, catalog.length,
      { id: ids.a, sku: SKU.a, title: `Anúncio A ${TAG}`, price: 10 },
      { id: ids.b, sku: SKU.b, title: `Anúncio B ${TAG}`, price: 20, variations: 2 })
    await setProvider(request, catalog, true)
    expect((await providerCalls(request)).tokenExchanges).toBe(0)
  })

  test('1. OAuth: connect through the real flow with the fake provider and reach CONNECTED', async ({ page, request }) => {
    ownerOnly()
    await page.goto('/marketplace-accounts')
    startPosts.length = 0
    await page.getByRole('button', { name: 'Conectar conta' }).click()
    await selectByText(page.getByLabel('Provedor'), /Mercado Livre/i)
    await selectByText(page.getByLabel('Canal'), channelCode)
    await page.getByLabel('Nome de exibição').fill(DISPLAY_NAME)

    let authorizeUrl: URL | null = null
    await page.route('https://auth.mercadolivre.com.br/**', async route => {
      authorizeUrl = new URL(route.request().url())
      const state = authorizeUrl.searchParams.get('state') ?? ''
      await route.fulfill({ status: 302, headers: { location: `${CALLBACK}?state=${encodeURIComponent(state)}&code=e2e-ml-code` } })
    })
    await page.evaluate(() => window.history.replaceState(null, '', window.location.pathname))
    await page.getByRole('button', { name: 'Autorizar com provedor' }).click()
    await page.waitForURL(/\/marketplace-accounts\?authorization=completed$/)
    await page.unroute('https://auth.mercadolivre.com.br/**')

    expect(authorizeUrl, 'the browser went through the (intercepted) provider consent URL').not.toBeNull()
    expect(authorizeUrl!.searchParams.get('client_id')).toBe('900000001')
    expect(authorizeUrl!.searchParams.get('redirect_uri')).toBe(CALLBACK)
    expect(authorizeUrl!.searchParams.get('response_type')).toBe('code')

    account = await accountId(page)
    await page.getByRole('button', { name: DISPLAY_NAME, exact: true }).click()
    await expect(page.getByText(/Estado de autorização:/)).toContainText('CONNECTED')
    await expect(page.getByText(/LISTINGS_READ: GRANTED/)).toBeVisible()
    expect(startPosts, 'selecting an account never starts a sync').toHaveLength(0)

    // Persistence evidence (disposable database) and provider-call evidence.
    expect(sql(`SELECT authorization_state FROM commerce.marketplace_account_connection WHERE marketplace_account_id = '${account}'`)).toBe('CONNECTED')
    expect(sql(`SELECT external_account_id FROM commerce.marketplace_account WHERE id = '${account}'`)).toBe(String(SELLER_ID))
    expect(sql(`SELECT state FROM commerce.marketplace_account_capability WHERE marketplace_account_id = '${account}' AND capability_code = 'LISTINGS_READ'`)).toBe('GRANTED')
    const calls = await providerCalls(request)
    expect(calls.tokenExchanges).toBe(1)
    expect(calls.usersMe).toBeGreaterThanOrEqual(1)
    expect(calls.scans, 'connecting never reads listings').toBe(0)
    // The seller's tokens never reach the browser: neither the page nor its storage carries them.
    expect(await page.content()).not.toMatch(/e2e-at-|e2e-rt-|e2e-only-secret|e2e-ml-code/)
  })

  test('2. first Full sync: SUCCEEDED, listings imported, stock explicitly not synchronized', async ({ page, request }) => {
    ownerOnly()
    startPosts.length = 0
    await openAccountPanel(page)
    expect(startPosts, 'opening the page never starts a sync').toHaveLength(0)
    await expect(syncPanel(page).getByText('Nunca sincronizada')).toBeVisible()
    await expect(syncPanel(page).getByRole('button', { name: 'Tentar novamente itens com erro' })).toHaveCount(0)

    firstRun = await startSync(page, account)
    expect(firstRun.status).toBe('SUCCEEDED')
    expect(firstRun.counters).toMatchObject({ total: 2, found: 2, blockingRetryable: 0, blockingNonRetryable: 0, pending: 0 })
    const group = syncPanel(page).getByRole('group', { name: 'Última execução' })
    await expect(group).toContainText('Concluída')
    await expect(group).toContainText('Total: 2 · Encontrados: 2 · Com erro (repetíveis): 0 · Com erro (não repetíveis): 0 · Pendentes: 0')
    await expect(syncPanel(page)).toContainText('Anúncios em dia')

    await openPublishedItems(page, account)
    await expect(page.getByText('2 item(ns)')).toBeVisible()
    for (const id of [ids.a, ids.b]) await expect(row(page, id)).toBeVisible()
    await expect(page.getByText('Estoque: não sincronizado')).toHaveCount(2)
    await expect(row(page, ids.b)).toContainText('Variações: 2')
    await expect(row(page, ids.a)).toContainText(`SKU: ${SKU.a}`)
    const body = await page.locator('body').innerText()
    expect(body).not.toMatch(/seller_id|permalink|last_updated|e2e-at-|scroll/i)
    expect(body).not.toMatch(/em estoque|quantidade disponível|saldo/i)

    expect(sql(`SELECT count(*) FROM commerce.marketplace_listing WHERE marketplace_account_id = '${account}'`)).toBe('2')
    expect(sql(`SELECT status FROM commerce.marketplace_listing_sync_run WHERE id = '${firstRun.runId}'`)).toBe('SUCCEEDED')
    const calls = await providerCalls(request)
    expect(calls.scans, 'one scan = the first page plus the empty continuation that ends it (ADR-0025 §B.2)').toBe(2)
    expect(calls.bulkIds.flat().sort()).toEqual([ids.a, ids.b].sort())
    expect(calls.nonGetOutsideOAuth, 'no write to the provider').toBe(0)
    expect(calls.stockOrWriteLike, 'no stock or inventory route is ever called').toBe(0)
    expect(calls.unexpectedRoutes).toBe(0)
  })

  test('3. a repeated sync with unchanged data creates no duplicate and no observation', async ({ page, request }) => {
    ownerOnly()
    const before = { a: await observations(request, ids.a), b: await observations(request, ids.b) }
    expect(before.a.observations).toHaveLength(1); expect(before.b.observations).toHaveLength(1)
    await openAccountPanel(page)
    secondRun = await startSync(page, account)
    expect(secondRun.status).toBe('SUCCEEDED'); expect(secondRun.runId).not.toBe(firstRun.runId)
    expect(secondRun.counters).toMatchObject({ total: 2, found: 2 })
    const after = { a: await observations(request, ids.a), b: await observations(request, ids.b) }
    expect(after.a.listings).toBe(1); expect(after.b.listings).toBe(1)
    expect(after.a.listingIds).toEqual(before.a.listingIds)
    expect(after.a.observations, 'identical facts append nothing').toHaveLength(1); expect(after.b.observations).toHaveLength(1)
    expect(sql(`SELECT count(*) FROM commerce.marketplace_listing WHERE marketplace_account_id = '${account}'`)).toBe('2')
    expect(sql(`SELECT count(*) FROM commerce.marketplace_listing_sync_run WHERE marketplace_account_id = '${account}' AND status = 'SUCCEEDED'`)).toBe('2')
    await expect(syncPanel(page).getByRole('button', { name: 'Tentar novamente itens com erro' })).toHaveCount(0)
    await openPublishedItems(page, account)
    await expect(page.getByText('2 item(ns)')).toBeVisible()
  })

  test('4. observation history A -> B -> A: three observations, stable identity, current state follows the last one', async ({ page, request }) => {
    ownerOnly()
    const listingBefore = (await observations(request, ids.a)).listingIds[0]
    const prices = [12.5, 10]
    let previousRun = secondRun.runId
    for (const price of prices) {
      catalog.find(x => x.id === ids.a)!.price = price
      await setProvider(request, catalog)
      await openAccountPanel(page)
      const run = await startSync(page, account)
      expect(run.status).toBe('SUCCEEDED'); expect(run.runId).not.toBe(previousRun); previousRun = run.runId
    }
    const history = await observations(request, ids.a)
    expect(history.listings).toBe(1); expect(history.listingIds[0], 'the listing identity never changes').toBe(listingBefore)
    expect(history.observations.map(o => o.price)).toEqual([10, 12.5, 10])
    expect(new Set(history.observations.map(o => o.provenance))).toEqual(new Set(['PROVIDER_SYNC']))
    expect(sql(`SELECT observed_price FROM commerce.marketplace_listing WHERE id = '${listingBefore}'`)).toBe('10.00')
    await openPublishedItems(page, account, ids.a)
    await expect(row(page, ids.a)).toContainText(/R\$\s*10,00/)
    expect((await observations(request, ids.b)).observations, 'B never changed').toHaveLength(1)
  })

  test('5. partial run: two provider failures give PARTIAL, honest counters, failures reachable in the panel', async ({ page, request }) => {
    ownerOnly()
    catalog.push({ id: ids.f1, sku: SKU.f1, title: `Falha 1 ${TAG}`, price: 30, fail: true }, { id: ids.f2, sku: SKU.f2, title: `Falha 2 ${TAG}`, price: 40, fail: true })
    await setProvider(request, catalog)
    await openAccountPanel(page)
    partialRun = await startSync(page, account)
    expect(partialRun.status).toBe('PARTIAL')
    expect(partialRun.counters).toMatchObject({ total: 4, found: 2, blockingRetryable: 2, blockingNonRetryable: 0, pending: 0 })

    const panel = syncPanel(page)
    const group = panel.getByRole('group', { name: 'Última execução' })
    await expect(group).toContainText('Concluída com pendências')
    await expect(group).toContainText('Total: 4 · Encontrados: 2 · Com erro (repetíveis): 2 · Com erro (não repetíveis): 0 · Pendentes: 0')
    await expect(panel).toContainText('Requer atenção'); await expect(panel).not.toContainText('Anúncios em dia')
    await expect(panel).toContainText('Itens bloqueantes em aberto: 2')
    const status = await syncStatus(page, account)
    expect(status.summary.health).toBe('ATTENTION_REQUIRED'); expect(status.summary.openBlockingItemCount).toBe(2)

    const failures = panel.getByRole('group', { name: 'Itens com falha' })
    await expect(failures.getByLabel('Resultado')).toHaveValue('TRANSIENT_ERROR')
    for (const id of [ids.f1, ids.f2]) await expect(failures.getByText(id)).toBeVisible()
    await expect(failures.getByText('Falha ao ler este anúncio.')).toHaveCount(2)
    await failures.getByLabel('Resultado').selectOption('PERMANENT_ERROR')
    await expect(failures.getByText('Nenhum item com este resultado.')).toBeVisible()
    await failures.getByLabel('Resultado').selectOption('TRANSIENT_ERROR')
    await expect(failures.getByText(ids.f1)).toBeVisible()
    await expect(panel.getByRole('button', { name: 'Tentar novamente itens com erro' })).toBeVisible()

    expect(sql(`SELECT count(*) FROM commerce.marketplace_listing_sync_run_item WHERE sync_run_id = '${partialRun.runId}' AND outcome = 'TRANSIENT_ERROR'`)).toBe('2')
    expect(sql(`SELECT count(*) FROM commerce.marketplace_listing WHERE marketplace_account_id = '${account}' AND external_listing_id IN ('${ids.f1}','${ids.f2}')`), 'failed ids never became listings').toBe('0')
  })

  test('6. retry recovers exactly the failed items from the server-derived run, without polling beyond the active run', async ({ page, request }) => {
    ownerOnly()
    for (const item of catalog) if (item.fail) item.fail = false
    await setProvider(request, catalog)
    await openAccountPanel(page)
    const callsBefore = await providerCalls(request)
    const bulkBefore = callsBefore.bulkRequests, scansBefore = callsBefore.scans

    const retryRequest = page.waitForRequest(r => r.method() === 'POST' && /\/marketplace-listing-syncs\/[^/]+\/retry$/.test(new URL(r.url()).pathname))
    await syncPanel(page).getByRole('button', { name: 'Tentar novamente itens com erro' }).click()
    const sent = await retryRequest
    expect(new URL(sent.url()).pathname).toBe(`/api/commerce/marketplace-listing-syncs/${partialRun.runId}/retry`)
    expect(sent.postData() ?? '', 'the browser sends no body and never any external id').toBe('')

    retryRun = await waitForNewTerminalRun(page, account, partialRun.runId)
    expect(retryRun.status).toBe('SUCCEEDED'); expect(retryRun.kind).toBe('FAILED_ONLY')
    expect(retryRun.counters).toMatchObject({ total: 2, found: 2, blockingRetryable: 0, pending: 0 })
    expect(sql(`SELECT retry_of_run_id FROM commerce.marketplace_listing_sync_run WHERE id = '${retryRun.runId}'`)).toBe(partialRun.runId)

    const panel = syncPanel(page)
    await expect(panel.getByRole('group', { name: 'Última execução' })).toContainText('Concluída')
    await expect(panel).toContainText('Anúncios em dia'); await expect(panel).not.toContainText('Requer atenção')
    await expect(panel.getByRole('button', { name: 'Tentar novamente itens com erro' })).toHaveCount(0)
    await expect(panel.getByRole('group', { name: 'Itens com falha' })).toHaveCount(0)
    const status = await syncStatus(page, account)
    expect(status.summary.health).toBe('HEALTHY'); expect(status.summary.openBlockingItemCount).toBe(0)

    const calls = await providerCalls(request)
    expect(calls.scans, 'a FAILED_ONLY run never enumerates').toBe(scansBefore)
    expect(calls.bulkRequests).toBe(bulkBefore + 1)
    expect(calls.bulkIds.at(-1)!.sort(), 'only the failed ids were re-read').toEqual([ids.f1, ids.f2].sort())
    for (const id of [ids.f1, ids.f2]) expect((await observations(request, id)).observations).toHaveLength(1)
    expect((await observations(request, ids.a)).observations, 'unfailed listings gained no observation').toHaveLength(3)
    expect(sql(`SELECT count(*) FROM commerce.marketplace_listing WHERE marketplace_account_id = '${account}'`)).toBe('4')

    // Internal polling ended with the run; Published Items never polls.
    const statusReads: string[] = []
    page.on('request', r => { if (r.method() === 'GET' && /\/listing-sync$/.test(new URL(r.url()).pathname)) statusReads.push(r.url()) })
    await page.waitForTimeout(6_500)
    expect(statusReads, 'no status polling after the run is terminal').toHaveLength(0)
    const itemReads: string[] = []
    page.on('request', r => { if (r.url().includes('/api/commerce/published-items')) itemReads.push(r.url()) })
    await openPublishedItems(page, account)
    await expect(page.getByText('4 item(ns)')).toBeVisible()
    const afterLoad = itemReads.length
    await page.waitForTimeout(6_500)
    expect(itemReads.length, 'Published Items does not poll').toBe(afterLoad)
  })

  test('7. confirm SKU for a manually linked listing, then a new listing with that SKU links deterministically; the original is untouched', async ({ page, request }) => {
    ownerOnly()
    // C: a new listing that carries the SKU to be confirmed; link it MANUALLY to the real product.
    catalog.push({ id: ids.c, sku: SKU.confirmed, title: `Anúncio C ${TAG}`, price: 55 })
    await setProvider(request, catalog)
    await openAccountPanel(page)
    expect((await startSync(page, account)).status).toBe('SUCCEEDED')

    await openPublishedItems(page, account, ids.c)
    await expect(row(page, ids.c)).toBeVisible()
    await expect(row(page, ids.c).getByRole('button', { name: 'Confirmar SKU → Produto para esta conta' }), 'not offered for an unlinked listing').toHaveCount(0)
    await row(page, ids.c).getByRole('button', { name: 'Vincular' }).click()
    await selectByText(page.getByLabel('Produto', { exact: true }), PRODUCT_CODE)
    await page.getByRole('button', { name: 'Confirmar vínculo' }).click()
    await expect(page.getByText('Listagem vinculada.')).toBeVisible()
    await expect(row(page, ids.c)).toContainText('Vinculado manualmente')
    expect(sql(`SELECT count(*) FROM commerce.marketplace_account_sku_mapping WHERE marketplace_account_id = '${account}'`), 'a manual link creates no SKU authority').toBe('0')

    const listingC = (await observations(request, ids.c)).listingIds[0]
    const versionBefore = sql(`SELECT version FROM commerce.marketplace_listing WHERE id = '${listingC}'`)
    let confirmBody = ''
    page.on('request', r => { if (r.method() === 'POST' && /\/sku-mappings$/.test(new URL(r.url()).pathname)) confirmBody = r.postData() ?? '' })
    await row(page, ids.c).getByRole('button', { name: 'Confirmar SKU → Produto para esta conta' }).click()
    await expect(page.getByText(new RegExp(`SKU ${SKU.confirmed} confirmado para esta conta`))).toBeVisible()
    expect(JSON.parse(confirmBody)).toEqual({ externalSku: SKU.confirmed, productId })
    await expect(row(page, ids.c)).toContainText('Vinculado manualmente')
    await expect(row(page, ids.c)).not.toContainText('Vinculado automaticamente por mapeamento de SKU confirmado')
    expect(sql(`SELECT count(*) FROM commerce.marketplace_account_sku_mapping WHERE marketplace_account_id = '${account}' AND external_sku = '${SKU.confirmed}' AND active`)).toBe('1')
    expect(sql(`SELECT linkage_source || '|' || coalesce(sku_mapping_id::text,'') FROM commerce.marketplace_listing WHERE id = '${listingC}'`), 'the current listing keeps its manual linkage').toBe('MANUAL|')
    expect(sql(`SELECT version FROM commerce.marketplace_listing WHERE id = '${listingC}'`), 'confirming never touches the listing').toBe(versionBefore)

    // The confirmed mapping is listed on Marketplace Accounts (C4).
    await openAccountPanel(page)
    await expect(skuPanel(page)).toContainText(SKU.confirmed)
    await expect(skuPanel(page)).toContainText(`${PRODUCT_CODE} · Produto ${PRODUCT_CODE}`)

    // D: a NEW listing with the same SKU appears; the next sync links it deterministically (no manual link).
    catalog.push({ id: ids.d, sku: SKU.confirmed, title: `Anúncio D ${TAG}`, price: 60 })
    await setProvider(request, catalog)
    expect((await startSync(page, account)).status).toBe('SUCCEEDED')
    await openPublishedItems(page, account, ids.d)
    await expect(row(page, ids.d)).toContainText('Vinculado automaticamente por mapeamento de SKU confirmado')
    await expect(row(page, ids.d)).toContainText(`Produto: ${PRODUCT_CODE}`)
    const mappingId = sql(`SELECT id FROM commerce.marketplace_account_sku_mapping WHERE marketplace_account_id = '${account}' AND external_sku = '${SKU.confirmed}' AND active`)
    const listingD = (await observations(request, ids.d)).listingIds[0]
    expect(sql(`SELECT linkage_state || '|' || linkage_source || '|' || sku_mapping_id::text || '|' || product_id::text || '|' || coalesce(channel_offer_id::text,'none') FROM commerce.marketplace_listing WHERE id = '${listingD}'`))
      .toBe(`LINKED|DETERMINISTIC_SKU|${mappingId}|${productId}|none`)
    // Nothing retroactive: C is still MANUAL, untouched by the later sync's linkage evaluation.
    await openPublishedItems(page, account, ids.c)
    await expect(row(page, ids.c)).toContainText('Vinculado manualmente')
    expect(sql(`SELECT linkage_source FROM commerce.marketplace_listing WHERE id = '${listingC}'`)).toBe('MANUAL')
    expect(sql(`SELECT count(*) FROM commerce.marketplace_account_sku_mapping WHERE marketplace_account_id = '${account}'`)).toBe('1')

    // Whole-journey provider evidence: read-only, no stock, nothing rejected or unexpected, seller tokens never refreshed (still valid).
    const calls = await providerCalls(request)
    expect(calls.nonGetOutsideOAuth).toBe(0); expect(calls.stockOrWriteLike).toBe(0); expect(calls.unexpectedRoutes).toBe(0); expect(calls.rejected).toBe(0)
    expect(calls.tokenExchanges).toBe(1)
    await openPublishedItems(page, account)
    const rows = await page.getByRole('listitem').filter({ hasText: 'Anúncio:' }).count()
    expect(rows).toBe(6)
    await expect(page.getByText('Estoque: não sincronizado')).toHaveCount(rows)

    writeFileSync(CONTEXT_FILE, JSON.stringify({ displayName: DISPLAY_NAME, account, sku: SKU.confirmed, listings: ids }))
  })

  test('8. outstanding PARTIAL is never shown healthy: a fresh failure flips health back to attention and retry reappears', async ({ page, request }) => {
    ownerOnly()
    catalog.find(x => x.id === ids.f1)!.fail = true
    await setProvider(request, catalog)
    await openAccountPanel(page)
    const run = await startSync(page, account)
    expect(run.status).toBe('PARTIAL')
    await expect(syncPanel(page)).toContainText('Concluída com pendências'); await expect(syncPanel(page)).not.toContainText('Anúncios em dia')
    await expect(syncPanel(page).getByRole('button', { name: 'Tentar novamente itens com erro' })).toBeVisible()
    // The known-but-failing listing keeps its last known facts (never inferred closed or deleted).
    expect((await observations(request, ids.f1)).observations).toHaveLength(1)
    catalog.find(x => x.id === ids.f1)!.fail = false
    await setProvider(request, catalog)
  })
})

test.describe('S8C.2 D11 — Viewer is read-only on the Mercado Livre surfaces', () => {
  test.beforeEach(({}, testInfo) => { test.skip(testInfo.project.name !== 'marketplace-ml-viewer', 'viewer project only') })

  test('a Viewer sees status and SKUs but can start, retry, deactivate and confirm nothing', async ({ page }) => {
    expect(existsSync(CONTEXT_FILE), 'the owner journey left its context file').toBe(true)
    const context = JSON.parse(readFileSync(CONTEXT_FILE, 'utf8')) as { displayName: string; account: string; sku: string; listings: typeof ids }
    const posts: string[] = []
    page.on('request', r => { if (r.method() !== 'GET') posts.push(`${r.method()} ${new URL(r.url()).pathname}`) })

    await page.goto('/marketplace-accounts')
    await page.getByRole('button', { name: context.displayName, exact: true }).click()
    await expect(syncPanel(page)).toBeVisible()
    await expect(syncPanel(page).getByRole('group', { name: 'Última execução' })).toBeVisible()
    await expect(skuPanel(page)).toContainText(context.sku)
    for (const name of ['Sincronizar anúncios', 'Tentar novamente itens com erro', 'Reautorizar', 'Sondar disponibilidade', 'Desconectar conta'])
      await expect(page.getByRole('button', { name })).toHaveCount(0)
    await expect(page.getByRole('button', { name: /Desativar SKU/ })).toHaveCount(0)

    await openPublishedItems(page, context.account)
    await expect(row(page, context.listings.d)).toContainText('Vinculado automaticamente por mapeamento de SKU confirmado')
    await expect(page.getByRole('button', { name: /Confirmar SKU/ })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Vincular' })).toHaveCount(0)

    // The server refuses too: a Viewer cannot start a sync even by calling the API directly from the app origin.
    const status = await page.evaluate(async (accountId) => {
      const token = document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))?.split('=')[1]
      const r = await fetch(`/api/commerce/marketplace-accounts/${accountId}/listing-syncs`, { method: 'POST', credentials: 'include', headers: token ? { 'X-XSRF-TOKEN': decodeURIComponent(token) } : {} })
      return r.status
    }, context.account)
    expect(status).toBe(403)
    expect(posts.filter(p => !p.includes('/listing-syncs')), 'the Viewer UI sent no mutation').toEqual([])
  })
})
