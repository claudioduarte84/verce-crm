import { test, expect } from '@playwright/test'

test.describe('S1 smoke', () => {
  test.beforeEach(async ({ page }) => {
    // Every session check also primes the antiforgery cookie (SessionProvider.refresh) — mock
    // it uniformly so the console stays clean; its content is never asserted on here.
    await page.route('**/api/auth/csrf', (route) => route.fulfill({ status: 204 }))
  })

  test('the frontend loads', async ({ page }) => {
    await page.route('**/api/auth/session', (route) => route.fulfill({ status: 401 }))

    await page.goto('/')

    await expect(page).toHaveTitle('VERCE 3D | Laboratório de Custos')
  })

  test('an unauthenticated user reaches the login page', async ({ page }) => {
    await page.route('**/api/auth/session', (route) => route.fulfill({ status: 401 }))

    await page.goto('/')

    await expect(page.getByRole('heading', { name: 'Entrar' })).toBeVisible()
    await expect(page).toHaveURL(/\/login$/)
  })

  test('the protected shell is never shown while anonymous', async ({ page }) => {
    await page.route('**/api/auth/session', (route) => route.fulfill({ status: 401 }))

    await page.goto('/')

    await expect(page.getByText('Bem-vindo(a)')).toHaveCount(0)
  })

  test('an unknown route renders the not-found page', async ({ page }) => {
    await page.route('**/api/auth/session', (route) => route.fulfill({ status: 401 }))

    await page.goto('/this-route-does-not-exist')

    await expect(page.getByText('Página não encontrada.')).toBeVisible()
  })

  test('a reachable session endpoint renders the authenticated shell', async ({ page }) => {
    await page.route('**/api/auth/session', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ id: 'u1', email: 'owner@example.com', displayName: 'Ana Owner', roles: ['Owner'] }),
      }),
    )

    await page.goto('/')

    await expect(page.getByText('Ana Owner')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Sair' })).toBeVisible()
  })

  test('a genuine backend outage shows an outage message, not a silent redirect to login', async ({ page }) => {
    await page.route('**/api/auth/session', (route) => route.abort('connectionrefused'))

    await page.goto('/')

    await expect(page.getByRole('alert')).toContainText('Não foi possível conectar ao servidor')
  })
})
