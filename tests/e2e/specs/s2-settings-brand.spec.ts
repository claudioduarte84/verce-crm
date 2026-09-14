import { test, expect } from '@playwright/test'

const tinyPng = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
  'base64',
)

test('S2 settings journey persists profile, typed setting and a retrievable brand asset', async ({ page }) => {
  const suffix = Date.now().toString(36)
  const legalName = `VERCE E2E ${suffix}`
  const productName = `VERCE Product ${suffix}`
  const assetName = `Logo ${suffix}`

  const [session] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/auth/session'),
    page.goto('/settings'),
  ])
  expect(session.status(), 'the saved owner session must be accepted by the Settings SPA').toBe(200)
  await expect(page.getByRole('heading', { name: 'Configurações' })).toBeVisible()

  await page.getByLabel('Razão social').fill(legalName)
  await page.getByRole('button', { name: 'Salvar dados' }).click()
  await expect(page.getByRole('status')).toHaveText('Dados da empresa salvos.')

  page.once('dialog', (dialog) => dialog.accept(productName))
  await page.getByRole('listitem').filter({ hasText: 'branding.product_name' }).getByRole('button', { name: 'Editar' }).click()
  await expect(page.getByRole('status')).toHaveText('Configuração salva.')

  await page.getByLabel('Nome', { exact: true }).fill(assetName)
  await page.getByLabel('Versão inicial').setInputFiles({
    name: 'logo.png',
    mimeType: 'image/png',
    buffer: tinyPng,
  })
  await page.getByRole('button', { name: 'Criar asset' }).click()
  await expect(page.getByRole('status')).toHaveText('Asset de marca enviado e normalizado.')
  const preview = page.getByRole('img', { name: `Prévia de ${assetName}` })
  await expect(preview).toBeVisible()
  await expect(preview).toHaveAttribute('src', /\/api\/settings\/brand-assets\/versions\/.+\/content$/)
})

test('every normative Company Profile field persists through reload', async ({ page }) => {
  test.setTimeout(60_000)
  const suffix = Date.now().toString(36)
  await page.goto('/settings')
  await expect(page.getByRole('heading', { name: 'Configurações' })).toBeVisible()

  const fields: Record<string, string> = {
    'Razão social': `VERCE E2E Completo ${suffix}`,
    'Nome fantasia': `VERCE Fantasia ${suffix}`,
    CNPJ: '73.894.567/0001-22',
    'E-mail': `contato-${suffix}@example.test`,
    Telefone: '1140028922',
    WhatsApp: '11999998888',
    Site: 'https://verce3d.example',
    Instagram: '@verce3d',
    CEP: '01001-000',
    Rua: 'Rua Synthetic Profile',
    Número: '100',
    Complemento: 'Sala 1',
    Bairro: 'Centro',
    Cidade: 'São Paulo',
  }
  for (const [label, value] of Object.entries(fields)) {
    const field = page.getByLabel(label, { exact: true })
    await field.fill('')
    await field.fill(value)
  }
  const uf = page.getByLabel('UF', { exact: true })
  await uf.fill(''); await uf.fill('SP')
  const country = page.getByLabel('País', { exact: true })
  await country.fill(''); await country.fill('BR')
  const timezone = page.getByLabel('Fuso horário', { exact: true })
  await timezone.fill(''); await timezone.fill('America/Sao_Paulo')
  const currency = page.getByLabel('Moeda', { exact: true })
  await currency.fill(''); await currency.fill('BRL')

  const [saveResponse] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/settings/company-profile' && response.request().method() === 'PUT'),
    page.getByRole('button', { name: 'Salvar dados' }).click(),
  ])
  expect(saveResponse.status()).toBe(204)
  await expect(page.getByRole('status')).toHaveText('Dados da empresa salvos.')

  await page.reload()
  for (const [label, value] of Object.entries(fields)) {
    await expect(page.getByLabel(label, { exact: true })).toHaveValue(value)
  }
  await expect(uf).toHaveValue('SP')
})

test('an existing Brand Asset accepts a new version, exposes history, and a historical version can be reactivated', async ({ page }) => {
  test.setTimeout(60_000)
  const suffix = Date.now().toString(36)
  const assetName = `Logo histórico ${suffix}`
  await page.goto('/settings')
  await page.getByLabel('Nome', { exact: true }).fill(assetName)
  await page.getByLabel('Versão inicial').setInputFiles({ name: 'v1.png', mimeType: 'image/png', buffer: tinyPng })
  await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/settings/brand-assets' && response.request().method() === 'POST'),
    page.getByRole('button', { name: 'Criar asset' }).click(),
  ])
  await expect(page.getByRole('status')).toHaveText('Asset de marca enviado e normalizado.')

  const assetCard = page.getByRole('listitem').filter({ hasText: assetName })
  await expect(assetCard.getByText('v1 (atual)')).toBeVisible()

  await Promise.all([
    page.waitForResponse((response) => /\/api\/settings\/brand-assets\/.+\/versions$/.test(new URL(response.url()).pathname) && response.request().method() === 'POST'),
    assetCard.getByLabel('Enviar nova versão').setInputFiles({ name: 'v2.png', mimeType: 'image/png', buffer: tinyPng }),
  ])
  await expect(page.getByRole('status')).toHaveText('Nova versão enviada com sucesso.')
  await expect(assetCard.getByText('v1')).toBeVisible()
  await expect(assetCard.getByText('v2 (atual)')).toBeVisible()

  await Promise.all([
    page.waitForResponse((response) => /\/api\/settings\/brand-assets\/.+\/activate$/.test(new URL(response.url()).pathname)),
    assetCard.getByRole('listitem').filter({ hasText: 'v1' }).getByRole('button', { name: 'Ativar' }).click(),
  ])
  await expect(page.getByRole('status')).toHaveText('Versão ativada com sucesso.')
  await expect(assetCard.getByText('v1 (atual)')).toBeVisible()
  await expect(assetCard.getByText(/^v2$/)).toBeVisible()

  await Promise.all([
    page.waitForResponse((response) => /\/api\/settings\/brand-assets\/.+\/activate$/.test(new URL(response.url()).pathname)),
    assetCard.getByRole('listitem').filter({ hasText: 'v2' }).getByRole('button', { name: 'Ativar' }).click(),
  ])
  await expect(assetCard.getByText('v2 (atual)')).toBeVisible()
})
