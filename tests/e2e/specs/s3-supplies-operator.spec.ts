import { test, expect, type Page } from '@playwright/test'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

function uniqueCode(prefix: string) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}`
}

function formFor(page: Page, headingName: string) {
  return page.locator('form').filter({ has: page.getByRole('heading', { name: headingName, exact: true }) })
}

// The disposable E2E database is a fixed, PERSISTED name reused across separate `npm test`
// invocations (never recreated per run — see e2e-env.cjs), so a stable code would conflict on a
// second run. The code is generated fresh here and handed to s3-supplies-viewer.spec.ts (which
// runs later in the same invocation, per playwright.config.ts's project order) through a small
// file, so both specs agree on one supply without either hard-coding it.
const fixtureCode = uniqueCode('E2E-VIEWER-FIXTURE')
const fixtureName = 'Embalagem Fixture E2E'
const fixtureCodeFile = join(__dirname, '..', '.playwright', 'supplies-viewer-fixture-code.txt')

test('an Operator can register a supply and record its inventory, per SuppliesManage/InventoryManage', async ({ page }) => {
  test.setTimeout(60_000)
  await page.goto('/supplies')
  await expect(page.getByRole('heading', { name: 'Suprimentos e estoque' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Novo insumo' })).toBeVisible()

  const createForm = formFor(page, 'Novo insumo')
  await createForm.getByLabel('Código').fill(fixtureCode)
  await createForm.getByLabel('Nome').fill(fixtureName)
  await createForm.getByLabel('Categoria').selectOption('PACKAGING')
  await createForm.getByLabel('Unidade base').selectOption('Unit')

  const [createResponse] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/supplies' && response.request().method() === 'POST'),
    createForm.getByRole('button', { name: 'Salvar insumo' }).click(),
  ])
  expect(createResponse.status(), 'Operator must be allowed to create a supply').toBe(201)
  await expect(page.getByRole('status')).toHaveText('Insumo criado com sucesso.')
  mkdirSync(join(__dirname, '..', '.playwright'), { recursive: true })
  writeFileSync(fixtureCodeFile, fixtureCode, 'utf8')

  await page.getByLabel('Buscar insumos').fill(fixtureCode)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: new RegExp(fixtureCode) }).click()
  await expect(page.getByRole('heading', { name: `Estoque de ${fixtureName}` })).toBeVisible()

  const initialForm = formFor(page, 'Saldo inicial')
  await initialForm.getByLabel('Quantidade').fill('25')
  const [initialResponse] = await Promise.all([
    page.waitForResponse((r) => r.url().includes('/inventory/initial-balance') && r.request().method() === 'POST'),
    initialForm.getByRole('button', { name: 'Registrar saldo inicial' }).click(),
  ])
  expect(initialResponse.status(), 'Operator must be allowed to record inventory').toBe(200)
  await expect(page.getByRole('status')).toHaveText('Saldo inicial registrado.')

  const stockCard = page.locator('div.card').filter({ has: page.getByRole('heading', { name: `Estoque de ${fixtureName}`, exact: true }) })
  await expect(stockCard.getByText('Saldo atual: 25 un')).toBeVisible()
})
