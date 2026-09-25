import { expect, test, type APIRequestContext, type Locator, type Page } from '@playwright/test'
import { readdirSync, rmSync, statSync } from 'node:fs'
import { join } from 'node:path'

/**
 * S8C.1 third-round mandate: real-browser, real-HTTP marketplace authorization journeys, driven
 * against tests/Verce.Marketplaces.E2EHost (see ../playwright.marketplace.config.ts) instead of
 * the real, unmodified src/Verce.Api artifact every other spec in this directory targets.
 *
 * ADR-0024 G-06 sanctions exactly this shape: "Test host registers [the fake] through the real
 * registry/ports and inserts test-only FAKE provider/capability fixtures into its disposable
 * PostgreSQL... Any deterministic fault/consent helper is mapped by the isolated test host only."
 * The E2E host exposes ONE such helper, POST /__e2e__/marketplace/scenarios/{sessionId} (mapped
 * only by that host's own Program.cs, never Verce.Api's), used here to tell the in-process fake
 * connector what external identity a session's exchange should resolve to.
 *
 * The fake's own authorizationUri points at a non-resolvable https://fake-marketplace.test
 * domain, exactly like a real provider's authorization page would from this browser's point of
 * view. Rather than faking that whole external page, page.route intercepts the browser's real
 * top-level navigation to it — reading the session id and state straight off that navigation's
 * own URL (the frontend's begin handler calls window.location.assign(authorizationUri)
 * synchronously the instant its POST resolves, which races Playwright's own CDP body capture for
 * that POST response and reliably loses it — reading the session id from the SUBSEQUENT
 * navigation request sidesteps that race entirely) — configures the fake scenario through the
 * control endpoint, then fulfills a 302 redirect straight to Verce.Api's OWN real callback route
 * (GET /api/commerce/marketplace-authorizations/{provider}/callback). The browser follows that
 * real redirect for real, with the real session-bound cookie the begin request already set,
 * exercising the actual callback/claim/confirm code path end-to-end.
 */

const MARKETPLACE_HOST_ORIGIN = 'https://localhost:7246'
const FAKE_PROVIDER_CODE = 'FAKE'

function uniqueCode(prefix: string) {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
}

function formFor(page: Page, heading: string | RegExp): Locator {
  return page.locator('form').filter({ has: page.getByRole('heading', { name: heading }) })
}

async function selectByText(select: Locator, text: string | RegExp) {
  const value = await select.locator('option').filter({ hasText: text }).first().getAttribute('value')
  expect(value, `expected option matching ${text}`).not.toBeNull()
  await select.selectOption(value!)
}

/** Creates a real Marketplace-kind sales channel through the existing Pricing UI — the same
 * pattern s8b-commerce.spec.ts already uses, unrelated to S8C.1 itself. */
async function createMarketplaceChannel(page: Page): Promise<string> {
  const channelCode = uniqueCode('S8C1CH')
  await page.goto('/pricing')
  const channelForm = formFor(page, 'Novo canal')
  await channelForm.getByLabel('Código').fill(channelCode)
  await channelForm.getByLabel('Nome').fill(`Canal ${channelCode}`)
  await channelForm.getByLabel('Tipo').selectOption('Marketplace')
  const [channelResponse] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/pricing/channels' && r.request().method() === 'POST'),
    channelForm.getByRole('button', { name: 'Criar canal' }).click(),
  ])
  expect(channelResponse.status()).toBe(201)
  return channelCode
}

/** Fills the real "Conectar conta" form (does not submit it) — the only S8C.1-specific fixture
 * selected is the provider dropdown option (Fake E2E Provider, inserted by the E2E host's own
 * Program.cs, never Verce.Api's real seed). */
async function fillNewAccountForm(page: Page, channelCode: string, displayName: string) {
  await page.goto('/marketplace-accounts')
  await page.getByRole('button', { name: 'Conectar conta' }).click()
  await selectByText(page.getByLabel('Provedor'), /Fake E2E Provider/i)
  await selectByText(page.getByLabel('Canal'), channelCode)
  await page.getByLabel('Nome de exibição').fill(displayName)
}

/**
 * Arms the fake-provider navigation intercept for exactly one round trip, submits the form via
 * `submit`, configures the fake connector's scenario for whichever session the browser's own
 * navigation reveals, and waits for the real callback redirect to land with the expected outcome.
 * Returns the session id the browser actually used, read from that same intercepted navigation —
 * never from the original POST's response body (see the file-level doc comment for why).
 */
