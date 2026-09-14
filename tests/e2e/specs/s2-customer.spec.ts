import { test, expect } from '@playwright/test'

function syntheticCpf() {
  const digits = `${Date.now()}`.slice(-9).split('').map(Number)
  const check = (values: number[], weight: number) => { const sum = values.reduce((total, value, index) => total + value * (weight - index), 0); const remainder = sum % 11; return remainder < 2 ? 0 : 11 - remainder }
  return [...digits, check(digits, 10), check([...digits, check(digits, 10)], 11)].join('')
}

test('S2 customer journey persists create, edit and default delivery address through reload', async ({ page }) => {
  const suffix = Date.now().toString(36)
  const name = `Synthetic Customer ${suffix}`
  const editedName = `${name} Edited`
  const cookies = await page.context().cookies()
  expect(cookies.some((cookie) => cookie.name === '__Host-verce.auth' && cookie.secure && cookie.httpOnly)).toBeTruthy()
  const [spaSession] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/auth/session'),
    page.goto('/customers'),
  ])
  expect(spaSession.status(), 'the SPA session refresh must be accepted').toBe(200)
  await expect(page.getByRole('heading', { name: 'Clientes' })).toBeVisible()
  await page.getByLabel('Nome').fill(name)
  await page.getByLabel('CPF ou CNPJ').fill(syntheticCpf())
  await page.getByLabel('E-mail').fill(`synthetic-${suffix}@example.test`)
  const [createResponse] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/customers' && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Salvar cliente' }).click(),
  ])
  expect(createResponse.status(), 'customer creation must succeed through the live API').toBe(201)
  await expect(page.getByRole('status')).toHaveText('Cliente criado com sucesso.')
  await page.getByLabel('Buscar clientes').fill(name)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name }).click()
  await expect(page.getByRole('heading', { name: 'Editar cliente' })).toBeVisible()
  await page.getByLabel('Nome').fill(editedName)
  await page.getByRole('button', { name: 'Salvar alterações' }).click()
  await expect(page.getByRole('status')).toHaveText('Cliente atualizado com sucesso.')
  await page.getByLabel('Identificação').fill('Synthetic delivery')
  await page.getByLabel('CEP').fill('01001-000')
  await page.getByLabel('Rua').fill('Rua Synthetic')
  await page.getByLabel('Número').fill('1')
  await page.getByLabel('Bairro').fill('Centro')
  await page.getByLabel('Cidade').fill('São Paulo')
  await page.getByRole('button', { name: 'Salvar endereço' }).click()
  await expect(page.getByText('Entrega padrão')).toBeVisible()
  await page.reload()
  await page.getByLabel('Buscar clientes').fill(editedName)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name: editedName }).click()
  await expect(page.getByText('Synthetic delivery')).toBeVisible()
  await expect(page.getByText('Entrega padrão')).toBeVisible()
})

