import { test, expect, type Page } from '@playwright/test'

function uniqueCode(prefix: string) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
}

function formFor(page: Page, headingName: string | RegExp) {
  return page.locator('form').filter({ has: page.getByRole('heading', { name: headingName }) })
}

/** Playwright's `selectOption({ label })` only matches an exact string, never a regex, so every
 * select whose option text we only know a substring of (a generated unique code) goes through
 * this: find the matching <option>'s value, then select by that value. */
async function selectByOptionText(select: import('@playwright/test').Locator, text: string | RegExp) {
  const optionValue = await select.locator('option').filter({ hasText: text }).first().getAttribute('value')
  expect(optionValue, `expected an <option> matching ${text}`).not.toBeNull()
  await select.selectOption(optionValue!)
}

async function createSupply(page: Page, code: string, name: string) {
  await page.goto('/supplies')
  const createForm = formFor(page, 'Novo insumo')
  await createForm.getByLabel('Código').fill(code)
  await createForm.getByLabel('Nome').fill(name)
  await createForm.getByLabel('Categoria').selectOption('FILAMENT')
  await createForm.getByRole('button', { name: 'Salvar insumo' }).click()
  await expect(page.getByRole('status')).toHaveText('Insumo criado com sucesso.')
}

async function purchase(page: Page, code: string, quantity: string, unit: 'Gram' | 'Kilogram', total: string) {
  await page.getByLabel('Buscar insumos').fill(code)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: new RegExp(code) }).click()
  const purchaseForm = formFor(page, 'Registrar compra')
  await purchaseForm.getByLabel('Quantidade').fill(quantity)
  await purchaseForm.getByRole('combobox').selectOption(unit)
  await purchaseForm.getByLabel('Valor total (R$)').fill(total)
  const [response] = await Promise.all([
    page.waitForResponse(r => r.url().includes('/inventory/purchase-receipt') && r.request().method() === 'POST'),
    purchaseForm.getByRole('button', { name: 'Registrar compra' }).click(),
  ])
  expect(response.status()).toBe(200)
  await expect(page.getByRole('status')).toHaveText('Compra registrada.')
}

/** Creates a product with a single-material recipe costing exactly R$10,00 per unit
 * (100g at a R$0,10/g weighted average, no wastage, no labor/machine). */
async function createPricedProduct(page: Page, productCode: string, productName: string): Promise<string> {
  const supplyCode = uniqueCode('S5P-MAT')
  await createSupply(page, supplyCode, `Material ${supplyCode}`)
  await purchase(page, supplyCode, '1000', 'Gram', '100')

  await page.goto('/products')
  const createForm = formFor(page, 'Novo produto')
  await createForm.getByLabel('Código').fill(productCode)
  await createForm.getByLabel('Nome').fill(productName)
  const [createResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/products' && r.request().method() === 'POST'),
    createForm.getByRole('button', { name: 'Criar produto' }).click(),
  ])
  const body = await createResponse.json() as { id: string }

  await page.getByRole('button', { name: 'Adicionar material' }).click()
  const line = page.getByRole('group', { name: 'Material 1' })
  const optionValue = await line.getByLabel('Insumo').locator('option').filter({ hasText: supplyCode }).getAttribute('value')
  await line.getByLabel('Insumo').selectOption(optionValue!)
  await line.getByLabel('Quantidade').fill('100')
  await Promise.all([
    page.waitForResponse(r => /\/api\/products\/[^/]+\/recipe$/.test(new URL(r.url()).pathname) && r.request().method() === 'PUT'),
    page.getByRole('button', { name: 'Salvar receita' }).click(),
  ])
  await expect(page.getByRole('status')).toHaveText('Receita salva com sucesso.')
  return body.id
}

test('S5 C: Direct-channel pricing uses the seeded zero-fee channel and the system default margin', async ({ page }) => {
  test.setTimeout(90_000)
  const productCode = uniqueCode('S5-DIRECT')
  await createPricedProduct(page, productCode, `Direto ${productCode}`)

  await page.goto('/pricing')
  await expect(page.getByRole('heading', { name: 'Precificação e canais de venda' })).toBeVisible()
  const pricingForm = formFor(page, 'Precificar um produto')
  await selectByOptionText(pricingForm.getByLabel('Produto', { exact: true }), productCode)
  await selectByOptionText(pricingForm.getByLabel('Canal', { exact: true }), 'DIRECT')
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/price') && r.request().method() === 'POST'),
    pricingForm.getByRole('button', { name: 'Calcular preço sugerido' }).click(),
  ])
  expect(response.status()).toBe(200)
  // cost 10.00, commission 0, fixed fee 0, margin 0.35 (system default) => 10 / 0.65 = 15.3846... -> R$ 15,38.
  await expect(pricingForm.locator('.unit-cost')).toHaveText('R$ 15,38')
  await expect(pricingForm.getByText('R$ 10,00', { exact: true })).toBeVisible()
})

