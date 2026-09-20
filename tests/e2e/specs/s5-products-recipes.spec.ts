import { test, expect, type Locator, type Page } from '@playwright/test'

function uniqueCode(prefix: string) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
}

function formFor(page: Page, headingName: string | RegExp) {
  return page.locator('form').filter({ has: page.getByRole('heading', { name: headingName }) })
}

function totalValue(result: Locator, label: string) {
  return result.locator('dt').filter({ hasText: label }).locator('..').locator('dd')
}

async function createSupply(page: Page, code: string, name: string) {
  await page.goto('/supplies')
  await expect(page.getByRole('heading', { name: 'Suprimentos e estoque' })).toBeVisible()
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

/** Creates a product through the real UI and leaves the recipe editor open, returning its
 * product id (read back from the URL-less SPA via the API response instead). */
async function createProduct(page: Page, code: string, name: string): Promise<string> {
  await page.goto('/products')
  await expect(page.getByRole('heading', { name: 'Produtos e receitas' })).toBeVisible()
  const createForm = formFor(page, 'Novo produto')
  await createForm.getByLabel('Código').fill(code)
  await createForm.getByLabel('Nome').fill(name)
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/products' && r.request().method() === 'POST'),
    createForm.getByRole('button', { name: 'Criar produto' }).click(),
  ])
  expect(response.status()).toBe(201)
  const body = await response.json() as { id: string }
  await expect(page.getByRole('heading', { name: new RegExp(`Receita \\(BOM\\) de ${name}`) })).toBeVisible()
  return body.id
}

async function addRecipeMaterial(page: Page, index: number, code: string, quantity: string) {
  await page.getByRole('button', { name: 'Adicionar material' }).click()
  const line = page.getByRole('group', { name: `Material ${index}` })
  const optionValue = await line.getByLabel('Insumo').locator('option').filter({ hasText: code }).getAttribute('value')
  expect(optionValue, `the active costing picker must contain ${code}`).not.toBeNull()
  await line.getByLabel('Insumo').selectOption(optionValue!)
  await line.getByLabel('Quantidade').fill(quantity)
  return line
}

async function saveRecipe(page: Page) {
  const [response] = await Promise.all([
    page.waitForResponse(r => /\/api\/products\/[^/]+\/recipe$/.test(new URL(r.url()).pathname) && r.request().method() === 'PUT'),
    page.getByRole('button', { name: 'Salvar receita' }).click(),
  ])
  expect(response.status()).toBe(200)
  await expect(page.getByRole('status')).toHaveText('Receita salva com sucesso.')
  return response
}

/** Reopens a product from the list after a reload — the recipe editor is component state, not
 * a deep-linkable route, so persistence is proven the same way s3-supplies.spec.ts does it:
 * search, click, and check what comes back from the server. */
async function reopenProduct(page: Page, code: string) {
  await page.goto('/products')
  await page.getByLabel('Buscar produtos').fill(code)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: new RegExp(code) }).click()
}

async function calculateProductCost(page: Page) {
  const [response] = await Promise.all([
    page.waitForResponse(r => /\/api\/products\/[^/]+\/cost$/.test(new URL(r.url()).pathname)),
    page.getByRole('button', { name: 'Calcular custo atual' }).click(),
  ])
  expect(response.status()).toBe(200)
  return page.locator('section.cost-result')
}

test('S5 A: a persisted recipe reuses the S4 CostEngine and the current cost is calculable end to end', async ({ page }) => {
  test.setTimeout(90_000)
  const supplyCode = uniqueCode('S5-BASIS')
  await createSupply(page, supplyCode, `Material ${supplyCode}`)
  await purchase(page, supplyCode, '1000', 'Gram', '100')

  const productCode = uniqueCode('S5-PROD')
  await createProduct(page, productCode, `Produto ${productCode}`)
  await addRecipeMaterial(page, 1, supplyCode, '100')
  await saveRecipe(page)

  const result = await calculateProductCost(page)
  await expect(result.getByRole('heading', { name: `${supplyCode} · Material ${supplyCode}` })).toBeVisible()
  await expect(result.getByRole('article')).toContainText('R$ 10,00 + R$ 0,00 de perda')
  await expect(result.locator('.unit-cost')).toHaveText('R$ 10,00 por unidade')

  // The recipe and its lines must have actually persisted, not just be held in component state.
  await reopenProduct(page, productCode)
  await expect(page.getByRole('heading', { name: new RegExp(`Receita \\(BOM\\) de Produto ${productCode}`) })).toBeVisible()
  await expect(page.getByRole('group', { name: 'Material 1' }).getByLabel('Insumo')).toHaveValue(/.+/)
})