async function completeAuthorizationRoundTrip(
  page: Page,
  request: APIRequestContext,
  submit: () => Promise<void>,
  externalAccountId: string,
  expectedOutcome: 'completed' | 'failed',
): Promise<string> {
  let capturedSessionId = ''
  await page.route('https://fake-marketplace.test/**', async route => {
    const url = new URL(route.request().url())
    capturedSessionId = url.searchParams.get('session') ?? ''
    const state = url.searchParams.get('state') ?? ''
    const configured = await request.post(`${MARKETPLACE_HOST_ORIGIN}/__e2e__/marketplace/scenarios/${capturedSessionId}`, {
      data: { externalAccountId },
    })
    if (configured.status() !== 204) throw new Error(`E2E control endpoint returned ${configured.status()} configuring session ${capturedSessionId}`)
    // Absolute, on the FRONTEND's own origin (never the fake domain, never the backend port
    // directly) — a relative Location header resolves against the REQUEST's own URL per the HTTP
    // spec, which here would still be https://fake-marketplace.test and re-trigger the exact same
    // DNS failure this route exists to avoid. Landing on :4173 keeps the whole round trip on the
    // same origin real users see: vite preview's own proxy (vite.config.ts) forwards /api/* to
    // the real backend, and Verce.Api's OWN final redirect (a relative path) then resolves
    // against THIS origin too, ending on the real SPA page exactly like a genuine provider
    // callback would.
    const callbackUrl = `https://localhost:4173/api/commerce/marketplace-authorizations/${FAKE_PROVIDER_CODE}/callback?state=${encodeURIComponent(state)}&code=e2e-fake-code`
    await route.fulfill({ status: 302, headers: { location: callbackUrl } })
  })
  // The button's onClick fires an async handler WITHOUT awaiting it (fire-and-forget), so
  // `.click()` itself resolves once the click event is dispatched, well before the handler's own
  // POST/navigation has happened. Combined with a page that may ALREADY be sitting on a URL
  // matching the target pattern (e.g. a previous round trip in the same test landed on the same
  // "?authorization=completed" outcome), page.waitForURL would otherwise resolve immediately
  // against that STALE match instead of genuinely waiting for this round trip's own navigation.
  // Clearing the query string first (no real navigation, so `selected`/React state is untouched)
  // guarantees the pattern cannot already be satisfied.
  await page.evaluate(() => window.history.replaceState(null, '', window.location.pathname))
  await submit()
  await page.waitForURL(new RegExp(`/marketplace-accounts\\?authorization=${expectedOutcome}$`))
  await page.unroute('https://fake-marketplace.test/**')
  expect(capturedSessionId, 'expected the browser to actually navigate through the fake provider').not.toBe('')
  return capturedSessionId
}

function selectAccountByDisplayName(page: Page, displayName: string) {
  return page.getByRole('button', { name: displayName, exact: true }).click()
}

