import { defineConfig, devices } from '@playwright/test'
import { resolve } from 'node:path'
import { acquireE2eRunLock, dropE2eDatabaseIfExists, provisionE2eDatabase, releaseE2eRunLock, resolveE2ePostgresTarget, resolveE2eRunId } from './e2e-env.cjs'

/** S1 smoke plus real S2 authenticated browser certification. The setup project provisions and
 * signs in a disposable owner through the live API; dependent projects load that saved browser
 * storage state. Legacy S1 smoke cases retain their intentionally isolated network stubs. */

// Canonical E2E PostgreSQL target (M-S2-002, H-01): resolved ONCE, here, and threaded through by
// value — this webServer, the restart-persistence spec's own API processes, global-setup.cjs and
// operator.setup.ts all resolve their own copy from the SAME e2e-env.cjs, but every one of them
// is required (by construction — see e2e-env.cjs) to name the same disposable container and
// database, never the shared "verce" dev database or its container.
const e2eRunId = resolveE2eRunId()
const e2eTarget = resolveE2ePostgresTarget()
const e2eConnectionString = e2eTarget.connectionString
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
  provisionE2eDatabase(e2eTarget)
} catch (error) {
  try { dropE2eDatabaseIfExists(e2eTarget.database, e2eTarget) } catch { /* preserve the original provisioning failure */ }
  releaseHarnessLock()
  throw error
}
process.once('exit', releaseHarnessLock)
process.env.ConnectionStrings__Verce = e2eConnectionString

// M-S8C1-003 (Codex Sol regate): `dotnet run --project ...` is a launcher — it spawns a SEPARATE
// child process for the actual compiled app, so Playwright's own webServer teardown only ever
// held a handle to the launcher, not the process actually holding the port. This mirrors the
// pattern s2-restart-persistence.spec.ts already uses successfully for its own dedicated API
// process: invoke the compiled DLL directly, so the process Playwright spawns AND kills is the
// real server — no intermediate process for a tree-kill to miss.
// NOTE: this config module is evaluated AGAIN by every Playwright worker process (see the
// run-lock's own "re-entrancy, across processes" handling below), not once per overall run — an
// earlier version of this fix tried to run `npm run build` here directly and it silently
// re-ran the full frontend build before nearly every test, which is both slow and was observed
// to destabilize `vite preview` (rebuilding `dist/` while it was actively serving from it).
// webServer entries themselves do NOT have this problem — Playwright starts/owns each one
// exactly once for the whole run, tracked by the one process it actually spawns — which is why
// only the backend entry below was changed to invoke the compiled DLL directly; the frontend
// entry's own build+preview chain is left exactly as it was.
const repoRoot = resolve(__dirname, '..', '..')
const apiProjectDir = resolve(repoRoot, 'src', 'Verce.Api')
const apiDll = resolve(apiProjectDir, 'bin', 'Release', 'net10.0', 'Verce.Api.dll')

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
    { name: 'viewer-setup', dependencies: ['operator-setup'], testMatch: /viewer\.setup\.ts/, use: { ...devices['Desktop Chrome'] } },
    { name: 'authorization', dependencies: ['operator-setup'], testMatch: /s2-authorization\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: resolve(__dirname, '.playwright', 'operator.json') } },
    { name: 'supplies-operator', dependencies: ['viewer-setup'], testMatch: /s3-supplies-operator\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: resolve(__dirname, '.playwright', 'operator.json') } },
    { name: 'supplies-viewer', dependencies: ['viewer-setup'], testMatch: /s3-supplies-viewer\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: resolve(__dirname, '.playwright', 'viewer.json') } },
    // s8c1-marketplace.spec.ts is deliberately excluded here: it requires the fake marketplace
    // connector and the "Fake E2E Provider" catalog row, both wired ONLY by
    // tests/Verce.Marketplaces.E2EHost's own composition (ADR-0024 G-06) — never by the real,
    // unmodified Verce.Api this config's webServer starts. See playwright.marketplace.config.ts,
    // which targets that separate host instead.
    { name: 'chromium', dependencies: ['viewer-setup'], testIgnore: /auth\.setup\.ts|operator\.setup\.ts|viewer\.setup\.ts|s2-authorization\.spec\.ts|s3-supplies-operator\.spec\.ts|s3-supplies-viewer\.spec\.ts|s8c1-marketplace\.spec\.ts/, use: { ...devices['Desktop Chrome'], storageState: resolve(__dirname, '.playwright', 'auth.json') } },
  ],
  webServer: [
    {
      // Direct DLL invocation (see repoRoot/apiDll above) — never `dotnet run`, which would be a
      // launcher process Playwright's teardown could lose track of. `--contentRoot` must be
      // ABSOLUTE: with no explicit content root, ASP.NET Core defaults it to the process's
      // current working directory (repo root, via `cwd` below) — fine on its own, but a
      // RELATIVE `--contentRoot` value is instead resolved against the DLL's own base directory
      // (AppContext.BaseDirectory), not the process cwd, a genuine .NET quirk confirmed by direct
      // reproduction. Getting this wrong pointed the host at the repo root as its content root,
      // where appsettings.Development.json does not exist (only its bin-output copy does) — the
      // Settings:SeedOnStartup=true it carries silently never took effect, no seed data was ever
      // created, and the failures cascaded across unrelated specs that all assume real seeded
      // data exists. An absolute path removes the ambiguity entirely.
      command: `dotnet "${apiDll}" --urls https://localhost:7246 --contentRoot "${apiProjectDir}"`,
      cwd: '../..',
      url: 'https://localhost:7246/health/ready',
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 120_000,
      // SECURITY §3.2's 10-req/min-per-IP limit on /api/auth/* is real production behavior and
      // stays that way by default (Program.cs) — every Playwright test shares one "client IP"
      // (localhost) and each page load re-checks the session, so a full E2E run legitimately
      // needs far more than 10 such calls/minute. Raised ONLY for this test-managed host, never
      // in the production composition root.
      // `dotnet run` used to set ASPNETCORE_ENVIRONMENT=Development implicitly via
      // Properties/launchSettings.json's default profile — direct DLL invocation bypasses
      // launchSettings.json entirely (that mechanism is dotnet-run-only), so it must be set here
      // explicitly or the host boots as Production and fails closed on the missing Data
      // Protection certificate (correct Production behavior — this is a real difference between
      // the two invocation styles, not a bug in either one).
      env: { ASPNETCORE_ENVIRONMENT: 'Development', RateLimiting__Auth__PermitLimit: '100000', ConnectionStrings__Verce: e2eConnectionString, VERCE_E2E_RUN_ID: e2eRunId },
    },
    // Production preview, not `vite dev`: a long sequential E2E run hits a real Vite dev-server
    // state issue (its per-module transform/HMR pipeline can wedge after enough real round-trips
    // through the proxy — the ASP.NET Core host itself stays healthy throughout, confirmed by
    // direct requests during the hang). `preview` serves the already-built static bundle, which
    // sidesteps that whole code path and is also more representative of what is actually deployed.
    { command: 'npm run build && npm run preview -- --port 4173 --strictPort', cwd: '../../frontend', url: 'https://localhost:4173', ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 120_000 },
  ],
})
