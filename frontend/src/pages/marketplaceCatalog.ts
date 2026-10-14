/**
 * S8C.2 C3 — the single pt-BR catalog for Marketplace listing-sync states, codes and guidance
 * (docs/S8C2-IMPLEMENTATION-HANDOFF.md, "C3 — pt-BR catalog"; ADR-0025 §J; ADR-0027).
 *
 * Pure data and pure functions: no React, no fetch, no session, no timers. C4 (Marketplace
 * Accounts sync panel) and C5 (Published Items) import from here; neither may carry its own copy.
 *
 * The texts are the frozen handoff table verbatim. Several backend fields share one vocabulary
 * of safe codes but stay different authorities, so they get different maps and different
 * functions and must not be mixed:
 *
 *   blockedReason   why starting a sync is blocked now          -> blockedReasonMessage
 *   status          where a run is in its lifecycle             -> runStatusLabel
 *   health          resource-level listing-sync health          -> syncHealthLabel
 *   safeResultCode  what a run or item outcome was              -> safeResultMessage
 *   requiredAction  the explicit human action needed (ADR-0027) -> requiredActionMessage
 *   linkageSource   how a listing was linked                    -> linkageSourceLabel
 *
 * A code without a frozen text renders as the code itself (`labels[code] ?? code`). Nothing here
 * ever shows provider text, an exception message, or a new invented sentence. Codes that are not
 * public contract (for example the aggregate's internal SKU_MAPPING_NOT_ACTIVE, removed by
 * ADR-0028: an idempotent replay answers 204) deliberately have no entry.
 */

type Catalog<K extends string> = Readonly<Record<K, string>>

const freeze = <K extends string>(catalog: Record<K, string>): Catalog<K> => Object.freeze({ ...catalog })

/** Returns the frozen text, or the code itself when the catalog has none. Own keys only. */
function lookup(catalog: Readonly<Record<string, string>>, code: string | null | undefined): string {
  if (code === null || code === undefined) return ''
  return Object.hasOwn(catalog, code) ? catalog[code] : code
}

// ---- blockedReason (ADR-0025 §J catalog) ----

export const BLOCKED_REASON_MESSAGES = freeze({
  ACCOUNT_INACTIVE: 'Conta inativa. Ative a conta para sincronizar.',
  CHANNEL_INVALID: 'O canal de vendas desta conta está inativo ou inválido.',
  PROVIDER_NOT_CONFIGURED: 'Este provedor não está configurado neste ambiente.',
  CAPABILITY_UNSUPPORTED: 'Leitura de anúncios não é suportada para este provedor.',
  GRANT_UNKNOWN: 'Permissão de leitura de anúncios ainda não verificada. Use "Sondar disponibilidade".',
  GRANT_DENIED: 'O provedor negou a leitura de anúncios. Verifique a conta no provedor.',
  AUTHORIZATION_NOT_USABLE: 'A autorização desta conta não está utilizável. Siga a ação indicada na conta.',
  RUNTIME_UNAVAILABLE: 'O provedor está indisponível para esta conta. Use "Sondar disponibilidade".',
  // `{hora}` is replaced from `retryAfterUntil` by blockedReasonMessage / safeResultMessage.
  RATE_LIMITED: 'Limite de requisições do provedor atingido. Tente novamente após {hora}.',
  SYNC_ACTIVE: 'Já existe uma sincronização em andamento.',
})
export type BlockedReasonCode = keyof typeof BLOCKED_REASON_MESSAGES

// ---- run status / health ----

export const RUN_STATUS_LABELS = freeze({
  QUEUED: 'Na fila',
  RUNNING: 'Em andamento',
  SUCCEEDED: 'Concluída',
  PARTIAL: 'Concluída com pendências',
  FAILED: 'Falhou',
})
export type RunStatusCode = keyof typeof RUN_STATUS_LABELS

export const SYNC_HEALTH_LABELS = freeze({
  NEVER_RUN: 'Nunca sincronizada',
  IN_PROGRESS: 'Em andamento',
  HEALTHY: 'Anúncios em dia',
  ATTENTION_REQUIRED: 'Requer atenção',
})
export type SyncHealthCode = keyof typeof SYNC_HEALTH_LABELS

// ---- safeResultCode: per-item results ----