test('a second primary/default address clears the previous one, edits persist, and deletion removes it', async ({ page }) => {
  // More real round-trips (create, 2 addresses, edit, delete, reload, re-verify) than the
  // default 30s test budget reliably covers alongside the rest of a full sequential run.
  test.setTimeout(60_000)
  const suffix = Date.now().toString(36)
  const name = `Synthetic Flags ${suffix}`
  await page.goto('/customers')
  await page.getByLabel('Nome').fill(name)
  await page.getByLabel('CPF ou CNPJ').fill(syntheticCpf())
  await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/customers' && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Salvar cliente' }).click(),
  ])
  await page.getByLabel('Buscar clientes').fill(name)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name }).click()

  await page.getByLabel('Identificação').fill('Endereço A')
  await page.getByLabel('CEP').fill('01001-000')
  await page.getByLabel('Rua').fill('Rua A')
  await page.getByLabel('Número').fill('1')
  await page.getByLabel('Bairro').fill('Centro')
  await page.getByLabel('Cidade').fill('São Paulo')
  await page.getByRole('button', { name: 'Salvar endereço' }).click()
  await expect(page.getByText('Endereço A')).toBeVisible()

  await page.getByLabel('Identificação').fill('Endereço B')
  await page.getByLabel('CEP').fill('20000-000')
  await page.getByLabel('Rua').fill('Rua B')
  await page.getByLabel('Número').fill('2')
  await page.getByLabel('Bairro').fill('Centro')
  await page.getByLabel('Cidade').fill('Rio de Janeiro')
  await page.getByRole('button', { name: 'Salvar endereço' }).click()

  const rowA = page.getByRole('listitem').filter({ hasText: 'Endereço A' })
  const rowB = page.getByRole('listitem').filter({ hasText: 'Endereço B' })
  await expect(rowB.getByText('Principal')).toBeVisible()
  await expect(rowB.getByText('Entrega padrão')).toBeVisible()
  await expect(rowA.getByText('Principal')).toHaveCount(0)
  await expect(rowA.getByText('Entrega padrão')).toHaveCount(0)

  await rowA.getByRole('button', { name: 'Editar' }).click()
  await expect(page.getByRole('heading', { name: 'Editar endereço' })).toBeVisible()
  await page.getByLabel('Rua').fill('Rua A Editada')
  await Promise.all([
    page.waitForResponse((response) => response.request().method() === 'PUT' && new URL(response.url()).pathname.includes('/addresses/')),
    page.getByRole('button', { name: 'Salvar edição' }).click(),
  ])
  await expect(page.getByText('Rua A Editada, 1')).toBeVisible()

  page.once('dialog', (dialog) => dialog.accept())
  await Promise.all([
    page.waitForResponse((response) => response.request().method() === 'DELETE' && new URL(response.url()).pathname.includes('/addresses/')),
    rowB.getByRole('button', { name: 'Remover' }).click(),
  ])
  await expect(page.getByText('Endereço B')).toHaveCount(0)
  await page.reload()
  await page.getByLabel('Buscar clientes').fill(name)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name }).click()
  await expect(page.getByText('Rua A Editada, 1')).toBeVisible()
  await expect(page.getByText('Endereço B')).toHaveCount(0)
})

test('deactivating a customer through the UI removes it from the default active list', async ({ page }) => {
  test.setTimeout(60_000)
  const suffix = Date.now().toString(36)
  const name = `Synthetic Deactivate ${suffix}`
  await page.goto('/customers')
  await page.getByLabel('Nome').fill(name)
  await page.getByLabel('CPF ou CNPJ').fill(syntheticCpf())
  await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/customers' && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Salvar cliente' }).click(),
  ])
  await page.getByLabel('Buscar clientes').fill(name)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await page.getByRole('button', { name }).click()

  page.once('dialog', (dialog) => dialog.accept())
  await Promise.all([
    page.waitForResponse((response) => response.request().method() === 'DELETE' && new URL(response.url()).pathname.startsWith('/api/customers/')),
    page.getByRole('button', { name: 'Desativar cliente' }).click(),
  ])
  await expect(page.getByRole('status')).toHaveText('Cliente desativado com sucesso.')

  await page.getByLabel('Buscar clientes').fill(name)
  await page.getByRole('button', { name: 'Buscar' }).click()
  // Deactivation is a real soft-delete (deleted_at set); there is no reachable "show inactive"
  // state — the customer is gone from search permanently, matching the backend contract.
  await expect(page.getByRole('button', { name })).toHaveCount(0)
})

test('pagination navigates across multiple pages of customers without duplicates', async ({ page }) => {
  test.setTimeout(60_000)
  const suffix = Date.now().toString(36)
  const namePrefix = `Synthetic Page ${suffix}`
  await page.goto('/customers')
  const csrf = await page.evaluate(() => document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1])
  for (let i = 0; i < 12; i++) {
    await page.request.post('/api/customers', {
      headers: { 'X-XSRF-TOKEN': decodeURIComponent(csrf ?? '') },
      data: { personType: 'Individual', name: `${namePrefix} ${i.toString().padStart(2, '0')}`, version: 0 },
    })
  }

  await page.getByLabel('Buscar clientes').fill(namePrefix)
  await page.getByRole('button', { name: 'Buscar' }).click()
  await expect(page.getByText(/Página 1 de 2/)).toBeVisible()
  const seenOnPageOne = await page.getByRole('listitem').allTextContents()
  await page.getByRole('button', { name: 'Próxima' }).click()
  await expect(page.getByText(/Página 2 de 2/)).toBeVisible()
  const seenOnPageTwo = await page.getByRole('listitem').allTextContents()
  expect(new Set([...seenOnPageOne, ...seenOnPageTwo]).size).toBe(seenOnPageOne.length + seenOnPageTwo.length)
  await page.getByRole('button', { name: 'Anterior' }).click()
  await expect(page.getByText(/Página 1 de 2/)).toBeVisible()
})
