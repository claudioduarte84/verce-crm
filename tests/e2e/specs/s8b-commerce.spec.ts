import { expect, test, type Locator, type Page } from '@playwright/test'
import { resolve } from 'node:path'

function uniqueCode(prefix: string) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
}

function formFor(page: Page, heading: string | RegExp): Locator {
  return page.locator('form').filter({ has: page.getByRole('heading', { name: heading }) })
}

async function selectByText(select: Locator, text: string | RegExp) {
  const value = await select.locator('option').filter({ hasText: text }).first().getAttribute('value')
  expect(value, `expected option matching ${text}`).not.toBeNull()
  await select.selectOption(value!)
}

test('S8B: Owner completes the local Commerce journey and Viewer remains read-only', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const marker = uniqueCode('S8B')
  const productName = `Produto Commerce ${marker}`

  // Owner creates a real Product through the existing Product workflow.
  await page.goto('/products')
  const productForm = formFor(page, 'Novo produto')
  await productForm.getByLabel('Código').fill(marker)
  await productForm.getByLabel('Nome').fill(productName)
  const [productResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/products' && r.request().method() === 'POST'),
    productForm.getByRole('button', { name: 'Criar produto' }).click(),
  ])
  expect(productResponse.status()).toBe(201)
  const product = await productResponse.json() as { id: string }

  // Owner creates and activates the DIRECT offer from Catalog.
  await page.goto('/catalog')
  await page.getByLabel('Buscar').fill(marker)
  await page.getByRole('button', { name: 'Filtrar' }).click()
  await page.getByRole('button', { name: new RegExp(marker) }).click()
  await selectByText(page.getByLabel('Canal').last(), /Venda Direta/i)
  await page.getByLabel('Preço pretendido').fill('49.90')
  const [offerCreate] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/commerce/offers' && r.request().method() === 'POST'),
    page.getByRole('button', { name: 'Salvar oferta' }).click(),
  ])
  const offerBody = await offerCreate.text()
  expect(offerCreate.status(), offerBody).toBe(201)
  const offer = JSON.parse(offerBody) as { id: string; version: number }
  // The POST response precedes the Catalog reload that deliberately closes the editor.
  // Wait for that state transition before reopening the product, otherwise the trailing
  // setSelected(null) can race this click and leave no activation control on screen.
  await expect(page.getByRole('heading', { name: 'Criar ou editar oferta' })).toBeHidden()
  const productRow = page.getByRole('listitem').filter({ hasText: marker })
  await productRow.getByRole('button', { name: new RegExp(marker) }).click()
  await expect(page.getByRole('button', { name: 'Ativar' })).toBeVisible()
  const [activation] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === `/api/commerce/offers/${offer.id}/activate`),
    page.getByRole('button', { name: 'Ativar' }).click(),
  ])
  expect(activation.status()).toBe(204)
  await expect(page.getByText('Comercial ativo')).toBeVisible()

  // DIRECT offers never become Published Items.
  await page.goto('/published-items')
  await page.getByLabel('Buscar').fill(marker)
  await page.getByRole('button', { name: 'Filtrar' }).click()
  await expect(page.getByText('0 item(ns)')).toBeVisible()

  // Create a local Marketplace channel — still exercised exactly as before S8C.1.
  const channelCode = uniqueCode('S8BCH')
  await page.goto('/pricing')
  const channelForm = formFor(page, 'Novo canal')
  await channelForm.getByLabel('Código').fill(channelCode)
  await channelForm.getByLabel('Nome').fill(`Canal ${channelCode}`)
  await channelForm.getByLabel('Tipo').selectOption('Marketplace')
  const [channelResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/pricing/channels' && r.request().method() === 'POST'),
    channelForm.getByRole('button', { name: 'Criar canal' }).click(),
  ])
  expect(channelResponse.status()).toBe(201)

  // S8C.1 (ADR-0024) retires manual marketplace-account creation: POST
  // /api/commerce/marketplace-accounts now returns 410, and a real account can only be created
  // through a completed provider authorization callback. This pure-browser harness drives the
  // REAL, unmodified Verce.Api process (dotnet run against the compiled binary) — the S8C.1 test
  // fake connector lives only in the Verce.IntegrationTests project and is never referenced by
  // Verce.Api's dependency graph (ADR-0024 G-06: "no fake code is shipped in its project
  // dependency graph"), so there is no live or fake provider this harness can complete an
  // authorization against. Proving the actual authorization/callback/RT-01..03 behavior is done
  // by the in-process WebApplicationFactory-based PostgreSQL integration tests
  // (tests/Verce.IntegrationTests/Commerce/MarketplaceAuthorization*.cs), which DO wire the fake
  // connector directly into the host's DI container. What THIS browser-level test can and does
  // certify: the legacy manual-creation surface is gone from both the UI and the API, and the
  // new authorization-only UI is presented correctly and never collects an external identity or
  // credential value client-side.
  const legacyCreateAttempt = await page.request.post('/api/commerce/marketplace-accounts', {
    headers: { 'X-XSRF-TOKEN': decodeURIComponent((await page.evaluate(() => document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1])) ?? '') },
    data: {},
  })
  expect(legacyCreateAttempt.status()).toBe(410)

  await page.goto('/marketplace-accounts')
  await expect(page.getByRole('button', { name: 'Conectar conta' })).toBeVisible()
  await expect(page.getByLabel('Identidade externa')).toHaveCount(0)
  await page.getByRole('button', { name: 'Conectar conta' }).click()
  await expect(page.getByLabel('Provedor')).toBeVisible()
  await selectByText(page.getByLabel('Canal'), channelCode)
  await page.getByLabel('Nome de exibição').fill(`Loja ${marker}`)
  // "Autorizar com provedor" redirects the browser to the provider's own authorization URI —
  // there is deliberately no local field for an external identity or a credential anywhere in
  // this form (ADR-0024 §2: "never accepts external ID/reference").
  await expect(page.getByRole('button', { name: 'Autorizar com provedor' })).toBeVisible()

  // Deactivating the only offer removes commercial-active without deleting Product or history.
  await page.goto('/catalog')
  await page.getByLabel('Buscar').fill(marker)
  await page.getByRole('button', { name: 'Filtrar' }).click()
  await page.getByRole('button', { name: new RegExp(marker) }).click()
  const [deactivation] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === `/api/commerce/offers/${offer.id}/deactivate`),
    page.getByRole('button', { name: 'Desativar' }).click(),
  ])
  expect(deactivation.status()).toBe(204)
  await expect(productRow.getByText('Sem oferta ativa', { exact: true })).toBeVisible()

  // The real Viewer session can read both compositions and receives no mutation controls.
  const viewerContext = await browser.newContext({
    baseURL: 'https://localhost:4173', ignoreHTTPSErrors: true,
    storageState: resolve(__dirname, '..', '.playwright', 'viewer.json'),
  })
  try {
    const viewer = await viewerContext.newPage()
    await viewer.goto('/catalog')
    await viewer.getByLabel('Buscar').fill(marker)
    await viewer.getByRole('button', { name: 'Filtrar' }).click()
    await viewer.getByRole('button', { name: new RegExp(marker) }).click()
    await expect(viewer.getByRole('button', { name: 'Salvar oferta' })).not.toBeVisible()
    await expect(viewer.getByRole('button', { name: 'Ativar' })).not.toBeVisible()
    // Marketplace Accounts: Viewer gets no configuration/credential controls at all (S8C.1 —
    // CommerceEndpoints.cs Permissions.CommerceAccountsManage is Owner-only). No account was
    // actually created above (clicking "Autorizar com provedor" would navigate to a real
    // provider's authorization URI, which does not exist for the fake provider outside the
    // in-process integration tests — see the comment above), so this only certifies the
    // read-only control surface itself, not a specific connected account's detail view.
    await viewer.goto('/marketplace-accounts')
    await expect(viewer.getByRole('button', { name: 'Conectar conta' })).not.toBeVisible()
    await expect(viewer.getByText('Contas são criadas e reautorizadas somente pelo fluxo seguro do provedor.', { exact: false })).toBeVisible()
  } finally {
    await viewerContext.close()
  }
})