test('S5 D: a versioned marketplace fee rule is resolved and applied on top of the persisted product cost', async ({ page }) => {
  test.setTimeout(90_000)
  const productCode = uniqueCode('S5-MKT')
  await createPricedProduct(page, productCode, `Marketplace ${productCode}`)

  const channelCode = uniqueCode('S5CH')
  await page.goto('/pricing')
  const channelForm = formFor(page, 'Novo canal')
  await channelForm.getByLabel('Código').fill(channelCode)
  await channelForm.getByLabel('Nome').fill(`Canal ${channelCode}`)
  await channelForm.getByLabel('Tipo').selectOption('Marketplace')
  const [createChannelResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/pricing/channels' && r.request().method() === 'POST'),
    channelForm.getByRole('button', { name: 'Criar canal' }).click(),
  ])
  expect(createChannelResponse.status()).toBe(201)

  await page.getByRole('button', { name: 'Criar regra de taxas' }).click()
  const versionForm = formFor(page, 'Nova versão de taxa')
  await versionForm.getByLabel('Vigente a partir de').fill('2020-01-01')
  await versionForm.getByLabel('Comissão (%)').fill('10')
  await versionForm.getByLabel('Taxa fixa (R$)').fill('5')
  const [versionResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/fee-rule/versions') && r.request().method() === 'POST'),
    versionForm.getByRole('button', { name: 'Adicionar versão' }).click(),
  ])
  expect(versionResponse.status()).toBe(201)
  await expect(page.getByText('Comissão: 10%')).toBeVisible()

  const pricingForm = formFor(page, 'Precificar um produto')
  await selectByOptionText(pricingForm.getByLabel('Produto', { exact: true }), productCode)
  await selectByOptionText(pricingForm.getByLabel('Canal', { exact: true }), channelCode)
  await pricingForm.getByLabel('Margem desejada (%) — substitui o padrão do canal').fill('20')
  const [priceResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/price') && r.request().method() === 'POST'),
    pricingForm.getByRole('button', { name: 'Calcular preço sugerido' }).click(),
  ])
  expect(priceResponse.status()).toBe(200)
  // (10 + 5) / (1 - (0.10 + 0.20)) = 15 / 0.70 = 21.4285... -> R$ 21,43.
  await expect(pricingForm.locator('.unit-cost')).toHaveText('R$ 21,43')
})

test('S5 E: an invalid commission/margin combination is rejected with a stable, safe error message', async ({ page }) => {
  test.setTimeout(60_000)
  await page.goto('/pricing')
  const adHocForm = formFor(page, 'Simulação avulsa')
  await adHocForm.getByLabel('Custo unitário total (R$)').fill('80')
  await adHocForm.getByLabel('Comissão (%)').fill('60')
  await adHocForm.getByLabel('Margem desejada (%)').fill('45')
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/pricing/calculate' && r.request().method() === 'POST'),
    adHocForm.getByRole('button', { name: 'Calcular' }).click(),
  ])
  expect(response.status()).toBe(422)
  await expect(page.getByRole('status')).toHaveText('Não foi possível concluir a operação.')
})

test('S5 G (Terra B-01): NINETY_NINE rounding is a visible ceiling, never a reduction, through the real UI', async ({ page }) => {
  test.setTimeout(60_000)
  await page.goto('/pricing')
  const adHocForm = formFor(page, 'Simulação avulsa')
  // cost=40.995, commission=0, margin=0 => rawPrice = 40.995 exactly. The original bug
  // (Math.Floor(raw) + 0.99) rendered 40,99 here — BELOW the raw price.
  await adHocForm.getByLabel('Custo unitário total (R$)').fill('40.995')
  await adHocForm.getByLabel('Comissão (%)').fill('0')
  await adHocForm.getByLabel('Margem desejada (%)').fill('0')
  await adHocForm.getByLabel('Política de arredondamento').selectOption('NINETY_NINE')
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/pricing/calculate' && r.request().method() === 'POST'),
    adHocForm.getByRole('button', { name: 'Calcular' }).click(),
  ])
  expect(response.status()).toBe(200)
  await expect(adHocForm.locator('.unit-cost')).toHaveText('R$ 41,99')
})