test.describe('S8C.1 marketplace authorization (real browser, real HTTP, isolated E2E host)', () => {
  test('focused flow: connect a new account, then voluntarily reconnect it', async ({ page, request }) => {
    test.setTimeout(120_000)
    const marker = uniqueCode('S8C1FOCUS')
    const externalId = `${marker}-EXT`
    const displayName = `Loja ${marker}`

    const channelCode = await createMarketplaceChannel(page)
    await fillNewAccountForm(page, channelCode, displayName)
    await completeAuthorizationRoundTrip(
      page, request,
      () => page.getByRole('button', { name: 'Autorizar com provedor' }).click(),
      externalId, 'completed',
    )

    await selectAccountByDisplayName(page, displayName)
    await expect(page.getByRole('status').getByText('CONNECTED', { exact: false })).toBeVisible()
    await expect(page.getByRole('alert')).toHaveCount(0)

    // Voluntary reconnect — the real "Reautorizar" recovery path, not triggered by any failure.
    await completeAuthorizationRoundTrip(
      page, request,
      () => page.getByRole('button', { name: 'Reautorizar' }).click(),
      externalId, 'completed',
    )

    await page.goto('/marketplace-accounts')
    await selectAccountByDisplayName(page, displayName)
    await expect(page.getByRole('status').getByText('CONNECTED', { exact: false })).toBeVisible()
    await expect(page.getByRole('alert')).toHaveCount(0)
  })

  test('store-loss recovery: probe after simulated local-store loss routes to reauthorize, and reconnecting issues a fresh (K2) credential', async ({ page, request }) => {
    test.setTimeout(120_000)
    const marker = uniqueCode('S8C1LOSS')
    const externalId = `${marker}-EXT`
    const displayName = `Loja ${marker}`
    const credentialStoreRoot = process.env.VERCE_E2E_MARKETPLACE_CREDENTIAL_STORE_ROOT
    if (!credentialStoreRoot) throw new Error('VERCE_E2E_MARKETPLACE_CREDENTIAL_STORE_ROOT must be set by playwright.marketplace.config.ts')

    const channelCode = await createMarketplaceChannel(page)
    await fillNewAccountForm(page, channelCode, displayName)
    await completeAuthorizationRoundTrip(
      page, request,
      () => page.getByRole('button', { name: 'Autorizar com provedor' }).click(),
      externalId, 'completed',
    )

    // Harness-side fault injection (explicitly sanctioned for this journey): confirmed credential
    // envelopes live as *.bin files directly under the store root (never under its staged/ or
    // candidates/ subdirectories, which this deliberately leaves untouched) — deleting them
    // simulates total local-store/key loss without needing to know the store's internal
    // SHA-256-derived file-naming scheme.
    const confirmedFilesBefore = readdirSync(credentialStoreRoot)
      .filter(name => name.endsWith('.bin'))
      .filter(name => statSync(join(credentialStoreRoot, name)).isFile())
    expect(confirmedFilesBefore.length, 'expected at least one confirmed credential file before simulating loss').toBeGreaterThan(0)
    for (const name of confirmedFilesBefore) rmSync(join(credentialStoreRoot, name), { force: true })

    await selectAccountByDisplayName(page, displayName)
    const [probeResponse] = await Promise.all([
      page.waitForResponse(r => /\/api\/commerce\/marketplace-accounts\/[^/]+\/probe$/.test(new URL(r.url()).pathname)),
      page.getByRole('button', { name: 'Sondar disponibilidade' }).click(),
    ])
    expect(probeResponse.status()).toBe(409)
    await expect(page.getByText('Sondagem falhou', { exact: false })).toBeVisible()
    await expect(page.getByRole('alert')).toContainText('perda de armazenamento/chave', { ignoreCase: true })
    const recoverButton = page.getByRole('button', { name: 'Recuperar credencial (reautorizar)' })
    await expect(recoverButton).toBeVisible()

    await completeAuthorizationRoundTrip(
      page, request,
      () => page.getByRole('button', { name: 'Recuperar credencial (reautorizar)' }).click(),
      externalId, 'completed',
    )

    await page.goto('/marketplace-accounts')
    await selectAccountByDisplayName(page, displayName)
    await expect(page.getByRole('status').getByText('CONNECTED', { exact: false })).toBeVisible()
    await expect(page.getByRole('alert')).toHaveCount(0)

    // K1 -> K2: a NEW confirmed credential file exists — never a silent reuse of the lost one.
    const confirmedFilesAfter = readdirSync(credentialStoreRoot)
      .filter(name => name.endsWith('.bin'))
      .filter(name => statSync(join(credentialStoreRoot, name)).isFile())
    expect(confirmedFilesAfter.length).toBeGreaterThan(0)
  })

  test('duplicate authorization: a second new-session exchange for the same external identity never creates a second account and routes the existing one to reconnect', async ({ page, request }) => {
    test.setTimeout(120_000)
    const marker = uniqueCode('S8C1DUP')
    const externalId = `${marker}-EXT`
    const displayName1 = `Loja ${marker} A`
    const displayName2 = `Loja ${marker} B`

    const channelCode = await createMarketplaceChannel(page)

    await fillNewAccountForm(page, channelCode, displayName1)
    await completeAuthorizationRoundTrip(
      page, request,
      () => page.getByRole('button', { name: 'Autorizar com provedor' }).click(),
      externalId, 'completed',
    )

    await fillNewAccountForm(page, channelCode, displayName2)
    await completeAuthorizationRoundTrip(
      page, request,
      () => page.getByRole('button', { name: 'Autorizar com provedor' }).click(),
      externalId, 'failed',
    )

    // No second account was ever created — only the FIRST display name is present.
    await page.goto('/marketplace-accounts')
    await expect(page.getByRole('button', { name: displayName1, exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: displayName2, exact: true })).toHaveCount(0)

    await selectAccountByDisplayName(page, displayName1)
    await expect(page.getByRole('alert')).toContainText('Uma nova autorização identificou esta MESMA conta', { ignoreCase: true })
    await expect(page.getByRole('button', { name: 'Reconectar conta existente' })).toBeVisible()
  })
})
