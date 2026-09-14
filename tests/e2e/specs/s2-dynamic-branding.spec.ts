import { test, expect } from '@playwright/test'

// Deliberately NOT the shared 1×1 tinyPng other specs use for upload-mechanics-only tests: this
// file's own "visible, not a 1×1 placeholder" assertion would then fail on a second invocation,
// once this very upload persists as the current version (a real 4×4 solid-color PNG, not a
// hand-typed literal, to keep the failure mode obvious rather than another opaque base64 blob).
const smallVisiblePng = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGM45MgDRwzEcQDEAxDxkT0TmwAAAABJRU5ErkJggg==',
  'base64',
)

test('the authenticated shell shows real, visible VERCE branding resolved from the server, never a blank/placeholder shell', async ({ page }) => {
  // Order-independent by design (mission §73): asserts consistency between the live
  // /api/settings/branding response and what the shell renders, whatever an earlier spec in this
  // run may have already renamed the product to — never a hard-coded "VERCE 3D" expectation.
  const branding = await (await page.request.get('/api/settings/branding')).json() as { productName: string }
  await page.goto('/')
  await expect(page.getByText(branding.productName, { exact: false }).first()).toBeVisible()
  const logo = page.locator('.brand-lockup__logo')
  await expect(logo).toBeVisible()
  const naturalWidth = await logo.evaluate((img: HTMLImageElement) => img.naturalWidth)
  expect(naturalWidth, 'the default logo must be a real image, not a 1x1 placeholder').toBeGreaterThan(1)
})

test('changing product name and subtitle updates the authenticated shell and document title after reload', async ({ page }) => {
  test.setTimeout(60_000)
  const suffix = Date.now().toString(36)
  const productName = `VERCE Dinâmico ${suffix}`
  const productSubtitle = `Subtítulo Dinâmico ${suffix}`

  // Isolation (mission §73): this mutates a global setting other specs may read, so the
  // original values are captured up front and restored at the end regardless of file run order.
  const before = await (await page.request.get('/api/settings')).json() as { key: string; value: string; version: number }[]
  const originalName = before.find((s) => s.key === 'branding.product_name')!
  const originalSubtitle = before.find((s) => s.key === 'branding.product_subtitle')!
  let csrf = ''

  try {
    await page.goto('/settings')
    csrf = decodeURIComponent((await page.evaluate(() => document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1])) ?? '')
    page.once('dialog', (dialog) => dialog.accept(productName))
    await page.getByRole('listitem').filter({ hasText: 'branding.product_name' }).getByRole('button', { name: 'Editar' }).click()
    await expect(page.getByRole('status')).toHaveText('Configuração salva.')

    page.once('dialog', (dialog) => dialog.accept(productSubtitle))
    await page.getByRole('listitem').filter({ hasText: 'branding.product_subtitle' }).getByRole('button', { name: 'Editar' }).click()
    await expect(page.getByRole('status')).toHaveText('Configuração salva.')

    const [brandingResponse] = await Promise.all([
      page.waitForResponse((response) => new URL(response.url()).pathname === '/api/settings/branding'),
      page.goto('/'),
    ])
    expect(brandingResponse.status()).toBe(200)
    await expect(page.getByText(productName, { exact: false }).first()).toBeVisible()
    await expect.poll(() => page.title()).toBe(`${productName} | ${productSubtitle}`)
  } finally {
    const current = await (await page.request.get('/api/settings')).json() as { key: string; value: string; version: number }[]
    const nameNow = current.find((s) => s.key === 'branding.product_name')!
    const subtitleNow = current.find((s) => s.key === 'branding.product_subtitle')!
    await page.request.put(`/api/settings/${encodeURIComponent(originalName.key)}`, { headers: { 'X-XSRF-TOKEN': csrf }, data: { value: originalName.value, version: nameNow.version } })
    await page.request.put(`/api/settings/${encodeURIComponent(originalSubtitle.key)}`, { headers: { 'X-XSRF-TOKEN': csrf }, data: { value: originalSubtitle.value, version: subtitleNow.version } })
  }
})

test('uploading a new version onto the active application logo asset updates the shell after a branding refresh', async ({ page }) => {
  // HomePage renders the COMPACT logo in its header lockup — that is the role this test must
  // change to observe a visible difference (SYSTEM_LOGO itself is not currently rendered there).
  await page.goto('/settings')
  const beforeBranding = await (await page.request.get('/api/settings/branding')).json() as { logos: { role: string; url: string }[] }
  const systemLogoUrlBefore = beforeBranding.logos.find((item) => item.role === 'SYSTEM_LOGO_COMPACT')?.url
  expect(systemLogoUrlBefore, 'a fresh install must already assign SYSTEM_LOGO_COMPACT to a real asset').toBeTruthy()
  const currentVersionId = systemLogoUrlBefore!.split('/').at(-2)

  const assets = await (await page.request.get('/api/settings/brand-assets')).json() as { id: string; name: string; versions: { id: string }[] }[]
  const systemLogoAsset = assets.find((asset) => asset.versions.some((version) => version.id === currentVersionId))!
  const assetCard = page.getByRole('listitem').filter({ hasText: systemLogoAsset.name })

  await Promise.all([
    page.waitForResponse((response) => /\/api\/settings\/brand-assets\/.+\/versions$/.test(new URL(response.url()).pathname) && response.request().method() === 'POST'),
    assetCard.getByLabel('Enviar nova versão').setInputFiles({ name: 'refreshed-logo.png', mimeType: 'image/png', buffer: smallVisiblePng }),
  ])
  await expect(page.getByRole('status')).toHaveText('Nova versão enviada com sucesso.')

  const [brandingResponse] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/settings/branding'),
    page.goto('/'),
  ])
  const afterBranding = await brandingResponse.json() as { logos: { role: string; url: string }[] }
  const systemLogoUrlAfter = afterBranding.logos.find((item) => item.role === 'SYSTEM_LOGO_COMPACT')?.url
  expect(systemLogoUrlAfter).not.toBe(systemLogoUrlBefore)
  await expect(page.locator('.brand-lockup__logo')).toHaveAttribute('src', systemLogoUrlAfter!)
})
