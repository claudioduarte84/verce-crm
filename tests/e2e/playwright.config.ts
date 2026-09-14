import { defineConfig, devices } from '@playwright/test'
import { resolve } from 'node:path'
import { acquireE2eRunLock, provisionE2eDatabase, releaseE2eRunLock, resolveE2eConnectionString } from './e2e-env.cjs'

/** S1 smoke plus real S2 authenticated browser certification. The setup project provisions and
 * signs in a disposable owner through the live API; dependent projects load that saved browser
 * storage state. Legacy S1 smoke cases retain their intentionally isolated network stubs. */

// Canonical E2E database (M-S2-002): every process the suite spawns — this webServer, the
// restart-persistence spec's own API processes, global-setup.cjs and operator.setup.ts — must
// resolve the SAME disposable database from e2e-env.cjs, never the shared "verce" dev database.
const e2eConnectionString = resolveE2eConnectionString()
// The run lock must be held before ANY mutable step: Playwright does not guarantee globalSetup
// finishes before webServer starts, so this config module — evaluated before any process is
// spawned — acquires it, and only then provisions/migrates the disposable database. Ownership is
// an exclusive loopback bind held by run-lock-holder.cjs (see e2e-env.cjs), so a second
// Playwright invocation genuinely waits instead of sharing this database, ports and storage
// state, and ownership ends automatically if this process dies.
const e2eRunLock = acquireE2eRunLock()
let e2eRunLockReleased = false
function releaseHarnessLock() {
  if (e2eRunLockReleased) return
  e2eRunLockReleased = true
  releaseE2eRunLock(e2eRunLock)
}
try {
  provisionE2eDatabase(e2eConnectionString)
} catch (error) {
  releaseHarnessLock()
  throw error
}
process.once('exit', releaseHarnessLock)
process.env.ConnectionStrings__Verce = e2eConnectionString

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  // The production auth limit is deliberately per client IP. Serial browser certification keeps
  // independent journeys from manufacturing one shared-IP burst while still exercising it live.
  workers: 1,
  retries: 0,
  reporter: [['list'], ['json', { outputFile: 'test-results/results.json' }]],
  globalSetup: './global-setup.cjs',
  globalTeardown: './global-teardown.cjs',
  use: {
    baseURL: 'https://localhost:4173',
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: { ...devices['Desktop Chrome'] } },
    { name: 'operator-setup', dependencies: ['setup'], testMatch: /operator\.setup\.ts/, use: { ...devices['Desktop Chrome'] } },
    { name: 'authorization', dependencies: ['operator-setup'], testMatch: /s2-authorization\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: resolve(__dirname, '.playwright', 'operator.json') } },
    { name: 'chromium', dependencies: ['operator-setup'], testIgnore: /auth\.setup\.ts|operator\.setup\.ts|s2-authorization\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: resolve(__dirname, '.playwright', 'auth.json') } },
  ],
  webServer: [
    {
      command: 'dotnet run --no-build --project src/Verce.Api --urls https://localhost:7246',
      cwd: '../..',
      url: 'https://localhost:7246/health/ready',
      ignoreHTTPSErrors: true,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      // SECURITY §3.2's 10-req/min-per-IP limit on /api/auth/* is real production behavior and
      // stays that way by default (Program.cs) — every Playwright test shares one "client IP"
      // (localhost) and each page load re-checks the session, so a full E2E run legitimately
      // needs far more than 10 such calls/minute. Raised ONLY for this test-managed host, never
      // in the production composition root.
      env: { RateLimiting__Auth__PermitLimit: '100000', ConnectionStrings__Verce: e2eConnectionString },
    },
    // Production preview, not `vite dev`: a long sequential E2E run hits a real Vite dev-server
    // state issue (its per-module transform/HMR pipeline can wedge after enough real round-trips
    // through the proxy — the ASP.NET Core host itself stays healthy throughout, confirmed by
    // direct requests during the hang). `preview` serves the already-built static bundle, which
    // sidesteps that whole code path and is also more representative of what is actually deployed.
    { command: 'npm run build && npm run preview -- --port 4173 --strictPort', cwd: '../../frontend', url: 'https://localhost:4173', ignoreHTTPSErrors: true, reuseExistingServer: !process.env.CI, timeout: 120_000 },
  ],
})
