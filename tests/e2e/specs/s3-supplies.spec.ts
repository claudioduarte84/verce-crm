import { test, expect, type Page } from '@playwright/test'

function uniqueCode(prefix: string) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}`
}

/** Scopes a query to the specific `<form>` that carries the given `<h2>`/`<h3>` heading text —
 * several of the page's forms reuse the same field labels ("Quantidade", "Categoria"), so every
 * lookup must be disambiguated by its owning form rather than matched globally. */
function formFor(page: Page, headingName: string) {
  return page.locator('form').filter({ has: page.getByRole('heading', { name: headingName, exact: true }) })
}

function stockCardFor(page: Page, supplyName: string) {
  return page.locator('div.card').filter({ has: page.getByRole('heading', { name: `Estoque de ${supplyName}`, exact: true }) })
}

test('Owner creates a filament supply, records stock movements, and state survives a reload', async ({ page }) => {
  test.setTimeout(90_000)
  const code = uniqueCode('FIL-E2E')
  const name = `PLA Teste ${code}`

  await page.goto('/supplies')
  await expect(page.getByRole('heading', { name: 'Suprimentos e estoque' })).toBeVisible()

  const createForm = formFor(page, 'Novo insumo')
  await createForm.getByLabel('Código').fill(code)
  await createForm.getByLabel('Nome').fill(name)
  await createForm.getByLabel('Categoria').selectOption('FILAMENT')
  await createForm.getByLabel('Estoque mínimo').fill('1000')
  await createForm.getByLabel('Este insumo é filamento').check()
  await createForm.getByLabel('Marca').fill('Voolt3D')
  await createForm.getByLabel('Cor', { exact: true }).fill('Preto')
  await createForm.getByLabel('Diâmetro (mm)').fill('1.75')
  await createForm.getByLabel('Peso líquido do carretel (g)').fill('1000')

  const [createResponse] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/supplies' && response.request().method() === 'POST'),
    createForm.getByRole('button', { name: 'Salvar insumo' }).click(),
  ])
  expect(createResponse.status(), 'supply creation must succeed through the live API').toBe(201)
  await expect(page.getByRole('status')).toHaveText('Insumo criado com sucesso.')

  await page.getByLabel('Buscar insumos').fill(code)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: new RegExp(code) }).click()
  await expect(page.getByRole('heading', { name: `Estoque de ${name}` })).toBeVisible()

  const stockCard = stockCardFor(page, name)
  await expect(stockCard.getByText('Estoque baixo')).toBeVisible()
  await expect(stockCard.getByText('Saldo atual: 0 g')).toBeVisible()

  // Initial balance: +500g
  const initialForm = formFor(page, 'Saldo inicial')
  await initialForm.getByLabel('Quantidade').fill('500')
  const [initialResponse] = await Promise.all([
    page.waitForResponse((r) => r.url().includes('/inventory/initial-balance') && r.request().method() === 'POST'),
    initialForm.getByRole('button', { name: 'Registrar saldo inicial' }).click(),
  ])
  expect(initialResponse.status()).toBe(200)
  await expect(page.getByRole('status')).toHaveText('Saldo inicial registrado.')
  await expect(stockCard.getByText('Saldo atual: 500 g')).toBeVisible()
  await expect(stockCard.getByText('Estoque baixo')).toBeVisible()
  await expect(stockCard.getByText('InitialBalance')).toBeVisible()
  // The initial balance is one-time only — its form must not reappear once recorded.
  await expect(page.getByRole('heading', { name: 'Saldo inicial' })).toHaveCount(0)

  // Purchase receipt: +1kg (normalized to +1000g against the gram-denominated supply)
  const purchaseForm = formFor(page, 'Registrar compra')
  await purchaseForm.getByLabel('Quantidade').fill('1')
  await purchaseForm.getByRole('combobox').selectOption('Kilogram')
  await purchaseForm.getByLabel('Valor total (R$)').fill('89.90')
  await purchaseForm.getByLabel('Fornecedor').fill('Fornecedor E2E')
  await purchaseForm.getByLabel('Nota fiscal / referência').fill('NF-E2E')
  const [purchaseResponse] = await Promise.all([
    page.waitForResponse((r) => r.url().includes('/inventory/purchase-receipt') && r.request().method() === 'POST'),
    purchaseForm.getByRole('button', { name: 'Registrar compra' }).click(),
  ])
  expect(purchaseResponse.status()).toBe(200)
  await expect(page.getByRole('status')).toHaveText('Compra registrada.')
  await expect(stockCard.getByText('Saldo atual: 1.500 g')).toBeVisible()
  await expect(stockCard.getByText('Estoque normal')).toBeVisible()
  await expect(stockCard.getByText('PurchaseReceipt')).toBeVisible()

  // Manual decrease: -700g, dropping back below the 1000g minimum
  const adjustmentForm = formFor(page, 'Ajuste manual')
  await adjustmentForm.getByLabel('Tipo').selectOption('Decrease')
  await adjustmentForm.getByLabel('Quantidade').fill('700')
  await adjustmentForm.getByLabel('Motivo').fill('Consumo em teste E2E')
  const [decreaseResponse] = await Promise.all([
    page.waitForResponse((r) => r.url().includes('/inventory/adjustment') && r.request().method() === 'POST'),
    adjustmentForm.getByRole('button', { name: 'Registrar ajuste' }).click(),
  ])
  expect(decreaseResponse.status()).toBe(200)
  await expect(page.getByRole('status')).toHaveText('Ajuste registrado.')
  await expect(stockCard.getByText('Saldo atual: 800 g')).toBeVisible()
  await expect(stockCard.getByText('Estoque baixo')).toBeVisible()
  await expect(stockCard.getByText('ManualDecrease')).toBeVisible()

  // A valid entered quantity may need more than four decimal places even when its
  // normalized base-unit effect remains representable.
  await purchaseForm.getByLabel('Quantidade').fill('0.00001')
  await purchaseForm.getByRole('combobox').selectOption('Kilogram')
  await purchaseForm.getByLabel('Valor total (R$)').fill('0.01')
  const [precisionPurchaseResponse] = await Promise.all([
    page.waitForResponse((r) => r.url().includes('/inventory/purchase-receipt') && r.request().method() === 'POST'),
    purchaseForm.getByRole('button', { name: 'Registrar compra' }).click(),
  ])
  expect(precisionPurchaseResponse.status()).toBe(200)
  await expect(stockCard.getByText('Saldo atual: 800,01 g')).toBeVisible()
  await expect(stockCard.getByText('Informado: 0,00001 kg')).toBeVisible()
  await expect(stockCard.getByText('Normalizado: +0,01 g')).toBeVisible()

  // An excessive decrease must be rejected server-side and must not move the stock.
  await adjustmentForm.getByLabel('Tipo').selectOption('Decrease')
  await adjustmentForm.getByLabel('Quantidade').fill('999999')
  await adjustmentForm.getByLabel('Motivo').fill('Tentativa inválida de baixa excessiva')
  const [rejectedResponse] = await Promise.all([
    page.waitForResponse((r) => r.url().includes('/inventory/adjustment') && r.request().method() === 'POST'),
    adjustmentForm.getByRole('button', { name: 'Registrar ajuste' }).click(),
  ])
  expect(rejectedResponse.status(), 'a decrease larger than the current stock must be rejected').toBe(409)
  await expect(page.getByRole('status')).toHaveText('Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.')
  await expect(stockCard.getByText('Saldo atual: 800,01 g')).toBeVisible()
  await expect(stockCard.getByText('ManualDecrease')).toHaveCount(1)

  // Reload: everything above must have actually persisted, not just be held in component state.
  await page.reload()
  await page.getByLabel('Buscar insumos').fill(code)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: new RegExp(code) }).click()
  await expect(page.getByRole('heading', { name: `Estoque de ${name}` })).toBeVisible()
  const stockCardAfterReload = stockCardFor(page, name)
  await expect(stockCardAfterReload.getByText('Saldo atual: 800,01 g')).toBeVisible()
  await expect(stockCardAfterReload.getByText('Estoque baixo')).toBeVisible()
  await expect(stockCardAfterReload.getByText('InitialBalance')).toBeVisible()
  await expect(stockCardAfterReload.getByText('PurchaseReceipt')).toHaveCount(2)
  await expect(stockCardAfterReload.getByText('Informado: 1 kg')).toBeVisible()
  await expect(stockCardAfterReload.getByText('Informado: 0,00001 kg')).toBeVisible()
  await expect(stockCardAfterReload.getByText('Normalizado: +0,01 g')).toBeVisible()
  await expect(stockCardAfterReload.getByText('Fornecedor: Fornecedor E2E')).toBeVisible()
  await expect(stockCardAfterReload.getByText('Referência: NF-E2E')).toBeVisible()
  await expect(stockCardAfterReload.getByText('ManualDecrease')).toHaveCount(1)

  // An inactive item leaves the default operational catalogue but remains available through the
  // explicit status filters, preserving its ledger for reconciliation.
  await page.getByRole('button', { name: 'Desativar insumo' }).click()
  await expect(page.getByRole('status')).toHaveText('Insumo desativado.')
  await page.reload()
  await expect(page.getByRole('button', { name: new RegExp(code) })).toHaveCount(0)
  await page.getByLabel('Situação').selectOption('inactive')
  await expect(page.getByRole('button', { name: new RegExp(code) })).toBeVisible()
  await page.getByLabel('Situação').selectOption('all')
  await expect(page.getByRole('button', { name: new RegExp(code) })).toBeVisible()
})
