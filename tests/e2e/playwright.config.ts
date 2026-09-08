import { defineConfig, devices } from '@playwright/test'

/**
 * S1 E2E/smoke foundation (mission §24). Only the frontend dev server is started here — the
 * backend does not yet expose a working login/session endpoint (see the S1 FINAL REPORT open
 * issues), so these smoke tests intercept `/api/auth/session` at the browser network layer
 * instead of requiring a live ASP.NET Core + PostgreSQL stack. That keeps every assertion here
 * true today, without fabricating an authenticated business workflow that isn't safely
 * automatable yet (mission §24).
 */
export default defineConfig({
  testDir: './specs',
  fullyParallel: true,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:4173',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm run build && npm run preview -- --port 4173 --strictPort',
    cwd: '../../frontend',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
})
