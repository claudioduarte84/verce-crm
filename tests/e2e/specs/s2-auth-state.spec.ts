import { test, expect } from '@playwright/test'
import { resolveOwnerName } from '../e2e-env.cjs'

test('saved real-owner state reaches an authenticated route without a login redirect', async ({ page }) => {
  const cookies = await page.context().cookies()
  expect(cookies.some((cookie) => cookie.name === '__Host-verce.auth' && cookie.secure && cookie.httpOnly)).toBeTruthy()

  const [session] = await Promise.all([
    page.waitForResponse((response) => new URL(response.url()).pathname === '/api/auth/session'),
    page.goto('/'),
  ])
  expect(session.status()).toBe(200)
  await expect(page.getByText(resolveOwnerName())).toBeVisible()
  await expect(page).not.toHaveURL(/\/login$/)
})
