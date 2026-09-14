import { test, expect } from '@playwright/test'

test('an authenticated Operator cannot administer owner-only S2 company settings', async ({ page }) => {
  const [profile] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/settings/company-profile'),
    page.goto('/settings'),
  ])
  expect(profile.status(), 'Company Profile is Owner-only').toBe(403)
  await expect(page.getByRole('alert')).toHaveText('Você não tem permissão para realizar esta ação.')
  await expect(page.getByRole('heading', { name: 'Dados da empresa' })).toHaveCount(0)
})
