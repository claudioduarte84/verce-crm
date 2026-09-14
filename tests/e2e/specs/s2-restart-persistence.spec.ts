import { test, expect, request, type APIRequestContext } from '@playwright/test'
import { execFileSync, spawn, type ChildProcess } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { resolveE2eConnectionString, assertDisposableE2eDatabase, resolveOwnerEmail } from '../e2e-env.cjs'

// M-S2-002: this must be a GENUINE OS process restart — not a page reload, not a fresh
// DbContext, not a second Playwright invocation depending on state left by an earlier run. Each
// test below spawns its own dedicated API process (never the shared Playwright `webServer`, so
// this cannot disturb any other spec), creates synthetic state through the real HTTP API,
// terminates that process, starts a genuinely new one against the SAME PostgreSQL database and
// the SAME Brand Asset storage root, re-authenticates from scratch, and proves every value
// survived byte-for-byte. Fully self-contained: no cross-invocation state marker, no assumption
// about what an earlier test run left behind.

const REPO_ROOT = resolve(__dirname, '..', '..', '..')
const API_DLL = resolve(REPO_ROOT, 'src/Verce.Api/bin/Debug/net10.0/Verce.Api.dll')
// Resolved from the SAME canonical source (tests/e2e/e2e-env.cjs) the Playwright webServer and
// global-setup.cjs use — never the shared "verce" dev database, and never a second, parallel
// configuration mechanism. Asserted disposable so a misconfigured environment fails fast (§5)
// instead of silently restarting against real developer data.
const E2E_CONNECTION_STRING = resolveE2eConnectionString()
assertDisposableE2eDatabase(E2E_CONNECTION_STRING)
const OWNER_EMAIL = resolveOwnerEmail()
const OWNER_PASSWORD = 'e2e-synthetic-password-123'

// A real, deterministically-generated 4×4 PNG (not the shared 1×1 placeholder other specs use
// for pure upload-mechanics checks) — this test also proves the server's re-encoded content
// survives a restart byte-for-byte, so the source image must be genuinely decodable.
const restartTestPng = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGM45MgDRwzEcQDEAxDxkT0TmwAAAABJRU5ErkJggg==',
  'base64',
)

function startApiProcess(port: number, storageRoot: string, dataProtectionDir: string): ChildProcess {
  return spawn('dotnet', [API_DLL, '--urls', `https://localhost:${port}`], {
    cwd: REPO_ROOT,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ConnectionStrings__Verce: E2E_CONNECTION_STRING,
      BrandAssets__StorageRoot: storageRoot,
      DataProtection__DevKeyDirectory: dataProtectionDir,
      Settings__SeedOnStartup: 'false',
      Outbox__SchedulingEnabled: 'false',
    },
    stdio: 'ignore',
    windowsHide: true,
  })
}

/** Deterministic shutdown with no orphan process (§7): on Windows, `taskkill /t /f` tears down
 * the whole process tree rather than relying on Node's best-effort `kill()` semantics. */
async function stopApiProcess(proc: ChildProcess | undefined): Promise<void> {
  if (!proc || proc.pid == null || proc.exitCode !== null) return
  const pid = proc.pid
  if (process.platform === 'win32') {
    try { execFileSync('taskkill', ['/pid', String(pid), '/t', '/f'], { stdio: 'ignore' }) } catch { /* already gone */ }
  } else {
    try { proc.kill('SIGKILL') } catch { /* already gone */ }
  }
  await new Promise<void>((done) => {
    if (proc.exitCode !== null) { done(); return }
    proc.once('exit', () => done())
    setTimeout(done, 5000)
  })
}

async function waitForHealth(baseURL: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs
  let lastStatus: number | string = 'never responded'
  while (Date.now() < deadline) {
    const probe = await request.newContext({ baseURL, ignoreHTTPSErrors: true })
    try {
      const response = await probe.get('/health/ready', { timeout: 2000 })
      if (response.status() === 200) return
      lastStatus = response.status()
    } catch (error) {
      lastStatus = String(error)
    } finally {
      await probe.dispose()
    }
    await new Promise((r) => setTimeout(r, 300))
  }
  throw new Error(`API did not become healthy within ${timeoutMs}ms (last status: ${lastStatus})`)
}

async function readXsrfCookie(context: APIRequestContext): Promise<string> {
  const state = await context.storageState()
  const cookie = state.cookies.find((c) => c.name === 'XSRF-TOKEN')
  if (!cookie) throw new Error('XSRF-TOKEN cookie missing after CSRF bootstrap')
  return decodeURIComponent(cookie.value)
}

