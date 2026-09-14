import { test, expect } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const fixtureName = 'Embalagem Fixture E2E'

test('a Viewer can read supplies and inventory but cannot mutate them', async ({ page }) => {
  test.setTimeout(60_000)
  // Read at run time, not at module-load time: Playwright loads every project's spec files
  // during test collection, before any project's tests actually execute, so this file would not
  // exist yet if read at module scope — s3-supplies-operator.spec.ts (which runs first in this
  // project's dependency chain — see playwright.config.ts) writes it from its own test body.
  const fixtureCode = readFileSync(join(__dirname, '..', '.playwright', 'supplies-viewer-fixture-code.txt'), 'utf8').trim()
  await page.goto('/supplies')
  await expect(page.getByRole('heading', { name: 'Suprimentos e estoque' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Novo insumo' })).not.toBeVisible()

  await page.getByLabel('Buscar insumos').fill(fixtureCode)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: new RegExp(fixtureCode) }).click()
  await expect(page.getByRole('heading', { name: `Estoque de ${fixtureName}` })).toBeVisible()

  // Read access is intact: the stock and its movement history are visible.
  const stockCard = page.locator('div.card').filter({ has: page.getByRole('heading', { name: `Estoque de ${fixtureName}`, exact: true }) })
  await expect(stockCard.getByText('Saldo atual: 25 un')).toBeVisible()
  await expect(stockCard.getByText('InitialBalance')).toBeVisible()

  // Write access is not: no edit/deactivate controls and none of the inventory-action forms.
  await expect(page.getByRole('button', { name: 'Salvar alterações' })).not.toBeVisible()
  await expect(page.getByRole('button', { name: 'Desativar insumo' })).not.toBeVisible()
  await expect(page.getByRole('heading', { name: 'Saldo inicial' })).not.toBeVisible()
  await expect(page.getByRole('heading', { name: 'Registrar compra' })).not.toBeVisible()
  await expect(page.getByRole('heading', { name: 'Ajuste manual' })).not.toBeVisible()

  // Attempting the mutation directly against the API (bypassing the hidden UI) must still be
  // rejected server-side — the UI gate is a convenience, never the actual authorization boundary.
  const csrf = await page.evaluate(() => document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1])
  const response = await page.request.post('/api/supplies', {
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(csrf ?? '') },
    data: { code: 'E2E-VIEWER-SHOULD-FAIL', name: 'Should not be created', categoryCode: 'PACKAGING', baseUnit: 'Unit' },
  })
  expect(response.status(), 'InventoryManage/SuppliesManage must be enforced server-side, not just hidden in the UI').toBe(403)
})
