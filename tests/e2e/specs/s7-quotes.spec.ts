import { test, expect, type Page } from '@playwright/test'

function uniqueDescription(prefix: string) {
  return `${prefix} ${Date.now().toString(36).toUpperCase()}-${Math.random().toString(36).slice(2, 6).toUpperCase()}`
}

/** Creates an ad-hoc quote (DIRECT channel — no fee rule needed, seeded by default) through the
 * real UI and leaves the browser on its detail page. Returns the quote's id (from the resulting
 * detail URL) and its server-assigned number (for list-search assertions). */
async function createAdHocQuote(page: Page, description: string): Promise<{ quoteId: string; number: string }> {
  await page.goto('/quotes/new')
  await expect(page.getByRole('heading', { name: 'Novo orçamento' })).toBeVisible()
  await page.getByLabel('Canal de vendas').selectOption({ label: 'Venda Direta' })
  await page.getByLabel('Item avulso').check()
  await page.getByPlaceholder('Item avulso').fill(description)
  await page.getByLabel(/Custo manual por unidade/).fill('20')
  await page.getByLabel('Quantidade').fill('2')
  await page.getByLabel(/Margem desejada/).fill('40')
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname === '/api/quotes' && r.request().method() === 'POST'),
    page.getByRole('button', { name: 'Criar orçamento' }).click(),
  ])
  expect(response.status()).toBe(201)
  const body = await response.json() as { number: string }
  await expect(page.getByRole('heading', { name: new RegExp(`^Orçamento ${body.number}`) })).toBeVisible()
  const quoteId = new URL(page.url()).pathname.split('/').pop()!
  return { quoteId, number: body.number }
}