/** Real re-authentication from scratch (§12) — this context has never talked to this process
 * before, exactly like a genuinely new client after a restart. */
async function loginAsOwner(context: APIRequestContext): Promise<string> {
  await context.get('/api/auth/csrf')
  let xsrf = await readXsrfCookie(context)
  const loginResponse = await context.post('/api/auth/login', {
    headers: { 'X-XSRF-TOKEN': xsrf },
    data: { email: OWNER_EMAIL, password: OWNER_PASSWORD },
  })
  if (loginResponse.status() !== 204) {
    throw new Error(`Owner login failed: ${loginResponse.status()} ${await loginResponse.text()}`)
  }
  // Mirrors the real frontend (LoginPage -> refresh({refreshCsrf:true})): the anonymous
  // antiforgery token is replaced by one minted for the authenticated principal.
  await context.get('/api/auth/csrf')
  xsrf = await readXsrfCookie(context)
  return xsrf
}

interface CapturedState {
  customerId: string
  customerName: string
  customerVersion: number
  addressId: string
  addressLabel: string
  profileLegalName: string
  settingKey: string
  settingValue: string
  brandVersionId: string
  brandContentSha256: string
}

async function createSyntheticStateAsync(context: APIRequestContext, xsrf: string, suffix: string): Promise<CapturedState> {
  const customerName = `Restart Customer ${suffix}`
  const customerCreate = await context.post('/api/customers', {
    headers: { 'X-XSRF-TOKEN': xsrf },
    data: { personType: 'Individual', name: customerName, version: 0 },
  })
  expect(customerCreate.status(), 'customer creation').toBe(201)
  const customer = await customerCreate.json() as { id: string; version: number }

  const addressLabel = `Restart Address ${suffix}`
  const addressCreate = await context.post(`/api/customers/${customer.id}/addresses`, {
    headers: { 'X-XSRF-TOKEN': xsrf },
    data: {
      label: addressLabel, zipCode: '01001-000', street: 'Rua Restart', number: '1', district: 'Centro',
      city: 'São Paulo', state: 'SP', country: 'BR', isPrimary: true, isDefaultShipping: true, customerVersion: customer.version,
    },
  })
  expect(addressCreate.status(), 'address creation').toBe(201)
  const address = await addressCreate.json() as { id: string }

  // Adding an owned child bumps the aggregate root's version (ADR-0011 §2.1) — the version to
  // compare after restart is the customer's FINAL state, not the value captured at creation.
  const customerAfterAddress = await (await context.get(`/api/customers/${customer.id}`)).json() as { version: number }

  const profileGet = await context.get('/api/settings/company-profile')
  const profile = await profileGet.json()
  const profileLegalName = `Restart Profile ${suffix}`
  const profilePut = await context.put('/api/settings/company-profile', {
    headers: { 'X-XSRF-TOKEN': xsrf },
    data: { ...profile, legalName: profileLegalName },
  })
  expect(profilePut.status(), 'company profile update').toBe(204)

  const settingsGet = await context.get('/api/settings')
  const settings = await settingsGet.json() as { key: string; version: number }[]
  const setting = settings.find((s) => s.key === 'branding.product_subtitle')!
  const settingValue = `Restart Subtitle ${suffix}`
  const settingPut = await context.put(`/api/settings/${encodeURIComponent(setting.key)}`, {
    headers: { 'X-XSRF-TOKEN': xsrf },
    data: { value: settingValue, version: setting.version },
  })
  expect(settingPut.status(), 'app setting update').toBe(204)

  const assetCreate = await context.post('/api/settings/brand-assets', {
    headers: { 'X-XSRF-TOKEN': xsrf },
    data: { brandAssetTypeCode: 'OTHER', name: `Restart Asset ${suffix}` },
  })
  expect(assetCreate.status(), 'brand asset creation').toBe(201)
  const asset = await assetCreate.json() as { id: string }

  const versionUpload = await context.post(`/api/settings/brand-assets/${asset.id}/versions`, {
    headers: { 'X-XSRF-TOKEN': xsrf },
    multipart: { file: { name: `${suffix}.png`, mimeType: 'image/png', buffer: restartTestPng } },
  })
  expect(versionUpload.status(), 'brand version upload').toBe(201)
  const version = await versionUpload.json() as { id: string }

  // The server RE-ENCODES uploaded images (ADR-0015 §6) — hash what it actually SERVES, not the
  // original upload, since that resolved content is exactly what must survive the restart.
  const contentGet = await context.get(`/api/settings/brand-assets/versions/${version.id}/content`)
  expect(contentGet.status(), 'brand content fetch (before restart)').toBe(200)
  const contentBytes = await contentGet.body()
  const brandContentSha256 = createHash('sha256').update(contentBytes).digest('hex')

  return {
    customerId: customer.id, customerName, customerVersion: customerAfterAddress.version,
    addressId: address.id, addressLabel, profileLegalName, settingKey: setting.key, settingValue,
    brandVersionId: version.id, brandContentSha256,
  }
}

