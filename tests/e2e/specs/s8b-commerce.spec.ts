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

  // Create a local Marketplace channel, then configure an account through its Owner-only UI.
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
  const channel = await channelResponse.json() as { id: string }

  await page.goto('/marketplace-accounts')
  await page.getByRole('button', { name: 'Nova conta' }).click()
  await page.getByLabel('Provedor').selectOption('SHOPEE')
  await page.getByLabel('Identidade externa').fill(`shop-${marker}`)
  await selectByText(page.getByLabel('Canal'), channelCode)
  await page.getByLabel('Nome de exibição').fill(`Loja ${marker}`)
  const [accountCreate] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/commerce/marketplace-accounts' && r.request().method() === 'POST'),
    page.getByRole('button', { name: 'Salvar conta' }).click(),
  ])
  expect(accountCreate.status()).toBe(204)
  await expect(page.getByRole('button', { name: `Loja ${marker}` })).toBeVisible()
  const accountsResponse = await page.request.get('/api/commerce/marketplace-accounts')
  const accounts = await accountsResponse.json() as { id: string; externalAccountId: string }[]
  const account = accounts.find(x => x.externalAccountId === `shop-${marker}`)
  expect(account).toBeTruthy()

  // Listing creation is an explicit local fixture API in S8B; no provider/network is contacted.
  const csrf = await page.evaluate(() => document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1])
  const externalListingId = `listing-${marker}`
  const listingCreate = await page.request.post('/api/commerce/published-items', {
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(csrf ?? '') },
    data: {
      marketplaceAccountId: account!.id, externalListingId, externalSku: marker,
      titleSnapshot: `Anúncio ${marker}`, observedPrice: 54.90, observedStatus: 'ACTIVE',
    },
  })
  expect(listingCreate.status()).toBe(204)

  await page.goto('/published-items')
  await page.getByLabel('Buscar').fill(marker)
  await page.getByRole('button', { name: 'Filtrar' }).click()
  const listingRow = page.getByRole('listitem').filter({ hasText: `Anúncio ${marker}` })
  await expect(listingRow).toBeVisible()
  await expect(listingRow.getByText('UNLINKED', { exact: true })).toBeVisible()
  await listingRow.getByRole('button', { name: 'Vincular' }).click()
  await selectByText(page.getByLabel('Produto'), marker)
  const compatibleOffer = page.getByLabel('Oferta compatível (opcional)')
  await expect(compatibleOffer.locator('option').filter({ hasText: 'Somente produto' })).toHaveCount(1)
  await expect(compatibleOffer).toHaveValue('')
  const [linkResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/link')),
    page.getByRole('button', { name: 'Confirmar vínculo' }).click(),
  ])
  expect(linkResponse.status()).toBe(204)
  await expect(listingRow.getByText('LINKED', { exact: true })).toBeVisible()
  await expect(listingRow.getByText(new RegExp(`Produto: ${marker}`))).toBeVisible()
  const [unlinkResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/unlink')),
    listingRow.getByRole('button', { name: 'Desvincular' }).click(),
  ])
  expect(unlinkResponse.status()).toBe(204)
  await expect(listingRow.getByText('UNLINKED', { exact: true })).toBeVisible()

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
    await viewer.goto('/published-items')
    await viewer.getByLabel('Buscar').fill(marker)
    await viewer.getByRole('button', { name: 'Filtrar' }).click()
    await expect(viewer.getByText(`Anúncio ${marker}`)).toBeVisible()
    await expect(viewer.getByRole('button', { name: 'Vincular' })).not.toBeVisible()
  } finally {
    await viewerContext.close()
  }
})