export const ITEM_RESULT_MESSAGES = freeze({
  LISTING_NOT_FOUND_AT_PROVIDER: 'Anúncio não encontrado no provedor (último estado conhecido mantido).',
  LISTING_ACCESS_DENIED: 'O provedor negou acesso a este anúncio.',
  LISTING_SELLER_MISMATCH: 'O anúncio pertence a outro vendedor no provedor. Verifique antes de sincronizar novamente.',
  LISTING_RESPONSE_INVALID: 'Resposta do provedor inválida para este anúncio.',
  LISTING_STATUS_UNMAPPED: 'Status do provedor não reconhecido; anúncio não atualizado.',
  LISTING_READ_FAILED: 'Falha ao ler este anúncio.',
  LISTING_READ_REJECTED: 'O provedor rejeitou a leitura deste anúncio.',
  LISTING_WRITE_CONFLICT: 'Conflito ao gravar este anúncio. Tente novamente.',
  RUN_ENDED_BEFORE_ITEM_PROCESSED: 'A sincronização terminou antes de ler este anúncio.',
})
export type ItemResultCode = keyof typeof ITEM_RESULT_MESSAGES

// ---- safeResultCode: run stop codes ----

export const RUN_STOP_MESSAGES = freeze({
  RUN_EXECUTION_ATTEMPTS_EXHAUSTED: 'Execução interrompida repetidamente. Inicie uma nova sincronização.',
  LISTING_ENUMERATION_INCOMPLETE: 'Não foi possível listar todos os anúncios do provedor.',
  LISTING_VOLUME_EXCEEDS_LIMIT: 'Volume de anúncios acima do limite configurado.',
  PROVIDER_CREDENTIAL_REJECTED: 'Credencial recusada pelo provedor. Reautorize a conta.',
  PROVIDER_ACCESS_FORBIDDEN: 'O provedor recusou o acesso. Verifique a conta no provedor.',
  PROVIDER_UNAVAILABLE: 'Provedor indisponível.',
  PROVIDER_RATE_LIMITED: 'Limite de requisições do provedor atingido.',
  CREDENTIAL_STORE_UNAVAILABLE: 'Armazenamento de credenciais indisponível. Contate o administrador.',
  PROVIDER_APP_CREDENTIALS_INVALID: 'Credenciais do aplicativo VERCE inválidas. Contate o administrador.',
})
export type RunStopCode = keyof typeof RUN_STOP_MESSAGES

// ---- refusals of a start/retry request ----

export const SYNC_REQUEST_MESSAGES = freeze({
  LISTING_SYNC_NOTHING_TO_RETRY: 'Não há itens com erro que possam ser tentados novamente.',
})
export type SyncRequestCode = keyof typeof SYNC_REQUEST_MESSAGES

// ---- SKU mapping refusals ----

export const SKU_MAPPING_ERROR_MESSAGES = freeze({
  SKU_MAPPING_CONFLICT: 'Já existe um mapeamento ativo para este SKU.',
  SKU_MAPPING_PRODUCT_INELIGIBLE: 'Produto inexistente ou inativo.',
  SKU_MAPPING_SKU_INVALID: 'SKU inválido.',
})
export type SkuMappingErrorCode = keyof typeof SKU_MAPPING_ERROR_MESSAGES

// ---- linkageSource ----

export const LINKAGE_SOURCE_LABELS = freeze({
  MANUAL: 'Vinculado manualmente',
  DETERMINISTIC_SKU: 'Vinculado automaticamente por mapeamento de SKU confirmado',
})
export type LinkageSourceCode = keyof typeof LINKAGE_SOURCE_LABELS

// ---- requiredAction (S8C.1 connection guidance, reused by ADR-0027 for both listing-sync surfaces) ----
// The texts are the ones MarketplaceAccountsPage.tsx already renders; C4 replaces its inline copy with this
// export. NONE renders nothing.

export const REQUIRED_ACTION_MESSAGES = freeze({
  AUTHORIZE: 'Conecte esta conta autorizando com o provedor.',
  REAUTHORIZE: 'Autorização não é mais válida. Reautorize com o provedor.',
  RECONNECT_EXISTING_ACCOUNT: 'Uma nova autorização identificou esta MESMA conta no provedor. Reconecte explicitamente esta conta em vez de criar uma nova.',
  REAUTHORIZE_AFTER_STORE_LOSS: 'A credencial protegida local não pôde ser confirmada (perda de armazenamento/chave). Reautorize para gerar uma nova credencial; a conta e o histórico são preservados.',
  CHECK_PROVIDER_ACCOUNT: 'O provedor negou uma capacidade ou uma operação falhou. Verifique a conta diretamente no provedor.',
  CONTACT_ADMIN: 'Contate o administrador: configuração ou armazenamento de credenciais indisponível.',
  NONE: '',
})
export type RequiredActionCode = keyof typeof REQUIRED_ACTION_MESSAGES

// ---- retryAfterUntil -> {hora} ----

/** An ISO-8601 date-time that carries an explicit offset. A value without one is refused: it would be read in an assumed zone. */
const ISO_WITH_OFFSET = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-](\d{2}):?(\d{2}))$/i