async function verifyStateSurvivedAsync(context: APIRequestContext, state: CapturedState): Promise<void> {
  const customerGet = await context.get(`/api/customers/${state.customerId}`)
  expect(customerGet.status(), 'customer must survive restart').toBe(200)
  const customer = await customerGet.json() as { name: string; version: number; addresses: { id: string; label: string }[] }
  expect(customer.name).toBe(state.customerName)
  expect(customer.version).toBe(state.customerVersion)
  const address = customer.addresses.find((a) => a.id === state.addressId)
  expect(address, 'address must survive restart').toBeTruthy()
  expect(address!.label).toBe(state.addressLabel)

  const profileGet = await context.get('/api/settings/company-profile')
  expect(profileGet.status()).toBe(200)
  expect((await profileGet.json()).legalName).toBe(state.profileLegalName)

  const settingsGet = await context.get('/api/settings')
  const settings = await settingsGet.json() as { key: string; value: string }[]
  expect(settings.find((s) => s.key === state.settingKey)?.value).toBe(state.settingValue)

  const contentGet = await context.get(`/api/settings/brand-assets/versions/${state.brandVersionId}/content`)
  expect(contentGet.status(), 'brand content must survive restart').toBe(200)
  const bytes = await contentGet.body()
  const sha256 = createHash('sha256').update(bytes).digest('hex')
  expect(sha256, 'brand content must be byte-identical after a real process restart').toBe(state.brandContentSha256)
}

async function runHermeticRestartCycle(iterationLabel: string, port: number): Promise<void> {
  const suffix = `${iterationLabel}-${Date.now().toString(36)}`
  const storageRoot = mkdtempSync(join(tmpdir(), 'verce-restart-storage-'))
  const dataProtectionDir = mkdtempSync(join(tmpdir(), 'verce-restart-dp-'))
  const baseURL = `https://localhost:${port}`
  let processA: ChildProcess | undefined
  let processB: ChildProcess | undefined

  try {
    // ---- Process A: create and capture state ----
    processA = startApiProcess(port, storageRoot, dataProtectionDir)
    await waitForHealth(baseURL, 60_000)

    const contextA = await request.newContext({ baseURL, ignoreHTTPSErrors: true })
    let state: CapturedState
    try {
      const xsrfA = await loginAsOwner(contextA)
      state = await createSyntheticStateAsync(contextA, xsrfA, suffix)
    } finally {
      await contextA.dispose()
    }

    // ---- Genuine OS process restart: A terminates, B starts fresh ----
    await stopApiProcess(processA)
    processA = undefined

    processB = startApiProcess(port, storageRoot, dataProtectionDir)
    await waitForHealth(baseURL, 60_000)

    // ---- Process B: fresh client, fresh login, verify ----
    const contextB = await request.newContext({ baseURL, ignoreHTTPSErrors: true })
    try {
      await loginAsOwner(contextB)
      await verifyStateSurvivedAsync(contextB, state)
    } finally {
      await contextB.dispose()
    }
  } finally {
    await stopApiProcess(processA)
    await stopApiProcess(processB)
    rmSync(storageRoot, { recursive: true, force: true })
    rmSync(dataProtectionDir, { recursive: true, force: true })
  }
}

test('hermetic restart-persistence — iteration 1', async () => {
  test.setTimeout(120_000)
  await runHermeticRestartCycle('run1', 7247)
})

test('hermetic restart-persistence — iteration 2', async () => {
  test.setTimeout(120_000)
  await runHermeticRestartCycle('run2', 7248)
})

test('hermetic restart-persistence — iteration 3', async () => {
  test.setTimeout(120_000)
  await runHermeticRestartCycle('run3', 7249)
})