test('S5 B: a multi-material recipe with labor, machine, additional cost and batch output is explained and persists', async ({ page }) => {
  test.setTimeout(90_000)
  const black = uniqueCode('S5-BLACK')
  await createSupply(page, black, `PLA Black ${black}`)
  await purchase(page, black, '1000', 'Gram', '100')
  const gold = uniqueCode('S5-GOLD')
  await createSupply(page, gold, `PLA Gold ${gold}`)
  await purchase(page, gold, '500', 'Gram', '100')

  const productCode = uniqueCode('S5-MULTI')
  await createProduct(page, productCode, `Multi ${productCode}`)
  await addRecipeMaterial(page, 1, black, '120')
  await addRecipeMaterial(page, 2, gold, '35')

  const labor = page.getByRole('region', { name: 'Mão de obra' })
  await labor.getByLabel('Minutos').fill('20')
  await labor.getByLabel('Taxa manual por hora (R$)').fill('30')
  const machine = page.getByRole('region', { name: 'Máquina' })
  await machine.getByLabel('Minutos').fill('180')
  await machine.getByLabel('Taxa por hora (R$)').fill('2')
  await page.getByRole('button', { name: 'Adicionar custo' }).click()
  const additional = page.getByRole('region', { name: 'Custos adicionais' })
  await additional.getByLabel('Descrição 1').fill('Embalagem')
  await additional.getByLabel('Valor (R$)').fill('4')
  await page.getByLabel('Quantidade produzida por lote').fill('2')
  await saveRecipe(page)

  const result = await calculateProductCost(page)
  // 120g@0.1 + 35g@0.2 = 12 + 7 = 19; labor 20min@30/h = 10; machine 180min@2/h = 6; additional 4.
  await expect(totalValue(result, 'Materiais com perda')).toHaveText('R$ 19,00')
  await expect(totalValue(result, 'Mão de obra')).toHaveText('R$ 10,00')
  await expect(totalValue(result, 'Máquina')).toHaveText('R$ 6,00')
  await expect(totalValue(result, 'Custos adicionais')).toHaveText('R$ 4,00')
  await expect(totalValue(result, 'Total estimado do lote (2 un.)')).toHaveText('R$ 39,00')
  await expect(totalValue(result, 'Custo estimado unitário')).toHaveText('R$ 19,50')

  await reopenProduct(page, productCode)
  await expect(page.getByRole('group', { name: /Material/ })).toHaveCount(2)
})

test('S5 F: an update against a stale product version is rejected as a concurrency conflict', async ({ page }) => {
  test.setTimeout(60_000)
  const productCode = uniqueCode('S5-CONFLICT')
  const productId = await createProduct(page, productCode, `Conflito ${productCode}`)

  const csrf = await page.evaluate(() => document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1])
  // Simulate a concurrent edit from another actor that lands between this page's initial load
  // and its own submit — the UI must not silently overwrite it.
  const external = await page.request.put(`/api/products/${productId}`, {
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(csrf ?? '') },
    data: { name: `Renomeado externamente ${productCode}`, description: null, version: 1 },
  })
  expect(external.status()).toBe(200)

  const editForm = formFor(page, /Editar produto|Novo produto/)
  await editForm.getByLabel('Nome').fill(`Minha edição ${productCode}`)
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === `/api/products/${productId}` && r.request().method() === 'PUT'),
    editForm.getByRole('button', { name: 'Salvar alterações' }).click(),
  ])
  expect(response.status()).toBe(409)
  await expect(page.getByRole('status')).toHaveText('Esta ação não pôde ser concluída porque os dados mudaram. Atualize a página e tente novamente.')
})
