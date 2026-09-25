import { defineConfig, devices } from '@playwright/test'
import { rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { acquireE2eRunLock, dropE2eDatabaseIfExists, provisionE2eDatabase, releaseE2eRunLock, resolveE2ePostgresTarget, resolveE2eRunId } from './e2e-env.cjs'

/**
 * S8C.1 third-round mandate: a SEPARATE Playwright configuration, isolated from playwright.config.ts,
 * that points the browser at tests/Verce.Marketplaces.E2EHost instead of the real, unmodified
 * `dotnet run --project src/Verce.Api` every other spec in this directory uses.
 *
 * ADR-0024 G-06 requires the fake marketplace connector to live in a test-support/harness project
 * only and never reach Verce.Api's own composition — MarketplaceProductionIsolationTests (an
 * in-process xunit test) already certifies that Verce.Api's project dependency graph has no path
 * to the fake. This config is what lets that SAME isolation guarantee be exercised end-to-end, with
 * a real Chromium browser over real HTTP, against the one place the fake is legitimately wired in:
 * a completely separate, test-owned host process, never Verce.Api's own artifact.
 *
 * Reuses e2e-env.cjs's own hardened PostgreSQL-safety contract verbatim (VERCE_E2E_POSTGRES_CONTAINER
 * required, verce-postgres/verce protected, run-lock discipline) — this config is not a parallel,
 * independently-maintained safety mechanism, it is the SAME one every other config in this
 * directory already uses, pointed at a different application process.
 */

const e2eRunId = resolveE2eRunId()
const e2eTarget = resolveE2ePostgresTarget()
const e2eConnectionString = e2eTarget.connectionString
const e2eRunLock = acquireE2eRunLock()
let e2eRunLockReleased = false
function releaseHarnessLock() {
  if (e2eRunLockReleased) return
  e2eRunLockReleased = true
  releaseE2eRunLock(e2eRunLock)
}
try {
  provisionE2eDatabase(e2eTarget)
} catch (error) {
  try { dropE2eDatabaseIfExists(e2eTarget.database, e2eTarget) } catch { /* preserve the original provisioning failure */ }
  releaseHarnessLock()
  throw error
}
process.once('exit', releaseHarnessLock)
process.env.ConnectionStrings__Verce = e2eConnectionString

// The store-loss journey performs harness-side filesystem fault injection (deleting confirmed
// credential envelopes to simulate total local-store loss) directly from Node — this path is
// computed HERE, handed to the E2E host via env var, and read back by the spec file itself, so
// both sides agree on it without the host needing to expose it over HTTP. Deliberately under the
// OS temp directory, never under the repository/content root (mirrors MarketplaceTestHarness.cs's
// and LocalProtectedCredentialStore's own rejection rule).
const marketplaceCredentialStoreRoot = join(tmpdir(), `verce-e2e-marketplace-store-${e2eRunId}`)
process.env.VERCE_E2E_MARKETPLACE_CREDENTIAL_STORE_ROOT = marketplaceCredentialStoreRoot
process.once('exit', () => { try { rmSync(marketplaceCredentialStoreRoot, { recursive: true, force: true }) } catch { /* best effort */ } })

// M-S8C1-003 (Codex Sol regate): same fix as playwright.config.ts — `dotnet run` is a launcher
// process, not the real server, so Playwright's webServer teardown could lose track of the
// actual process holding the port. Invoking the compiled DLL directly means the process spawned
// IS the process killed. NOTE: this config module is evaluated again by every worker process, so
// (as playwright.config.ts's own comment explains in detail) only the webServer command itself
// was changed here, never a one-shot build step placed at this top level.
// No --contentRoot argument here (unlike playwright.config.ts's own fix for the same class of
// bug): VerceWebApplicationFactory's real-Kestrel mode already calls builder.UseContentRoot
// (AppContext.BaseDirectory) itself, applied AFTER command-line parsing, so it would win
// regardless. This host also never depends on appsettings.Development.json's own
// Settings:SeedOnStartup value in the first place — Program.cs sets that key explicitly in
// `extraConfiguration`, immune to this whole class of bug by construction.
const repoRoot = resolve(__dirname, '..', '..')
const e2eHostDll = resolve(repoRoot, 'tests', 'Verce.Marketplaces.E2EHost', 'bin', 'Release', 'net10.0', 'Verce.Marketplaces.E2EHost.dll')

export default defineConfig({
  testDir: './specs',
  testMatch: /s8c1-marketplace\.spec\.ts/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['json', { outputFile: 'test-results/marketplace-results.json' }]],
  globalSetup: './global-setup.cjs',
  globalTeardown: './global-teardown.cjs',
  use: {
    baseURL: 'https://localhost:4173',
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: { ...devices['Desktop Chrome'] } },
    { name: 'marketplace', dependencies: ['setup'], testMatch: /s8c1-marketplace\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: resolve(__dirname, '.playwright', 'auth.json') } },
  ],
  webServer: [
    {
      // The ONLY line in this entire config that differs in kind from playwright.config.ts's own
      // webServer entry: the DLL is tests/Verce.Marketplaces.E2EHost's, never src/Verce.Api's.
      // Same port, same URL health check, same real Kestrel/real HTTPS/dev-cert behavior as the
      // main config — Verce.Api's own compiled artifact is never started by this file.
      command: `dotnet "${e2eHostDll}" --urls https://localhost:7246`,
      cwd: '../..',
      url: 'https://localhost:7246/health/ready',
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 120_000,
      env: {
        // Defensive/consistent with playwright.config.ts's own fix, even though
        // VerceWebApplicationFactory.ConfigureWebHost always calls UseEnvironment("Development")
        // itself regardless of the ambient value.
        ASPNETCORE_ENVIRONMENT: 'Development',
        RateLimiting__Auth__PermitLimit: '100000',
        ConnectionStrings__Verce: e2eConnectionString,
        VERCE_E2E_RUN_ID: e2eRunId,
        VERCE_E2E_MARKETPLACE_CREDENTIAL_STORE_ROOT: marketplaceCredentialStoreRoot,
      },
    },
    { command: 'npm run build && npm run preview -- --port 4173 --strictPort', cwd: '../../frontend', url: 'https://localhost:4173', ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 120_000 },
  ],
})
