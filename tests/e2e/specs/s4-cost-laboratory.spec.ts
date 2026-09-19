import { test, expect, type Locator, type Page } from '@playwright/test'

function uniqueCode(prefix: string) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
}

function formFor(page: Page, headingName: string) {
  return page.locator('form').filter({ has: page.getByRole('heading', { name: headingName, exact: true }) })
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

async function openSupply(page: Page, code: string) {
  await page.getByLabel('Buscar insumos').fill(code)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: new RegExp(code) }).click()
}

async function purchase(page: Page, code: string, quantity: string, unit: 'Gram' | 'Kilogram', total: string) {
  await openSupply(page, code)
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

async function addMaterial(page: Page, code: string, quantity: string) {
  await page.getByRole('button', { name: 'Adicionar material' }).click()
  const line = page.getByRole('group', { name: 'Material 1' })
  const optionValue = await line.getByLabel('Insumo').locator('option').filter({ hasText: code }).getAttribute('value')
  expect(optionValue, `the active costing picker must contain ${code}`).not.toBeNull()
  await line.getByLabel('Insumo').selectOption(optionValue!)
  await line.getByLabel('Quantidade').fill(quantity)
  return line
}

test('S4 A: real purchase receipts produce the weighted acquisition basis in the laboratory', async ({ page }) => {
  test.setTimeout(90_000)
  const code = uniqueCode('S4-BASIS')
  await createSupply(page, code, `Material ${code}`)
  await purchase(page, code, '1000', 'Gram', '100')
  await purchase(page, code, '500', 'Gram', '75')

  await page.goto('/cost-laboratory')
  await expect(page.getByRole('heading', { name: 'Laboratório de Custos' })).toBeVisible()
  const line = await addMaterial(page, code, '100')
  await expect(line.locator('.basis-summary')).toContainText(/Custo médio: R\$\s+0,116667 \/ g/)
  await expect(line.getByText(/WEIGHTED_AVERAGE_ACQUISITION/)).toBeVisible()

  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/costing/calculate'),
    page.getByRole('button', { name: 'Calcular custo estimado' }).click(),
  ])
  expect(response.status()).toBe(200)
  const result = page.locator('section.cost-result')
  await expect(result.getByRole('heading', { name: `${code} · Material ${code}` })).toBeVisible()
  await expect(result.getByText(/média ponderada das aquisições/)).toBeVisible()
  await expect(result.getByRole('article')).toContainText('R$ 11,67 + R$ 0,00 de perda')
})

test('S4 B: material, waste, labor, machine, additional cost and batch output are explained', async ({ page }) => {
  test.setTimeout(90_000)
  const code = uniqueCode('S4-MULTI')
  await createSupply(page, code, `Composto ${code}`)
  await purchase(page, code, '100', 'Gram', '10')

  await page.goto('/cost-laboratory')
  const line = await addMaterial(page, code, '100')
  await line.getByLabel('Perda desta linha (%)').fill('5')
  const labor = page.getByRole('region', { name: 'Mão de obra' })
  await labor.getByLabel('Minutos').fill('30')
  await labor.getByLabel('Taxa manual por hora (R$)').fill('40')
  const machine = page.getByRole('region', { name: 'Máquina' })
  await machine.getByLabel('Minutos').fill('120')
  await machine.getByLabel('Taxa por hora (R$)').fill('3')
  await page.getByRole('button', { name: 'Adicionar custo' }).click()
  const additional = page.getByRole('region', { name: 'Custos adicionais' })
  await additional.getByLabel('Descrição 1').fill('Acabamento')
  await additional.getByLabel('Valor (R$)').fill('13.5')
  await page.getByLabel('Quantidade produzida').fill('10')

  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/costing/calculate'),
    page.getByRole('button', { name: 'Calcular custo estimado' }).click(),
  ])
  expect(response.status()).toBe(200)
  const result = page.locator('section.cost-result')
  await expect(result.getByText('R$ 5,00 por unidade')).toBeVisible()
  await expect(totalValue(result, 'Total estimado do lote')).toHaveText('R$ 50,00')
  await expect(totalValue(result, 'Custo da perda')).toHaveText('R$ 0,50')
  await expect(totalValue(result, 'Mão de obra')).toHaveText('R$ 20,00')
  await expect(totalValue(result, 'Máquina')).toHaveText('R$ 6,00')
  await expect(totalValue(result, 'Custos adicionais')).toHaveText('R$ 13,50')
})

test('S4 C: a supply without acquisition cost can be simulated with a manual override', async ({ page }) => {
  test.setTimeout(90_000)
  const code = uniqueCode('S4-MANUAL')
  await createSupply(page, code, `Sem base ${code}`)

  await page.goto('/cost-laboratory')
  const line = await addMaterial(page, code, '2')
  await expect(line.getByText(/Sem base de aquisição/)).toBeVisible()
  await line.getByLabel('Custo manual por unidade-base (R$)').fill('3.5')
  await expect(line.getByText(/não altera o cadastro nem o histórico/)).toBeVisible()

  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/costing/calculate'),
    page.getByRole('button', { name: 'Calcular custo estimado' }).click(),
  ])
  expect(response.status()).toBe(200)
  const result = page.locator('section.cost-result')
  await expect(result.getByText(/override manual de simulação/)).toBeVisible()
  await expect(result.locator('.unit-cost')).toHaveText('R$ 7,00 por unidade')
})
