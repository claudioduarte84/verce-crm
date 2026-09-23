import { expect, test } from '@playwright/test'

function uniqueDescription() {
  return `S8A-VENDA ${Date.now().toString(36).toUpperCase()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
}

test('S8A: approval does not create a sale; explicit conversion creates and cancellation preserves its history', async ({ page }) => {
  test.setTimeout(60_000)
  const description = uniqueDescription()
  await page.goto('/quotes/new')
  await page.getByLabel('Canal de vendas').selectOption({ label: 'Venda Direta' })
  await page.getByLabel('Item avulso').check()
  await page.getByPlaceholder('Item avulso').fill(description)
  await page.getByLabel(/Custo manual por unidade/).fill('20')
  await page.getByLabel('Quantidade').fill('1')
  await page.getByLabel(/Margem desejada/).fill('40')
  const [created] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === '/api/quotes' && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Criar orçamento' }).click(),
  ])
  const quote = await created.json() as { currentRevision: { id: string } }
  await Promise.all([
    page.waitForResponse(response => /\/send$/.test(new URL(response.url()).pathname)),
    page.getByRole('button', { name: 'Enviar' }).click(),
  ])
  await Promise.all([
    page.waitForResponse(response => /\/approve$/.test(new URL(response.url()).pathname)),
    page.getByRole('button', { name: 'Aprovar' }).click(),
  ])
  await expect(page.getByText('Aprovado', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Registrar venda' })).toBeVisible()
  const beforeConversion = await page.request.get('/api/sales?page=1&pageSize=100')
  expect(beforeConversion.status()).toBe(200)
  const salesBefore = await beforeConversion.json() as { items: { quoteRevisionId: string | null }[] }
  expect(salesBefore.items.filter(sale => sale.quoteRevisionId === quote.currentRevision.id)).toHaveLength(0)

  const [conversion] = await Promise.all([
    page.waitForResponse(response => /\/api\/sales\/from-quote\//.test(new URL(response.url()).pathname)),
    page.getByRole('button', { name: 'Registrar venda' }).click(),
  ])
  expect(conversion.status()).toBe(200)
  const salesAfterResponse = await page.request.get('/api/sales?page=1&pageSize=100')
  const salesAfter = await salesAfterResponse.json() as { items: { quoteRevisionId: string | null }[] }
  expect(salesAfter.items.filter(sale => sale.quoteRevisionId === quote.currentRevision.id)).toHaveLength(1)
  await expect(page.getByRole('heading', { name: /Venda \d{6}-\d+/ })).toBeVisible()
  await expect(page.getByText(description)).toBeVisible()
  page.once('dialog', dialog => { void dialog.accept('Cliente desistiu da compra') })
  await page.getByRole('button', { name: 'Cancelar venda' }).click()
  const cancellation = page.getByRole('listitem').filter({ hasText: 'CANCELED' }).filter({ hasText: 'Cliente desistiu da compra' })
  await expect(cancellation).toBeVisible()
})