/** `new Date` silently rolls 2026-02-31 over to March, which would print a time nobody sent: check the calendar and the clock ourselves. */
function isRealInstant(match: RegExpExecArray): boolean {
  const [year, month, day, hour, minute, second, offsetHour, offsetMinute] = match.slice(1).map((value) => (value === undefined ? 0 : Number(value)))
  if (month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59 || offsetHour > 23 || offsetMinute > 59) return false
  return day >= 1 && day <= new Date(Date.UTC(year, month, 0)).getUTCDate()
}

export interface RetryAfterFormatOptions {
  /** IANA zone for the rendering; defaults to the viewer's local zone. Tests pin it for determinism. */
  readonly timeZone?: string
}

/**
 * Formats the instant of `retryAfterUntil` as `dd/MM/yyyy HH:mm` (pt-BR, 24 h, viewer's zone). Returns null when the
 * value is absent, not an offset-qualified ISO-8601 date-time, or not a real instant, so callers never print a
 * made-up time, `Invalid Date` or `undefined`. Assembled from date parts so the shape does not depend on the ICU version.
 */
export function formatRetryAfter(retryAfterUntil: string | null | undefined, options: RetryAfterFormatOptions = {}): string | null {
  if (typeof retryAfterUntil !== 'string') return null
  const match = ISO_WITH_OFFSET.exec(retryAfterUntil.trim())
  if (match === null || !isRealInstant(match)) return null
  const instant = new Date(retryAfterUntil.trim())
  if (Number.isNaN(instant.getTime())) return null
  let parts: Intl.DateTimeFormatPart[]
  try {
    parts = new Intl.DateTimeFormat('pt-BR', {
      timeZone: options.timeZone,
      year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
    }).formatToParts(instant)
  } catch {
    return null // an unsupported time zone name
  }
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value
  const day = part('day'), month = part('month'), year = part('year'), hour = part('hour'), minute = part('minute')
  if (!day || !month || !year || !hour || !minute) return null
  return `${day}/${month}/${year} ${hour}:${minute}`
}

/** The sentence carrying `{hora}`, removed whole when no time is known ("Tente novamente após {hora}."). */
const HORA_SENTENCE = /\s*[^.]*\{hora\}\.?/

function withRetryAfter(template: string, retryAfterUntil: string | null | undefined, options?: RetryAfterFormatOptions): string {
  if (!template.includes('{hora}')) return template
  const formatted = formatRetryAfter(retryAfterUntil, options)
  return formatted === null ? template.replace(HORA_SENTENCE, '').trim() : template.replace('{hora}', formatted)
}

// ---- public lookups ----

/** blockedReason of the listing-sync availability (or of a refused start). `retryAfterUntil` fills `{hora}` for RATE_LIMITED. */
export function blockedReasonMessage(code: string | null | undefined, retryAfterUntil?: string | null, options?: RetryAfterFormatOptions): string {
  const text = lookup(BLOCKED_REASON_MESSAGES, code)
  return code !== null && code !== undefined && Object.hasOwn(BLOCKED_REASON_MESSAGES, code) ? withRetryAfter(text, retryAfterUntil, options) : text
}

export const runStatusLabel = (code: string | null | undefined): string => lookup(RUN_STATUS_LABELS, code)

/** Health is the server's derived value; this only labels it. The UI never computes health. */
export const syncHealthLabel = (code: string | null | undefined): string => lookup(SYNC_HEALTH_LABELS, code)

/**
 * `safeResultCode` of a run, or `safeErrorCode` of a run item: item results, run stop codes, and the guard codes a run can
 * stop with (which share the blockedReason texts, ADR-0027 §4). Anything else renders as the code itself.
 */
export function safeResultMessage(code: string | null | undefined, retryAfterUntil?: string | null, options?: RetryAfterFormatOptions): string {
  if (code === null || code === undefined) return ''
  if (Object.hasOwn(ITEM_RESULT_MESSAGES, code)) return ITEM_RESULT_MESSAGES[code as ItemResultCode]
  if (Object.hasOwn(RUN_STOP_MESSAGES, code)) return RUN_STOP_MESSAGES[code as RunStopCode]
  return blockedReasonMessage(code, retryAfterUntil, options)
}

/** Refusal code of a start/retry request (e.g. a retry with nothing to retry). */
export const syncRequestMessage = (code: string | null | undefined): string => lookup(SYNC_REQUEST_MESSAGES, code)

export const skuMappingErrorMessage = (code: string | null | undefined): string => lookup(SKU_MAPPING_ERROR_MESSAGES, code)

/** `linkageSource` of a LINKED listing. A null source (not linked) renders nothing. */
export const linkageSourceLabel = (code: string | null | undefined): string => lookup(LINKAGE_SOURCE_LABELS, code)

/** RequiredAction guidance text (empty for NONE). Which role sees it, and whether it is a button, is the page's decision (ADR-0027 §9). */
export const requiredActionMessage = (code: string | null | undefined): string => lookup(REQUIRED_ACTION_MESSAGES, code)