test('S7 A: create, revise, send, approve and generate a PDF for a quote through the real UI', async ({ page }) => {
  test.setTimeout(60_000)
  const description = uniqueDescription('S7-VASO')
  const { quoteId, number } = await createAdHocQuote(page, description)

  // Server-authoritative pricing: 2 x (20 cost / (1 - 0.40)) = 2 x 33.33 = 66.66, rounded to cents.
  await expect(page.getByText(description)).toBeVisible()
  await expect(page.getByText('R$ 33,33')).toBeVisible()
  await expect(page.getByText('R$ 66,66').first()).toBeVisible()
  await expect(page.getByText('Gerado', { exact: true })).toBeVisible()

  // ---- S7/S14 scope authority gate mission §88: create -> REVISE -> send -> approve ->
  // generate/download PDF. Revise proves the suffix algorithm, R1 -> SUPERSEDED, and — since
  // proposal content is frozen at revision creation — carries the operator's typed terms onto
  // R2 (never re-read from Settings at render time later).
  await page.getByRole('button', { name: 'Criar nova revisão' }).click()
  await expect(page.getByRole('heading', { name: 'Nova revisão' })).toBeVisible()
  await page.getByLabel('Condições de pagamento').fill('Pix a vista')
  await page.getByLabel('Quantidade').fill('3')
  const [reviseResponse] = await Promise.all([
    page.waitForResponse(r => /\/api\/quotes\/[^/]+\/revise$/.test(new URL(r.url()).pathname) && r.request().method() === 'POST'),
    page.getByRole('button', { name: 'Criar revisão' }).click(),
  ])
  expect(reviseResponse.status()).toBe(200)
  // 2 -> B: revision index 2 displays as suffix "B" (ADR-0004 §2), and the new total reflects
  // the revised quantity: 3 x 33.33 = 99.99.
  await expect(page.getByRole('heading', { name: new RegExp(`^Orçamento ${number}B`) })).toBeVisible()
  await expect(page.getByText('R$ 99,99').first()).toBeVisible()

  // R1 is superseded and no longer the current revision — visible in the timeline, never
  // silently discarded. The timeline renders each revision as several sibling text nodes inside
  // one <li> ("— Substituído Emitido em … · Total … Substituído por outra revisão"), so this
  // matches on the containing list item rather than requiring one element whose ENTIRE text is
  // exactly "Substituído".
  const timelineR1Row = page.getByRole('listitem').filter({ hasText: 'Substituído por outra revisão' })
  await expect(timelineR1Row).toBeVisible()
  await expect(timelineR1Row).toContainText('Substituído')

  const [sendResponse] = await Promise.all([
    page.waitForResponse(r => /\/api\/quotes\/[^/]+\/send$/.test(new URL(r.url()).pathname)),
    page.getByRole('button', { name: 'Enviar' }).click(),
  ])
  expect(sendResponse.status()).toBe(200)
  await expect(page.getByText('Enviado', { exact: true })).toBeVisible()

  const [approveResponse] = await Promise.all([
    page.waitForResponse(r => /\/api\/quotes\/[^/]+\/approve$/.test(new URL(r.url()).pathname)),
    page.getByRole('button', { name: 'Aprovar' }).click(),
  ])
  expect(approveResponse.status()).toBe(200)
  await expect(page.getByText('Aprovado', { exact: true })).toBeVisible()
  await expect(page.getByText('QUEUED')).toBeVisible()

  const [pdfResponse] = await Promise.all([
    page.waitForResponse(r => /\/pdf$/.test(new URL(r.url()).pathname) && r.request().method() === 'POST'),
    page.getByRole('button', { name: 'Gerar PDF' }).click(),
  ])
  expect(pdfResponse.status()).toBe(200)
  const pdfMetadata = await pdfResponse.json() as { downloadUrl: string }
  const downloadLink = page.getByRole('link', { name: 'Baixar PDF' })
  await expect(downloadLink).toBeVisible()
  await expect(downloadLink).toHaveAttribute('href', pdfMetadata.downloadUrl)

  const pdfBytes = await (await page.request.get(pdfMetadata.downloadUrl)).body()
  expect(pdfBytes.subarray(0, 5).toString()).toBe('%PDF-')

  // ---- S7/S14 scope authority gate mission §89: historical-revision document selection. R1
  // is superseded but STILL renderable/downloadable — rendering is never a lifecycle transition
  // (mission §61) — and its document is a genuinely DISTINCT artifact from R2's (mission §37).
  await timelineR1Row.getByRole('button', { name: 'Ver documentos' }).click()
  await expect(timelineR1Row.getByText('Nenhum documento gerado ainda para esta revisão.')).toBeVisible()
  const [r1PdfResponse] = await Promise.all([
    page.waitForResponse(r => /\/pdf$/.test(new URL(r.url()).pathname) && r.request().method() === 'POST'),
    timelineR1Row.getByRole('button', { name: 'Emitir PDF desta revisão' }).click(),
  ])
  expect(r1PdfResponse.status()).toBe(200)
  const r1PdfMetadata = await r1PdfResponse.json() as { id: string; downloadUrl: string }
  expect(r1PdfMetadata.id).not.toBe(pdfMetadata.id) // a genuinely distinct GeneratedDocument row

  const r1PdfBytes = await (await page.request.get(r1PdfMetadata.downloadUrl)).body()
  expect(r1PdfBytes.subarray(0, 5).toString()).toBe('%PDF-')

  // ---- S7 final-findings correction F-03: reissuing the CURRENT (R2) revision's already-issued
  // document requires a real, non-blank reason captured through the actual UI prompt — never an
  // auto-generated placeholder — and that reason is what ends up persisted/audited server-side.
  const timelineR2Row = page.getByRole('listitem').filter({ hasText: 'atual' })
  await timelineR2Row.getByRole('button', { name: 'Ver documentos' }).click()
  const reissueReason = `Cliente pediu correção do endereço ${Date.now()}`
  // R2 already has one issued document, so reissuePdfForRevision shows its "already issued"
  // window.confirm FIRST, then the window.prompt for the reason — both real browser dialogs,
  // answered in the order the UI actually raises them.
  page.on('dialog', dialog => {
    if (dialog.type() === 'confirm') { void dialog.accept(); return }
    expect(dialog.type()).toBe('prompt')
    void dialog.accept(reissueReason)
  })
  const [reissueResponse] = await Promise.all([
    page.waitForResponse(r => /\/pdf\/reissue$/.test(new URL(r.url()).pathname) && r.request().method() === 'POST'),
    timelineR2Row.getByRole('button', { name: 'Reemitir PDF desta revisão' }).click(),
  ])
  expect(reissueResponse.status()).toBe(200)
  const reissueRequestBody = reissueResponse.request().postDataJSON() as { reason: string }
  expect(reissueRequestBody.reason).toBe(reissueReason)
  const reissuedMetadata = await reissueResponse.json() as { id: string; downloadUrl: string }
  expect(reissuedMetadata.id).not.toBe(pdfMetadata.id) // a NEW row, the original superseded but retrievable

  const reissuedBytes = await (await page.request.get(reissuedMetadata.downloadUrl)).body()
  expect(reissuedBytes.subarray(0, 5).toString()).toBe('%PDF-')

  // The quote is reachable again from the list, carrying the same outcome/status forward. The
  // search is a substring ILIKE, so an exact number can still match a longer sibling number from
  // the same day's sequence (e.g. "260921-3" inside "260921-30") — scope the outcome assertion to
  // this quote's own row rather than a page-wide text search.
  await page.goto('/quotes')
  await page.getByLabel('Buscar por número ou cliente').fill(number)
  await page.getByRole('button', { name: 'Buscar' }).click()
  const ownRow = page.getByRole('row', { name: new RegExp(`^${number}[^\\d]`) })
  await expect(ownRow).toBeVisible()
  await expect(ownRow.getByRole('cell', { name: 'Ganho' })).toBeVisible()

  await page.goto(`/quotes/${quoteId}`)
  await expect(page.getByText('Aprovado', { exact: true })).toBeVisible()
})

test('S7 B: a stale aggregate version is rejected as a concurrency conflict, never silently retried', async ({ page }) => {
  test.setTimeout(60_000)
  const description = uniqueDescription('S7-CONFLICT')
  const { quoteId } = await createAdHocQuote(page, description)

  // An external actor sends the quote first — the version this open page still holds is now
  // stale, exactly like two operators editing the same quote concurrently.
  const csrf = await page.evaluate(() => document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/)?.[1])
  const external = await page.request.post(`/api/quotes/${quoteId}/send`, {
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(csrf ?? '') },
    data: { quoteVersion: 1 },
  })
  expect(external.status()).toBe(200)

  // The page's own "Enviar" is now disabled (GENERATED -> SENT no longer applies from its
  // stale view), but "Marcar em negociação" (SENT -> NEGOTIATING) is a valid transition from the
  // status this page still believes is current, sent with its now-stale version.
  const [conflictResponse] = await Promise.all([
    page.waitForResponse(r => /\/api\/quotes\/[^/]+\/negotiate$/.test(new URL(r.url()).pathname)),
    page.getByRole('button', { name: 'Marcar em negociação' }).click(),
  ])
  expect(conflictResponse.status()).toBe(409)
  await expect(page.getByRole('status')).toContainText('alterado por outra pessoa')
  await expect(page.getByRole('button', { name: 'Atualizar' })).toBeVisible()

  // Manual refresh (never a silent retry) shows the real, current state.
  await page.getByRole('button', { name: 'Atualizar' }).click()
  await expect(page.getByText('Enviado', { exact: true })).toBeVisible()
})
