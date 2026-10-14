/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  BLOCKED_REASON_MESSAGES, ITEM_RESULT_MESSAGES, LINKAGE_SOURCE_LABELS, REQUIRED_ACTION_MESSAGES, RUN_STATUS_LABELS, RUN_STOP_MESSAGES,
  SKU_MAPPING_ERROR_MESSAGES, SYNC_HEALTH_LABELS, SYNC_REQUEST_MESSAGES,
  blockedReasonMessage, formatRetryAfter, linkageSourceLabel, requiredActionMessage, runStatusLabel, safeResultMessage, skuMappingErrorMessage,
  syncHealthLabel, syncRequestMessage,
} from './marketplaceCatalog'

// Read straight from the repository (vitest runs from frontend/): a Vite `?raw` import outside the frontend root is denied, and the config is not C3's to change.
const handoff = readFileSync(resolve(process.cwd(), '../docs/S8C2-IMPLEMENTATION-HANDOFF.md'), 'utf8')

/** The C3 table of docs/S8C2-IMPLEMENTATION-HANDOFF.md, parsed: the literal source of every frozen code and text. */
function frozenC3Table(): Map<string, string> {
  const start = handoff.indexOf('**C3 — pt-BR catalog**'), end = handoff.indexOf('**C4 — ')
  expect(start, 'C3 heading in the handoff').toBeGreaterThan(-1); expect(end).toBeGreaterThan(start)
  const rows = new Map<string, string>()
  for (const line of handoff.slice(start, end).split(/\r?\n/)) {
    const cells = line.split(/(?<!\\)\|/).map((c) => c.trim())
    if (cells.length < 4 || !cells[1].startsWith('`')) continue
    const codes = cells[1].split(' / ').map((c) => c.replace(/`/g, '').trim()), texts = cells[2].split(' / ').map((t) => t.trim())
    expect(texts.length, `row ${cells[1]} has one text per code`).toBe(codes.length)
    codes.forEach((code, index) => { expect(rows.has(code), `duplicate ${code}`).toBe(false); rows.set(code, texts[index]) })
  }
  return rows
}

const ALL_CATALOGS = {
  BLOCKED_REASON_MESSAGES, RUN_STATUS_LABELS, SYNC_HEALTH_LABELS, ITEM_RESULT_MESSAGES, RUN_STOP_MESSAGES, SYNC_REQUEST_MESSAGES, SKU_MAPPING_ERROR_MESSAGES, LINKAGE_SOURCE_LABELS,
} as const
const catalogEntries = () => Object.values(ALL_CATALOGS).flatMap((catalog) => Object.entries(catalog))

describe('C3 catalog parity with the frozen handoff table', () => {
  const frozen = frozenC3Table()

  it('parses the whole frozen table (43 codes)', () => { expect(frozen.size).toBe(43) })

  it.each([...frozenC3Table()])('%s has exactly the frozen text', (code, text) => {
    const found = catalogEntries().filter(([key]) => key === code)
    expect(found, `${code} is in exactly one catalog`).toHaveLength(1)
    expect(found[0][1]).toBe(text)
  })

  it('contains no public code outside the frozen contract', () => {
    expect(new Set(catalogEntries().map(([code]) => code))).toEqual(new Set(frozen.keys()))
    expect(catalogEntries()).toHaveLength(frozen.size)
  })

  it.each([
    ['blockedReason', BLOCKED_REASON_MESSAGES, 10], ['run status', RUN_STATUS_LABELS, 5], ['health', SYNC_HEALTH_LABELS, 4], ['item results', ITEM_RESULT_MESSAGES, 9],
    ['run stop codes', RUN_STOP_MESSAGES, 9], ['request refusals', SYNC_REQUEST_MESSAGES, 1], ['SKU mapping errors', SKU_MAPPING_ERROR_MESSAGES, 3], ['linkage source', LINKAGE_SOURCE_LABELS, 2],
  ] as const)('%s catalog has %i codes', (_name, catalog, count) => { expect(Object.keys(catalog)).toHaveLength(count) })

  it('keeps the distinct authorities in distinct catalogs', () => {
    const owners = new Map<string, string[]>()
    for (const [name, catalog] of Object.entries(ALL_CATALOGS)) for (const code of Object.keys(catalog)) owners.set(code, [...(owners.get(code) ?? []), name])
    for (const [code, names] of owners) expect(names, code).toHaveLength(1)
    // Health and run status share no label key: FAILED is a run status only and IN_PROGRESS a health only.
    expect(Object.keys(RUN_STATUS_LABELS)).not.toContain('IN_PROGRESS'); expect(Object.keys(SYNC_HEALTH_LABELS)).not.toContain('FAILED')
  })

  it('freezes every catalog', () => {
    for (const catalog of [...Object.values(ALL_CATALOGS), REQUIRED_ACTION_MESSAGES]) expect(Object.isFrozen(catalog)).toBe(true)
  })
})

describe('SKU_MAPPING_NOT_ACTIVE is not public contract (ADR-0028)', () => {
  it('has no key, text, alias or special lookup anywhere in the catalog', () => {
    const everything = [...catalogEntries(), ...Object.entries(REQUIRED_ACTION_MESSAGES)]
    expect(everything.map(([code]) => code)).not.toContain('SKU_MAPPING_NOT_ACTIVE')
    expect(JSON.stringify(everything)).not.toMatch(/NOT_ACTIVE|NOT.ACTIVE/i)
    expect(skuMappingErrorMessage('SKU_MAPPING_NOT_ACTIVE')).toBe('SKU_MAPPING_NOT_ACTIVE') // the generic fallback, never a message
    expect(safeResultMessage('SKU_MAPPING_NOT_ACTIVE')).toBe('SKU_MAPPING_NOT_ACTIVE')
    expect(syncRequestMessage('SKU_MAPPING_NOT_ACTIVE')).toBe('SKU_MAPPING_NOT_ACTIVE')
  })
})

describe('RATE_LIMITED {hora} interpolation', () => {
  const template = 'Limite de requisições do provedor atingido. Tente novamente após {hora}.'
  it('keeps the frozen template in the table', () => { expect(BLOCKED_REASON_MESSAGES.RATE_LIMITED).toBe(template) })

  it.each([
    ['2026-10-09T14:05:00Z', 'UTC', '09/10/2026 14:05'],
    ['2026-10-09T14:05:00+00:00', 'UTC', '09/10/2026 14:05'],
    ['2026-10-09T14:05:30.1234567+00:00', 'UTC', '09/10/2026 14:05'],
    ['2026-10-09T14:05:00Z', 'America/Sao_Paulo', '09/10/2026 11:05'],
    ['2026-10-09T14:05:00-03:00', 'UTC', '09/10/2026 17:05'],
    ['2026-10-09T23:30:00-03:00', 'America/Sao_Paulo', '09/10/2026 23:30'],
    ['2026-12-31T23:59:00Z', 'UTC', '31/12/2026 23:59'],
    ['2026-10-09T00:00:00Z', 'UTC', '09/10/2026 00:00'],
    ['2028-02-29T12:00:00Z', 'UTC', '29/02/2028 12:00'],
    ['2026-10-09T14:05:00+0300', 'UTC', '09/10/2026 11:05'],
  ])('fills {hora} from %s in %s as %s (offset respected, pt-BR, 24h)', (iso, timeZone, expected) => {
    expect(formatRetryAfter(iso, { timeZone })).toBe(expected)
    expect(blockedReasonMessage('RATE_LIMITED', iso, { timeZone })).toBe(`Limite de requisições do provedor atingido. Tente novamente após ${expected}.`)
  })

  it('is deterministic for the same instant and independent of other catalog calls', () => {
    const first = blockedReasonMessage('RATE_LIMITED', '2026-10-09T14:05:00Z', { timeZone: 'UTC' })
    blockedReasonMessage('GRANT_DENIED'); runStatusLabel('QUEUED')
    expect(blockedReasonMessage('RATE_LIMITED', '2026-10-09T14:05:00Z', { timeZone: 'UTC' })).toBe(first)
  })

  it('formats in the viewer zone by default, never as an unresolved placeholder', () => {
    const text = blockedReasonMessage('RATE_LIMITED', '2026-10-09T14:05:00Z')
    expect(text).toMatch(/^Limite de requisições do provedor atingido\. Tente novamente após \d{2}\/\d{2}\/\d{4} \d{2}:\d{2}\.$/)
  })

  it('also fills the placeholder when RATE_LIMITED arrives as a run safeResultCode', () => {
    expect(safeResultMessage('RATE_LIMITED', '2026-10-09T14:05:00Z', { timeZone: 'UTC' })).toBe('Limite de requisições do provedor atingido. Tente novamente após 09/10/2026 14:05.')
  })

  it('only RATE_LIMITED carries a placeholder', () => {
    const withPlaceholder = catalogEntries().filter(([, text]) => text.includes('{'))
    expect(withPlaceholder.map(([code]) => code)).toEqual(['RATE_LIMITED'])
  })
})

describe('absent or invalid retryAfterUntil', () => {
  const sentence = 'Limite de requisições do provedor atingido.'
  it.each([
    ['null', null], ['undefined', undefined], ['empty', ''], ['blank', '   '], ['garbage', 'amanhã'], ['date only', '2026-10-09'],
    ['no offset (zone would be assumed)', '2026-10-09T14:05:00'], ['impossible date', '2026-13-45T25:61:00Z'], ['invalid month', '2026-13-01T10:00:00Z'],
    ['31 February (would roll into March)', '2026-02-31T10:00:00Z'], ['29 February in a common year', '2026-02-29T10:00:00Z'], ['31 April', '2026-04-31T10:00:00Z'], ['day zero', '2026-10-00T10:00:00Z'],
    ['hour 24', '2026-10-09T24:00:00Z'], ['minute 60', '2026-10-09T10:60:00Z'], ['second 60', '2026-10-09T10:00:60Z'], ['offset hour 24', '2026-10-09T10:00:00+24:00'], ['offset minute 60', '2026-10-09T10:00:00+03:60'], ['epoch number as text', '1760000000'],
    ['a number', 1760000000 as unknown as string], ['an object', {} as unknown as string],
  ])('%s yields no invented time and no leaked placeholder', (_label, value) => {
    expect(formatRetryAfter(value as string | null | undefined, { timeZone: 'UTC' })).toBeNull()
    const text = blockedReasonMessage('RATE_LIMITED', value as string | null | undefined, { timeZone: 'UTC' })
    expect(text).toBe(sentence)
    for (const forbidden of ['{hora}', '{', 'undefined', 'null', 'Invalid Date', 'NaN', 'Tente novamente']) expect(text).not.toContain(forbidden)
  })

  it('an unsupported time zone name degrades to no time instead of throwing', () => {
    expect(formatRetryAfter('2026-10-09T14:05:00Z', { timeZone: 'Mars/Olympus' })).toBeNull()
    expect(blockedReasonMessage('RATE_LIMITED', '2026-10-09T14:05:00Z', { timeZone: 'Mars/Olympus' })).toBe(sentence)
  })

  it('other blocked reasons ignore retryAfterUntil entirely', () => {
    expect(blockedReasonMessage('GRANT_DENIED', '2026-10-09T14:05:00Z')).toBe(BLOCKED_REASON_MESSAGES.GRANT_DENIED)
    expect(blockedReasonMessage('SYNC_ACTIVE', 'not a date')).toBe(BLOCKED_REASON_MESSAGES.SYNC_ACTIVE)
  })
})

describe('unknown and absent codes', () => {
  it.each([
    ['blockedReasonMessage', blockedReasonMessage], ['runStatusLabel', runStatusLabel], ['syncHealthLabel', syncHealthLabel], ['safeResultMessage', safeResultMessage],
    ['syncRequestMessage', syncRequestMessage], ['skuMappingErrorMessage', skuMappingErrorMessage], ['linkageSourceLabel', linkageSourceLabel], ['requiredActionMessage', requiredActionMessage],
  ] as const)('%s falls back to the code itself and renders nothing for an absent code', (_name, lookup) => {
    expect(lookup('SOME_FUTURE_SAFE_CODE')).toBe('SOME_FUTURE_SAFE_CODE')
    expect(lookup(null)).toBe(''); expect(lookup(undefined)).toBe('')
  })

  it('never resolves inherited object keys and never treats an unknown code as success', () => {
    for (const key of ['constructor', 'toString', '__proto__', 'hasOwnProperty']) {
      expect(runStatusLabel(key)).toBe(key); expect(safeResultMessage(key)).toBe(key); expect(blockedReasonMessage(key)).toBe(key); expect(requiredActionMessage(key)).toBe(key)
    }
    expect(runStatusLabel('SOMETHING_NEW')).not.toBe(RUN_STATUS_LABELS.SUCCEEDED)
    expect(syncHealthLabel('FAILED')).toBe('FAILED') // no frozen health text for FAILED: the code, never "Anúncios em dia"
  })

  it('does not reinterpret codes by case or whitespace', () => {
    expect(runStatusLabel('succeeded')).toBe('succeeded'); expect(runStatusLabel(' SUCCEEDED')).toBe(' SUCCEEDED')
  })

  it('resolves the codes without a frozen run-stop text through the same fallback', () => {
    for (const code of ['LISTING_SYNC_COMPLETED', 'LISTING_SYNC_PARTIAL', 'LISTING_SYNC_NO_SUCCESSFUL_ITEMS', 'CREDENTIAL_CONFIRMED_LOST', 'CAPABILITY_CODE_REQUIRED']) expect(safeResultMessage(code)).toBe(code)
  })
})

describe('safeResultMessage covers item results, run stops and guard stops', () => {
  it('routes each family to its own text', () => {
    expect(safeResultMessage('LISTING_READ_FAILED')).toBe('Falha ao ler este anúncio.')
    expect(safeResultMessage('LISTING_READ_REJECTED')).toBe('O provedor rejeitou a leitura deste anúncio.')
    expect(safeResultMessage('PROVIDER_CREDENTIAL_REJECTED')).toBe('Credencial recusada pelo provedor. Reautorize a conta.')
    expect(safeResultMessage('PROVIDER_UNAVAILABLE')).toBe('Provedor indisponível.')
    expect(safeResultMessage('PROVIDER_RATE_LIMITED')).toBe('Limite de requisições do provedor atingido.')
    expect(safeResultMessage('GRANT_DENIED')).toBe('O provedor negou a leitura de anúncios. Verifique a conta no provedor.')
    expect(safeResultMessage('RATE_LIMITED')).toBe('Limite de requisições do provedor atingido.')
  })
})

describe('literal texts that carry quoted guidance', () => {
  it('preserves the straight quotes around "Sondar disponibilidade"', () => {
    expect(BLOCKED_REASON_MESSAGES.GRANT_UNKNOWN).toBe('Permissão de leitura de anúncios ainda não verificada. Use "Sondar disponibilidade".')
    expect(BLOCKED_REASON_MESSAGES.RUNTIME_UNAVAILABLE).toBe('O provedor está indisponível para esta conta. Use "Sondar disponibilidade".')
  })
  it('labels statuses, health and linkage sources with their frozen terms', () => {
    expect(runStatusLabel('PARTIAL')).toBe('Concluída com pendências'); expect(syncHealthLabel('HEALTHY')).toBe('Anúncios em dia'); expect(syncHealthLabel('ATTENTION_REQUIRED')).toBe('Requer atenção')
    expect(linkageSourceLabel('DETERMINISTIC_SKU')).toBe('Vinculado automaticamente por mapeamento de SKU confirmado'); expect(linkageSourceLabel('MANUAL')).toBe('Vinculado manualmente')
    expect(linkageSourceLabel(null)).toBe('')
  })
})

describe('requiredAction messages stay literally equal to the S8C.1 account page texts (ADR-0027 §5/§9)', () => {
  it.each([
    ['AUTHORIZE', 'Conecte esta conta autorizando com o provedor.'],
    ['REAUTHORIZE', 'Autorização não é mais válida. Reautorize com o provedor.'],
    ['RECONNECT_EXISTING_ACCOUNT', 'Uma nova autorização identificou esta MESMA conta no provedor. Reconecte explicitamente esta conta em vez de criar uma nova.'],
    ['REAUTHORIZE_AFTER_STORE_LOSS', 'A credencial protegida local não pôde ser confirmada (perda de armazenamento/chave). Reautorize para gerar uma nova credencial; a conta e o histórico são preservados.'],
    ['CHECK_PROVIDER_ACCOUNT', 'O provedor negou uma capacidade ou uma operação falhou. Verifique a conta diretamente no provedor.'],
    ['CONTACT_ADMIN', 'Contate o administrador: configuração ou armazenamento de credenciais indisponível.'],
    ['NONE', ''],
  ])('%s', (code, text) => { expect(requiredActionMessage(code)).toBe(text) })

  it('is exactly the seven ADR-0024 values, with no WAIT, RETRY or START_SYNC', () => {
    expect(Object.keys(REQUIRED_ACTION_MESSAGES).sort()).toEqual(['AUTHORIZE', 'CHECK_PROVIDER_ACCOUNT', 'CONTACT_ADMIN', 'NONE', 'REAUTHORIZE', 'REAUTHORIZE_AFTER_STORE_LOSS', 'RECONNECT_EXISTING_ACCOUNT'])
    for (const word of ['WAIT', 'RETRY', 'START_SYNC']) expect(requiredActionMessage(word)).toBe(word)
  })
})

describe('purity', () => {
  it('works with no network, session, React or timers', () => {
    const fetchSpy = typeof globalThis.fetch === 'function' ? globalThis.fetch : undefined
    const calls: string[] = []
    globalThis.fetch = ((...args: unknown[]) => { calls.push(String(args[0])); throw new Error('network is forbidden') }) as typeof fetch
    try {
      expect(blockedReasonMessage('SYNC_ACTIVE')).toBe('Já existe uma sincronização em andamento.')
      expect(safeResultMessage('LISTING_WRITE_CONFLICT')).toBe('Conflito ao gravar este anúncio. Tente novamente.')
      expect(calls).toEqual([])
    } finally {
      if (fetchSpy) globalThis.fetch = fetchSpy
    }
  })
})
