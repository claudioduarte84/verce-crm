import { test as setup, expect } from '@playwright/test'
import { join } from 'node:path'
import { resolveE2eConnectionString, assertDisposableE2eDatabase, resolveOwnerEmail, runE2ePsql } from '../e2e-env.cjs'

const operatorEmail = 'e2e-operator@example.test'
const password = 'e2e-synthetic-password-123'

setup.setTimeout(120_000)

function provisionDisposableOperator() {
  const database = assertDisposableE2eDatabase(resolveE2eConnectionString())
  // Must be the SAME Owner identity global-setup.cjs / auth.setup.ts bootstrapped — never a
  // fixed literal — so a custom VERCE_E2E_OWNER_EMAIL is honored here too (§9/§10).
  const ownerNormalizedEmail = resolveOwnerEmail().toUpperCase()
  const sql = `
    WITH owner AS (SELECT password_hash FROM platform."user" WHERE normalized_email = '${ownerNormalizedEmail}')
    INSERT INTO platform."user" (id, display_name, is_active, setup_status, setup_completed_at, user_name, normalized_user_name, email, normalized_email, email_confirmed, password_hash, security_stamp, concurrency_stamp, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
    SELECT gen_random_uuid(), 'E2E Operator', true, 'Active', now(), '${operatorEmail}', '${operatorEmail.toUpperCase()}', '${operatorEmail}', '${operatorEmail.toUpperCase()}', true, password_hash, gen_random_uuid()::text, gen_random_uuid()::text, false, false, true, 0 FROM owner
    ON CONFLICT (normalized_user_name) DO UPDATE SET display_name = EXCLUDED.display_name, is_active = true, setup_status = 'Active', password_hash = EXCLUDED.password_hash, security_stamp = gen_random_uuid()::text, concurrency_stamp = gen_random_uuid()::text;
    DELETE FROM platform.user_role WHERE user_id = (SELECT id FROM platform."user" WHERE normalized_email = '${operatorEmail.toUpperCase()}');
    INSERT INTO platform.user_role (user_id, role_id)
    SELECT (SELECT id FROM platform."user" WHERE normalized_email = '${operatorEmail.toUpperCase()}'), (SELECT id FROM platform.role WHERE name = 'Operator');
  `
  runE2ePsql(database, ['-c', sql])
}

setup('provisions and authenticates a disposable Operator through the real UI', async ({ page }) => {
  // The production endpoint set deliberately has no user-administration surface in S2. This
  // isolated test-user fixture writes one real Operator record; no claims, cookie or API route
  // is fabricated. Wait for a fresh fixed window so the live auth limiter remains exercised.
  await page.waitForTimeout(61_000)
  provisionDisposableOperator()
  await page.goto('/login')
  await page.getByLabel('E-mail').fill(operatorEmail)
  await page.getByLabel('Senha').fill(password)
  await page.getByRole('button', { name: 'Entrar' }).click()
  await expect(page.getByText('E2E Operator')).toBeVisible()
  await page.context().storageState({ path: join(__dirname, '..', '.playwright', 'operator.json') })
})
