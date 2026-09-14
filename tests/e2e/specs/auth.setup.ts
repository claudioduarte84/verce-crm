import { test as setup, expect } from '@playwright/test'
import { readFileSync, unlinkSync } from 'node:fs'
import { join } from 'node:path'
import { resolveOwnerEmail, resolveOwnerName } from '../e2e-env.cjs'

setup('creates and authenticates the disposable E2E Owner through the real UI', async ({ page }) => {
  const ownerEmail = resolveOwnerEmail()
  const ownerName = resolveOwnerName()
  const tokenFile = join(__dirname, '..', '.playwright-bootstrap-token')
  const token = readFileSync(tokenFile, 'utf8').trim()
  await page.goto(`/setup-account?token=${encodeURIComponent(token)}`)
  await page.getByLabel('Nova senha').fill('e2e-synthetic-password-123')
  await page.getByLabel('Confirmar senha').fill('e2e-synthetic-password-123')
  await page.getByRole('button', { name: 'Salvar e continuar' }).click()
  await page.getByRole('button', { name: 'Ir para o login' }).click()
  await expect(page.getByRole('heading', { name: 'Entrar' })).toBeVisible()
  await page.getByLabel('E-mail').fill(ownerEmail)
  await page.getByLabel('Senha').fill('e2e-synthetic-password-123')
  await page.getByRole('button', { name: 'Entrar' }).click()
  await expect(page.getByText(ownerName)).toBeVisible()
  await page.context().storageState({ path: join(__dirname, '..', '.playwright', 'auth.json') })
  unlinkSync(tokenFile)
})
