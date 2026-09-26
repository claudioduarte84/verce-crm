# ADR-0025 — S8C.2 Mercado Livre Listing Read Integration and Normalized Observations

- **Status:** Accepted — approved and frozen; implementation not started
- **Date:** 2026-09-25 (consolidated fix pass for Sol gate findings G01–G06, same date)
- **Sprint:** S8C.2.ARCH
- **Baseline:** `01e5d6a68cce2e83744e5c280faab903afcdc4cd` (S8C.1 marketplace connector foundation)
- **Builds on:** [ADR-0023](ADR-0023-s8b-commerce-foundation.md) (Commerce model),
  [ADR-0024](ADR-0024-s8c1-marketplace-connector-authorization-foundation.md) (authorization,
  credentials, operation arbiter, G-08 HTTP policy). Supersedes neither and **amends neither**:
  every ADR-0024 rule, including its failure-certainty semantics, stays normative. This ADR resolves
  the G-08 HTTP-layer deferral recorded in [ARCHITECTURE-DEBT](../ARCHITECTURE-DEBT.md).
- **Handoff:** [S8C2-IMPLEMENTATION-HANDOFF.md](../S8C2-IMPLEMENTATION-HANDOFF.md)
- **Review record:** S8C2-G01: GPT-6 Astra reviewed — **G01_CLOSED** (§F.2 incorporates its rule
  verbatim in substance). G01–G06 and D12 scanner self-test architecture: independent Codex Sol
  final regates **APPROVED** on 2026-09-26. Further Astra review was not required.

## Context

S8C.1 shipped a provider-neutral authorization foundation exercised only by a test fake. S8C.2
connects the first real provider, Mercado Livre Brazil (site `MLB`), for **listing reads only**:
authorize a seller, enumerate the seller's listings, read their details, normalize the facts into
the existing `MarketplaceListing`/`MarketplaceListingObservation` model and keep manual
product/offer reconciliation authoritative. Nothing is written to the provider.

Repository truth at the baseline:

- Commerce defines `IMarketplaceAuthorizationConnector` (Begin/CompleteOnce/Refresh/Inspect),
  `IMarketplaceConnectorRegistry`, `IProtectedCredentialStore` and
  `MarketplaceRequestNotSentException` (`MarketplaceConnectorContracts.cs`).
- `Verce.Infrastructure.Marketplaces` holds `MarketplaceAuthorizationWorkflow`, `ProviderExecutionGate`,
  `LocalProtectedCredentialStore` and `MarketplaceConnectorRegistry`. The local store and registry
  exist only in `Development` with `Marketplaces:CredentialStoreRoot`; Production registers
  `UnsupportedProtectedCredentialStore` and fails startup if any connector is registered.
- No `IHttpClientFactory` usage exists in `src/`.
- `MarketplaceListing` enforces the ADR-0023 linkage CHECKs, but always appends a MANUAL `initial:`
  observation, compares consecutive fingerprints with the load-order-dependent
  `_observations.LastOrDefault()`, never clears a removed SKU, has no `ListingUrl` setter, and its
  observation `ProviderObservedAt` is `NOT NULL` although DATA-MODEL declares it nullable.
- Provider capability seeds are all `UNKNOWN`.

Three S8C.1 **implementation defects under the existing ADR-0024** (no amendment) become reachable
with a real rotating-token provider and are fixed first (§A.0):

| ID | Defect | ADR-0024 rule it violates |
|---|---|---|
| D-01 | `AccessExpiresAt` exists on the connection but the expiry returned by callback/refresh is never persisted, so every use would refresh | §4 "commit confirmed version/expiry" |
| D-02 | Refresh maps every failure, including a pre-send failure, to `REAUTHORIZATION_REQUIRED`; there is no same-R2 persistence retry | §4 outcome table |
| D-03 | Probe writes grant source `PROBE` instead of `PROBE_INSPECTION` and ignores persisted `RuntimeRetryAfterUntil` | §5, §6, DATA-MODEL grant metadata |

## Official evidence recheck (access date 2026-09-25)

Only official Mercado Livre developer pages were used. `WebFetch` returned HTTP 403, so the same
official URLs were retrieved with `curl` using a browser user agent and read in full. Each page
date is the "last updated" date that page shows.

| ID | Official page | Page date | Facts frozen from it |
|---|---|---|---|
| ML-AUTH | [Autenticação e Autorização](https://developers.mercadolivre.com.br/pt_br/autenticacao-e-autorizacao) | 29/12/2025 | Server-side authorization-code grant; BR URL `https://auth.mercadolivre.com.br/authorization?response_type=code&client_id=…&redirect_uri=…&state=…`; `redirect_uri` must match the registered value exactly with no variable data; `state` is recommended and not validated by ML; PKCE optional, mandatory only if enabled on the app; exchange `POST https://api.mercadolibre.com/oauth/token` form `grant_type=authorization_code, client_id, client_secret, code, redirect_uri`; refresh `grant_type=refresh_token, client_id, client_secret, refresh_token`; response `access_token, token_type=bearer, expires_in=21600, scope, user_id, refresh_token`; access token 6 h; refresh token single-use, only the latest generated one accepted, rotated on each refresh, 6-month lifetime; `Authorization: Bearer`; errors `invalid_client, invalid_grant, invalid_scope, invalid_request, unsupported_grant_type, forbidden (403), local_rate_limited (429), unauthorized_client, unauthorized_application`, operator users `invalid_operator_user_id`; password change, secret change, revocation or 4 months of inactivity invalidate tokens |
| ML-USERS | [Consulta de usuários](https://developers.mercadolivre.com.br/pt_br/consulta-de-usuarios), [Usuários e Aplicativos](https://developers.mercadolivre.com.br/pt_br/usuarios-e-aplicativos) | 30/12/2025 | `GET /users/me` returns `id`, `site_id` and PII (email, identification, address) that VERCE discards; no per-user functional-permission inspection resource |
| ML-PERM | [Permissões funcionais](https://developers.mercadolivre.com.br/pt_br/permissoes-funcionais) | 21/11/2025 | read-only scope allows GET; "Publicação e sincronização" covers items resources |
| ML-ITEMS | [Busca de itens](https://developers.mercadolivre.com.br/pt_br/itens-e-buscas) | 31/08/2026 | `GET /users/{user_id}/items/search` lists the seller account's item IDs (`results`); default limit 50, max 100; offset paging capped at 1000; `search_type=scan` returns a `scroll_id` that **expires in 5 minutes**, sent on each continuation until the end; offset/limit must not be mixed with scroll; documented status filter values `pending, not_yet_active, programmed, active, paused, closed`; public resources return **ranged** `available_quantity` reference values; `/items?ids=` multi-get (max 20) is discontinued by **25/10/2026** in favor of `/items/bulk?ids=` with per-element `status_code`, root `id`, `body` and `attributes=body.<field>`; search "does not replace item notifications". **The page states no maximum for `/items/bulk`.** |
| ML-PUBLISH | [Publicar produtos](https://developers.mercadolivre.com.br/pt_br/publicacao-de-produtos) | 09/01/2026 | item body fields `id, site_id, title, seller_id, price, currency_id, status, sub_status, permalink, attributes[], variations[], last_updated, seller_address…` |
| ML-SYNC | [Sincronização e modificação de publicações](https://developers.mercadolivre.com.br/pt_br/produto-sincronizacao-de-publicacoes) | 24/03/2026 | states `payment_required, under_review (warning, waiting_for_patch, held, pending_documentation, forbidden), paused (out_of_stock, paused_by_seller, picture pending), closed (expired, deleted, suspended, freezed…), inactive`; "after some time finished items are no longer shown for your query" — **no HTTP status is specified for that case** |
| ML-VAR | [Variações](https://developers.mercadolivre.com.br/pt_br/variacoes) | 29/12/2025 | SKU belongs in attribute `SELLER_SKU`; `seller_custom_field` is internal and unrelated; variations carry their own `SELLER_SKU` |
| ML-UP | [User Products](https://developers.mercadolivre.com.br/pt_br/user-products) | 17/06/2026 | `user_product_id`; migrations may create new `item_id`s |
| ML-RATE | [Rate limit / Erro 429](https://developers.mercadolivre.com.br/pt_br/usuarios-e-aplicativos/rate-limit-erro-429) | 05/05/2026 | exponential backoff with jitter, reduce concurrency, consume the scroll completely before expiry; limit applied mainly **per Client ID and per endpoint**; no numeric quota; no `Retry-After` statement |
| ML-NOTIFY | [Notificações](https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes) | 14/09/2026 | `items` topic is a resource reference; 200 within 500 ms; retries for 1 hour; `missed_feeds` keeps 2 days |
| ML-TEST | [Realização de testes](https://developers.mercadolivre.com.br/pt_br/realizacao-de-testes) | 30/12/2025 | **No sandbox**; test users act in production; `POST /users/test_user`; at most 10; removed after 60 days of inactivity; test listings titled "Item de Teste – Por favor, NÃO OFERTAR!", category "Outros", not gold/gold_premium |

`UNKNOWN` (official pages silent) — recoverable behavior designed, no value invented:

| Unknown | Design consequence |
|---|---|
| `/items/bulk` maximum IDs per call | Application batch size **20 is a VERCE assumption, not a provider maximum**; proven only by live check L-07 (§K) |
| Whether the 5-minute scroll lifetime is fixed or renewed per call | No lifetime arithmetic; a failed continuation restarts enumeration (§E.3) |
| Ordering/duplicates of `search_type=scan` | No ordering assumption; run items are unique per run |
| Whether unfiltered search returns paused/closed items | Known listings absent from enumeration are re-read by ID (§E.5); live check L-09 |
| Meaning of a per-element 404 in `/items/bulk` | `NOT_FOUND` is **blocking**, never a definitive negative (§E.6); live check L-11 |
| Expired scroll error shape | Any non-401/403/429 4xx on a continuation = cursor invalid |
| Owner-token quantity exactness | Quantity not stored (§B.4) |
| `Retry-After` on 429 | Optional diagnostic/backoff fact |
| Numeric quotas | None invented (§F.5) |
| Denial redirect parameters | Callback without `code`/`state` never reaches the provider (§A.4) |
| Whether a new authorization invalidates the previous refresh token | ML-AUTH: only the latest refresh token is usable ⇒ impact `MAY_SUPERSEDE_EXISTING` |
| `localhost` redirect registration | Live check L-01 |
| Provider request-ID header | `provider_request_id` always null for ML |

Shopee BR and TikTok Shop BR `LISTINGS_READ` remain **UNKNOWN**; no Shopee or TikTok adapter exists
in S8C.2.

## Decision

### A. Mercado Livre authorization adapter (existing S8C.1 port)

**A.0 Order of work.** D-01, D-02 (per §F.2) and D-03 are fixed and the full S8C.1 regression is
re-run green **before** any real ML HTTP client or configuration is enabled (handoff A1–A4).

**A.1 Placement.** `MercadoLivreConnector` (namespace `Verce.Infrastructure.Marketplaces.MercadoLivre`)
implements `IMarketplaceAuthorizationConnector` and `IMarketplaceListingReader` (§B.1),
`ProviderCode = "MERCADO_LIVRE"`, internal DTOs. Registered (one singleton instance for both service
types) only in the existing local-store branch (`Development` + `Marketplaces:CredentialStoreRoot`)
**and** `Marketplaces:MercadoLivre:Enabled = true`. `Enabled = true` in any other composition fails
startup with `MARKETPLACE_REAL_PROVIDER_REQUIRES_CERTIFIED_STORE`. No ML session table, token table,
refresh lock or connection state: all credentials flow through the S8C.1 session, operation arbiter,
protected store, single-flight refresh, provider fence and K1/K2 recovery.

**A.2 Application credential versus seller credential.**

| Material | Where | Never |
|---|---|---|
| App ID `client_id` | `Marketplaces:MercadoLivre:ClientId` (User Secrets/env, not committed) | account column, frontend, Git |
| Client secret | Development: User Secrets `Marketplaces:MercadoLivre:ClientSecret`; production-grade delivery: `Marketplaces:MercadoLivre:ClientSecretFile` (absolute path to a mounted `0400` file, read once at startup); both set = startup error | appsettings*.json, Git, logs, DTOs, audit, database |
| Redirect URI | `Marketplaces:MercadoLivre:RedirectUri` | client-supplied |
| Seller access/refresh tokens | S8C.1 `IProtectedCredentialStore` only | anywhere else |
| Seller `user_id` | `MarketplaceAccount.ExternalAccountId` | untrusted input |

Startup validation when enabled: ClientId `^[0-9]{1,32}$`; secret 1..512 printable ASCII; RedirectUri
absolute `https`, no query/fragment, path exactly
`/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback`, origin equal to the origin serving
`/api` to the browser (the `__Host-` binding cookie origin). Messages name keys, never values.
Options are never logged. `appsettings.json` holds only `Enabled=false` and numeric defaults.

**A.3 Authorization URL.** Constant host
`https://auth.mercadolivre.com.br/authorization?response_type=code&client_id={ClientId}&redirect_uri={RedirectUri}&state={state}`,
values URL-encoded; the workflow-supplied relative redirect path must equal the fixed callback path.
**PKCE is not used** (optional per ML-AUTH; the app is registered with PKCE disabled; a verifier needs
protected transient material the S8C.1 store does not offer and ADR-0024 rejected encrypted blobs in
Commerce). Protection: confidential server-side secret, 32-byte hashed single-use state, browser
binding cookie, one-time CLAIMED transition. PKCE is a debt row.

**A.4 Callback.** ADR-0024 §2 unchanged. `state` and `code` bind as optional; if either is absent the
workflow is not invoked, the session is unchanged (it expires), the browser goes to
`/marketplace-accounts?authorization=failed`. `error`, `error_description` and any other field are
never read, stored or logged.

**A.5 Code exchange (CompleteOnce).** One `POST /oauth/token` (form, `Accept: application/json`),
never retried, 20 s deadline, send-certainty per §F.2. A usable 2xx requires `access_token`
(1..4096), `token_type` `bearer` (ordinal-ignore-case), `user_id` (JSON integer, invariant rendering,
`^[1-9][0-9]{0,19}$`), `refresh_token` (1..4096) and `expires_in` (positive integer). Anything else is
a malformed 2xx ⇒ `MarketplaceProviderCallException` (SENT_OR_UNKNOWN) and the ADR-0024 callback
fail-closed path. `AccessExpiresAt = sendInvokedAt + expires_in` (`IClock` read immediately before
`SendAsync`). `scope` is discarded (scope never proves a grant). Then, in adapter memory with the new
token and the safe-read policy (§F.4):

1. `GET /users/me` (required): `id == user_id` and `site_id == "MLB"`; only those two fields are read,
   the rest is discarded unparsed. Failure/mismatch ⇒ `MarketplaceProviderCallException` ⇒ ADR-0024
   fail-closed path.
2. `GET /users/{user_id}/items/search?limit=1` (grant evidence): 200 ⇒ `LISTINGS_READ = GRANTED`;
   anything else ⇒ `UNKNOWN` (403 is ambiguous per ML-AUTH; the ML adapter never reports `DENIED`).
   Other seven capabilities `UNKNOWN`.

`ExternalAccountId = user_id`; `CredentialImpact = MAY_SUPERSEDE_EXISTING`. The port record
`MarketplaceAuthorizationCompletion` gains `DateTimeOffset? AccessExpiresAt` (D-01).

**A.6 Secret material.** Adapter-owned UTF-8 JSON v1
`{"v":1,"access_token":"…","refresh_token":"…","user_id":"…"}`; never logged; decoded buffers cleared
in `finally`. Expiry lives in `marketplace_account_connection.access_expires_at`.

**A.7 Refresh.** One `POST /oauth/token` `grant_type=refresh_token`, never retried, 20 s deadline;
outcomes exactly per §F.2/§F.3. S8C.1 protocol unchanged: account single-flight, REFRESH operation
committed and advanced to `EXTERNAL_IN_FLIGHT` before `SendAsync`, CAS `V → V+1`, confirmation with
`access_expires_at`/`last_refresh_at` in one transaction, PostgreSQL terminal authority, K1/K2.
`RefreshAsync` gains `force` (after a 401) that refreshes even when `access_expires_at > now`, only if
the connection's `ConfirmedOperationId` still equals the operation whose token got the 401.
`ResolvePendingAsync` gains a safe-code parameter so §F.3 codes are recorded instead of the generic
`OPERATION_RECOVERY_FAIL_CLOSED`.

**A.8 Proactive refresh.** A token is used only if
`access_expires_at > now + Marketplaces:MercadoLivre:AccessTokenRefreshMarginSeconds` (default 300,
0..3600; a VERCE default); otherwise the single-flight refresh runs first.

**A.9 Probe.** `InspectAsync` runs A.5 steps 1–2 with the confirmed token; identity must equal the
account's `ExternalAccountId` (mismatch ⇒ `PROBE_IDENTITY_MISMATCH` + `REAUTHORIZATION_REQUIRED`).
`ProbeAsync` uses the credential executor (§G.3), honors `RuntimeRetryAfterUntil` (503 with
`Retry-After`) and writes source `PROBE_INSPECTION` (D-03).

**A.10 Revoke.** None provider-side in S8C.2; disconnect stays local-first.

### B. LISTINGS_READ contract

**B.1 Provider-neutral port** (Commerce, `MarketplaceListingContracts.cs`):

```csharp
public interface IMarketplaceListingReader
{
    string ProviderCode { get; }
    Task<MarketplaceListingPage> ReadListingPageAsync(MarketplaceListingPageRequest request, CancellationToken ct);
    Task<IReadOnlyList<MarketplaceListingDetailResult>> ReadListingDetailsAsync(MarketplaceListingDetailRequest request, CancellationToken ct);
}
public abstract class MarketplaceListingCursor { }   // opaque, in-memory only; never persisted/serialized
public sealed record MarketplaceListingPageRequest(Guid AccountId, string ExternalAccountId, ReadOnlyMemory<byte> SecretMaterial, MarketplaceListingCursor? Cursor);
public sealed record MarketplaceListingPage(IReadOnlyList<string> ExternalListingIds, MarketplaceListingCursor? NextCursor, bool EndOfEnumeration, int RejectedIdentifierCount);
public sealed record MarketplaceListingDetailRequest(Guid AccountId, string ExternalAccountId, ReadOnlyMemory<byte> SecretMaterial, IReadOnlyList<string> ExternalListingIds);
public enum MarketplaceListingReadOutcome { FOUND, NOT_FOUND, ACCESS_DENIED, SELLER_MISMATCH, INVALID_RESPONSE, UNMAPPED_STATUS, FAILED }
public sealed record MarketplaceListingDetailResult(string ExternalListingId, MarketplaceListingReadOutcome Outcome, NormalizedListingFacts? Facts, string? ProviderErrorCode);
public sealed record NormalizedListingFacts(string ExternalListingId, string? ExternalSku, string? Title, decimal? ObservedPrice,
    MarketplaceListingStatus ObservedStatus, string? ProviderNativeStatus, int VariationCount,
    string? ListingUrl, DateTimeOffset? ProviderObservedAt, IReadOnlyList<string> NormalizationWarnings);
```

The batch size is not part of the port: the worker chunks detail requests with
`Marketplaces:ListingSync:DetailBatchSize` (default 20, range 1..20). Call failures throw
`MarketplaceProviderCallException` (§F.1). The registry gains `TryGetListingReader`; a reader
without a same-code authorization connector fails startup.

**B.2 Enumeration.** Only `GET https://api.mercadolibre.com/users/{ExternalAccountId}/items/search`:
first call `?search_type=scan&limit=100`, then `?search_type=scan&limit=100&scroll_id={latest}` where
`{latest}` is the scroll_id of the immediately preceding response. No status filter, offset or sort.
IDs validated `^MLB[0-9]{1,20}$` (others dropped, counted). End: empty `results` or null/absent
`scroll_id`. `paging.total` informational. No ordering assumed. Local bounds (VERCE defaults):
three consecutive pages adding no new ID ⇒ treated as cursor invalid; more than
`MaxListingsPerRun` (200000) run items ⇒ run `FAILED/LISTING_VOLUME_EXCEEDS_LIMIT`. A continuation
answered with a 4xx other than 401/403/429 throws `LISTING_ENUMERATION_CURSOR_INVALID`. The scroll_id
lives only in the in-memory cursor; never persisted, logged or returned.

**B.3 Detail.** Only `GET https://api.mercadolibre.com/items/bulk?ids={comma-separated}&attributes=body.id,body.seller_id,body.site_id,body.title,body.price,body.currency_id,body.status,body.sub_status,body.permalink,body.last_updated,body.attributes,body.variations`
(never the deprecated `/items?ids=`). Element results: `status_code` 200 with a valid body ⇒ FOUND
(or `UNMAPPED_STATUS` when `status` is absent or not in §C.2); 404 ⇒ `NOT_FOUND`; 403 ⇒
`ACCESS_DENIED`; `seller_id` ≠ `ExternalAccountId` ⇒ `SELLER_MISMATCH` (facts discarded); body `id` ≠
requested ID, missing `seller_id`, `site_id` present and ≠ `MLB`, or unparseable body ⇒
`INVALID_RESPONSE`; any other element code or a requested ID missing from the response ⇒ `FAILED`.

| Normalized field | Source | Rule | Null means |
|---|---|---|---|
| ExternalListingId | `body.id` | must equal requested ID | — |
| ExternalSku | `attributes[id="SELLER_SKU"].value_name` | NFC, trimmed, control chars removed, 1..200; several different values ⇒ null + `SKU_AMBIGUOUS`; `seller_custom_field` never used | no authoritative SKU |
| Title | `title` | trimmed, control chars removed, cut to 500 UTF-16 units surrogate-safe (`TITLE_TRUNCATED`) | absent |
| ObservedPrice | `price`, `currency_id` | JSON `decimal` (never `double`); only BRL, > 0, scale ≤ 2; else null + `PRICE_CURRENCY_UNSUPPORTED`/`PRICE_PRECISION_UNSUPPORTED`/`PRICE_NOT_POSITIVE` | unknown — never zero |
| ObservedStatus | `status` | §C.2 | — |
| ProviderNativeStatus | `status`, `sub_status[]` | lowercase `[a-z0-9_]` tokens, `status` or `status:sub1,sub2` (distinct, ordinal-sorted), ≤ 200 | absent |
| VariationCount | `variations` | array length, 0 when absent | never |
| ListingUrl | `permalink` | absolute http/https, host `mercadolivre.com.br` or `*.mercadolivre.com.br`, ≤ 2000; else null + `URL_REJECTED` | no safe URL |
| ProviderObservedAt | `last_updated` | ISO-8601 with offset → UTC; unparseable ⇒ null | unknown — never now |

Ignored and never persisted: all quantities, pictures, descriptions, shipping, seller address,
category, catalog/user-product IDs, variation details beyond the count, promotions, tags, health.

**B.4 Quantity.** Not stored. Ranged public values are indistinguishable from exact ones and
multi-origin stock lives elsewhere; S8C.2 is not inventory. UI: "Estoque: não sincronizado".

**B.5 Variations.** Count only, on listing and observation; no child tables.

### C. MarketplaceListing model

**C.1 Identity and writes.** Upsert by the existing unique `(marketplace_account_id,
external_listing_id)`, never by SKU. New domain methods (all invoked only inside lease-fenced
transactions, §E.2):

- `static ImportFromProvider(accountId, facts, runId, LinkageDecision, now)` — SYNCED, facts,
  linkage per `SKU-LINK-1`, one PROVIDER_SYNC observation; no MANUAL `initial:` row.
- `ApplyProviderFacts(facts, runId, now)` — replaces observed fields (a removed SKU/title/price
  becomes null), sets `ListingUrl`, `ProviderObservedAt`, `VariationCount`, `SyncState = SYNCED`,
  `SyncError = null`, `LastSyncAttemptAt = LastSuccessfulSyncAt = now`; appends an observation only if
  the fingerprint changed; returns `APPENDED | FRESHNESS_ONLY`; never touches Product, Offer, linkage
  state/source or mapping reference.
- `ApplyLinkageDecision(LinkageDecision)` — only for an existing `UNLINKED` listing with
  `AutoLinkSuppressed = false` (§D.4).
- `RecordProviderReadFailure(safeCode, now)` — `SyncState = ERROR`, `SyncError = safeCode`
  (`^[A-Z0-9_]{1,64}$`), `LastSyncAttemptAt = now`; facts, linkage, history untouched.
- Manual `ApplyObservation` keeps its S8B contract but compares against and maintains
  `LatestObservationFingerprint`.

**C.2 Status mapping (frozen).**

| ML `status` | Normalized | Note |
|---|---|---|
| `active` | ACTIVE | any sub_status stays ACTIVE; native shows it |
| `paused` | PAUSED | |
| `closed`, `inactive` | INACTIVE | sub_status preserved in native |
| `not_yet_active`, `programmed` | DRAFT | |
| `under_review`, `payment_required`, `pending` | ERROR | provider-reported blocking condition |
| absent, empty, anything else | **not normalized** | read outcome `UNMAPPED_STATUS` ⇒ run item `PERMANENT_ERROR/LISTING_STATUS_UNMAPPED`; no facts applied, no listing created |

Unknown never becomes ACTIVE nor any other normalized status.

**C.3 Listing sync state.** SYNCED on a FOUND read; ERROR with the item's safe code on any other
outcome for an existing listing; facts unchanged on ERROR.

**C.4 Concurrency.** A `DbUpdateConcurrencyException` (operator edited the listing) or unique
violation (concurrent manual `POST /published-items`) rolls the batch transaction back; the worker
discards the `DbContext` and re-applies the batch in a fresh fenced transaction, at most 3 times,
then marks the affected items `TRANSIENT_ERROR/LISTING_WRITE_CONFLICT`. An aborted transaction is
never continued (rule 19). Operator writes may get `CONCURRENCY_CONFLICT` 409 and reload.

**C.5 Added listing fields** (§H.3): `latest_observation_fingerprint`, `linkage_source`
(`MANUAL|DETERMINISTIC_SKU`, non-null iff LINKED), `sku_mapping_id` (non-null iff
`linkage_source = DETERMINISTIC_SKU`), `auto_link_suppressed`, `variation_count`.

### D. SKU authority and linkage

**D.1 Previous rule removed.** A manual listing link proves only "listing X → Product P". It never
becomes account-wide SKU authority. Neither provider observations nor manual links create mappings.

**D.2 Explicit mapping.** New aggregate root `MarketplaceAccountSkuMapping` (Commerce,
`commerce.marketplace_account_sku_mapping`, §H.4): "SKU S of account A means Product P", confirmed by
an operator action. `ExternalSku` is normalized exactly like §B.3 (NFC, trim, control characters
removed) and compared ordinal, case-sensitive. `ProductId` is a logical Catalog UUID (no physical FK);
confirmation validates through the composition-root port `ICommerceProductEligibilityReader`
(Commerce contract, implemented in `Verce.Api`) that the Product exists and is active. At most one
ACTIVE mapping per `(account, external_sku)` (filtered unique index).

**D.3 Lifecycle and invalidation.**

| Event | Effect on mapping | Effect on listings |
|---|---|---|
| Operator confirms S→P (none active) | new ACTIVE mapping, audited | future `SKU-LINK-1` evaluations only |
| Operator confirms S→P2 while S→P1 ACTIVE | rejected `SKU_MAPPING_CONFLICT` 409 unless the request names the active mapping ID and its Version (`replace`); then P1 mapping → INACTIVE `REPLACED_BY_NEW_MAPPING` and P2 created in one transaction | listings already linked to P1 unchanged |
| Operator deactivates | ACTIVE → INACTIVE `OPERATOR_DEACTIVATED`, `invalidated_at/by` set; never reactivated (confirm a new mapping instead) | historical links unchanged; auto-link stops |
| Product becomes inactive/missing | mapping unchanged (no background mutation; the Product may be reactivated) | evaluation yields NEEDS_REVIEW (§D.4) |
| Provider changes a listing's SKU | mapping unchanged | the listing keeps its link (manual or deterministic); the new SKU inherits nothing until explicitly confirmed |

A mapping controls future auto-link authority only; it never unlinks or relinks existing linked
listings.

**D.4 SKU-LINK-1** — evaluated in the fenced batch transaction for a listing being **created** by
provider import, or an existing listing that is `UNLINKED` with `auto_link_suppressed = false`,
using the read's normalized SKU S:

```text
S is null                                             -> UNLINKED (no change for existing)
M = ACTIVE mappings WHERE account = A AND external_sku = S
|M| = 0                                               -> UNLINKED (no change for existing)
|M| > 1 (legacy/corrupt state the unique index should prevent) -> NEEDS_REVIEW
|M| = 1 and Product(M.ProductId) exists and active and VariationCount <= 1
                                                      -> LINKED, ProductId = M.ProductId, ChannelOfferId = null,
                                                         LinkageSource = DETERMINISTIC_SKU, SkuMappingId = M.Id
|M| = 1 otherwise (product ineligible or VariationCount > 1) -> NEEDS_REVIEW
```

An operator `Unlink` sets `auto_link_suppressed = true`; an operator `Link` sets `MANUAL`, clears
`sku_mapping_id` and clears suppression. `NEEDS_REVIEW` and `LINKED` listings are never
re-evaluated. No Product is created; no Offer is created, guessed or linked. ADR-0023 linkage shapes
and CHECKs are unchanged.

**D.5 Permissions.** Confirm/replace/deactivate mapping: `commerce:manage` (Owner, Operator) —
the same authority as link/unlink. List: `commerce:read` (Owner, Operator, Viewer). Viewer never
mutates.

### E. Sync execution, fencing, run items and outcomes

**E.1 Execution model.** Runs are started explicitly by Owner/Operator (FULL) or by retry
(FAILED_ONLY). **No scheduled provider polling** (no official cadence/quota; search does not replace
notifications); **no notifications/webhooks**. Quartz (the repository scheduler, persistent store)
hosts `MarketplaceListingSyncDispatchJob` (`[DisallowConcurrentExecution]`), triggered immediately
after a run is committed and by an internal recurring trigger every
`Marketplaces:ListingSync:DispatchIntervalSeconds` (default 60) that only reads VERCE's run table.
The dispatcher claims due runs (QUEUED, or RUNNING with expired lease) up to
`MaxConcurrentRuns` (default 2) and hands each to `MarketplaceListingSyncExecutor`, a singleton hosted
service that runs each execution in its own DI scope. Different accounts therefore run independently.
ML-RATE documents the limit per Client ID/endpoint, so a process-wide per-provider in-flight cap
`MaxInFlightRequestsPerProvider` (default 2, range 1..8, a VERCE default, not a quota) bounds
throughput without serializing accounts. There is no RETRY_WAIT state: request-level retries are the
safe-read policy, item-level retries are FAILED_ONLY runs, crash recovery is reclaim.

**E.2 Lease fencing (G02).** Authority = `RunId + LeaseToken`.

- **Claim/reclaim** generates a **new** `lease_token` (UUID v7) every time, increments
  `attempt_count`, sets `lease_until = now + LeaseSeconds (120)` and `heartbeat_at = now`:
  ```sql
  UPDATE commerce.marketplace_listing_sync_run
     SET status = 'RUNNING', lease_token = @newToken, lease_until = @now + @lease, heartbeat_at = @now,
         attempt_count = attempt_count + 1, started_at = COALESCE(started_at, @now),
         confirmed_operation_id_at_start = @confirmedOperationId, version = version + 1
   WHERE id = @runId
     AND ((status = 'QUEUED') OR (status = 'RUNNING' AND lease_until < @now))
     AND attempt_count < max_attempts
  RETURNING id;
  ```
  An old token never becomes valid again; timestamps are never extended with a previous token.
- **Every run-owned write transaction starts with the guard**, as its first statement, in the same
  PostgreSQL transaction that writes the data:
  ```sql
  UPDATE commerce.marketplace_listing_sync_run
     SET heartbeat_at = @now, lease_until = @now + @lease
   WHERE id = @runId AND lease_token = @leaseToken AND status = 'RUNNING'
  RETURNING id;
  ```
  Zero rows ⇒ the worker is stale: roll back, abandon the execution, write nothing. The guard's row
  lock is held until commit, so a concurrent reclaim waits and then re-evaluates `lease_until`.
- **Fenced writes** (all of them): run-item inserts and outcome updates; listing insert/update;
  observation insert; listing sync state/error; `SKU-LINK-1` decisions; run phase/enumeration flag;
  terminal status; run-tied audit; connection runtime updates made by the run. No run-owned write
  exists outside a guarded transaction.
- **Lease-exhausted runs**: a RUNNING run with expired lease and `attempt_count >= max_attempts`
  (default 3) is terminalized by the dispatcher with a compare-and-set on the stale token it read:
  `… SET status='FAILED', safe_result_code='RUN_EXECUTION_ATTEMPTS_EXHAUSTED', lease_token=NULL,
  lease_until=NULL, finished_at=@now WHERE id=@runId AND lease_token=@observedStaleToken AND
  status='RUNNING' AND lease_until < @now`, and in the same transaction every PENDING item becomes
  `TRANSIENT_ERROR/RUN_ENDED_BEFORE_ITEM_PROCESSED`.
- Provider GETs performed by a stale worker are harmless reads; none of their results can commit.

**E.3 FULL run (per execution).**

1. **Guards** §G.2 (fresh read). Failure ⇒ terminal `FAILED` with the guard code.
2. **Enumerate** (`phase = ENUMERATING`, skipped if `enumeration_completed`): each page's IDs are
   inserted as run items (`origin = ENUMERATED`, `outcome = PENDING`) in one fenced transaction per
   page with `INSERT … ON CONFLICT (sync_run_id, external_listing_id) DO NOTHING` (never one giant
   transaction). On `LISTING_ENUMERATION_CURSOR_INVALID` restart the scan from the first page (items
   already inserted remain); at most 2 restarts per execution, then the run stops with
   `LISTING_ENUMERATION_INCOMPLETE`. Completion (`enumeration_completed = true`, fenced) only when one
   scan reached its end.
3. **Known-not-enumerated** (`phase = RECONCILING_MISSING`), only after completion: one fenced
   `INSERT … SELECT` (batched by 500 in keyset order of `external_listing_id`) adds items
   `origin = KNOWN_NOT_ENUMERATED` for every listing of the account with no item in this run
   (including S8B manual listings).
4. **Detail** (`phase = DETAILING`): PENDING items in keyset order `(origin rank, external_listing_id)`
   with rank `ENUMERATED = 0`, `RETRY_TARGET = 1`, `KNOWN_NOT_ENUMERATED = 2`, chunked by
   `DetailBatchSize`; one fenced transaction per chunk applies results (§E.4), sets
   `marketplace_listing_id` for FOUND, increments item `attempt_count`. `FAILED` element results are
   re-requested once in a later chunk of the same execution before becoming `TRANSIENT_ERROR`.
5. **Finish** (§E.6) in one fenced transaction.

After a restart/reclaim the execution resumes: completed enumeration is never rescanned; PENDING
items continue.

**Stop reasons.** A whole-call failure that survives the safe-read policy stops provider work for the
run: `RATE_LIMITED` ⇒ `PROVIDER_RATE_LIMITED` (persist `RuntimeRetryAfterUntil` when known);
`RETRYABLE`/`UNKNOWN` ⇒ `PROVIDER_UNAVAILABLE`; `AUTH_RENEWAL_REQUIRED` after one forced refresh ⇒
`PROVIDER_CREDENTIAL_REJECTED`; `USER_ACTION_REQUIRED` ⇒ `PROVIDER_ACCESS_FORBIDDEN`;
`STORE_UNAVAILABLE` ⇒ `CREDENTIAL_STORE_UNAVAILABLE`; a guard failure mid-run (account deactivated,
disconnected, grant/runtime change) ⇒ that guard's code. `NON_RETRYABLE` on a detail chunk marks
that chunk's items `PERMANENT_ERROR/LISTING_READ_REJECTED` and does not stop the run. On a stop, the
finish transaction turns all PENDING items into `TRANSIENT_ERROR` with the stop code.

**E.4 Run items (G03).** `commerce.marketplace_listing_sync_run_item` (§H.2) is the durable
per-run identity of every external ID the run must resolve, including IDs that have no listing row.
Outcome vocabulary (frozen, minimal):

| Outcome | From read result | Class | Retry by FAILED_ONLY |
|---|---|---|---|
| `PENDING` | not yet read | in progress (never terminal) | — |
| `FOUND` | FOUND | successful-resolved | no |
| `NOT_FOUND` | NOT_FOUND | **blocking-unresolved** (no official semantics make 404 definitive) | yes |
| `ACCESS_DENIED` | ACCESS_DENIED | blocking-unresolved | yes |
| `SELLER_MISMATCH` | SELLER_MISMATCH | blocking-unresolved, security-relevant | **no** (only a new FULL run re-reads it) |
| `TRANSIENT_ERROR` | FAILED after one in-execution re-request; run stop; write conflict | blocking-unresolved | yes |
| `PERMANENT_ERROR` | INVALID_RESPONSE, UNMAPPED_STATUS, NON_RETRYABLE chunk | blocking-unresolved | **no** |

The resolved-negative class is **empty** in S8C.2: no outcome has official definitive-negative
semantics. A live-evidence-backed correction (debt S8C2-06) may later reclassify NOT_FOUND.

Safe item codes: `LISTING_NOT_FOUND_AT_PROVIDER`, `LISTING_ACCESS_DENIED`, `LISTING_SELLER_MISMATCH`,
`LISTING_READ_FAILED`, `LISTING_WRITE_CONFLICT`, `LISTING_RESPONSE_INVALID`,
`LISTING_STATUS_UNMAPPED`, `LISTING_READ_REJECTED`, `RUN_ENDED_BEFORE_ITEM_PROCESSED`, or the run stop
code. Item transitions: `PENDING → {FOUND | NOT_FOUND | ACCESS_DENIED | SELLER_MISMATCH |
TRANSIENT_ERROR | PERMANENT_ERROR}`, each exactly once, fenced; terminal item outcomes never change.
Items record `listing_created` and `observation_appended` booleans for FOUND.

**E.5 Missing listings and every direct-read outcome.** Absence from enumeration changes nothing by
itself. The same matrix applies to enumerated items, known-not-enumerated items and FAILED_ONLY
targets; for an ID with no listing row, listing columns read "not created" and
`marketplace_listing_id` stays null.

| Direct read | Listing state | Listing sync state | Item outcome | Run contribution | Summary | FAILED_ONLY |
|---|---|---|---|---|---|---|
| FOUND (e.g. closed item) | facts applied, observation if changed | SYNCED | FOUND | found | resolves | n/a |
| NOT_FOUND | unchanged (never inferred INACTIVE/deleted) | ERROR `LISTING_NOT_FOUND_AT_PROVIDER` | NOT_FOUND | blocking | open item | yes |
| ACCESS_DENIED | unchanged | ERROR `LISTING_ACCESS_DENIED` | ACCESS_DENIED | blocking | open item | yes |
| SELLER_MISMATCH | unchanged, never reassigned | ERROR `LISTING_SELLER_MISMATCH` | SELLER_MISMATCH | blocking, security | open item | no |
| temporary failure | unchanged | ERROR `LISTING_READ_FAILED` | TRANSIENT_ERROR | blocking | open item | yes |
| unknown status / malformed | unchanged | ERROR `LISTING_STATUS_UNMAPPED` / `LISTING_RESPONSE_INVALID` | PERMANENT_ERROR | blocking | open item | no |

**E.6 Run outcome (G05).** Computed in the finish transaction from run items only:

```text
if stop reason set and kind = FULL and not enumeration_completed      -> FAILED
else if found = 0 and (blocking > 0 or stop reason set)               -> FAILED
else if blocking = 0 and pending = 0 and stop reason not set          -> SUCCEEDED
else                                                                  -> PARTIAL
```

`SUCCEEDED` means every required item is `FOUND`. `PARTIAL` means at least one blocking item remains
and meaningful work (≥ 1 FOUND) succeeded. `FAILED` categories: authorization/guard unusable at
start; enumeration failed before a complete ID set (`LISTING_ENUMERATION_INCOMPLETE`, systemic
provider failure, 429, forbidden); zero FOUND with blocking items; volume limit; lease/recovery
terminal failure. `safe_result_code` is `LISTING_SYNC_COMPLETED`, `LISTING_SYNC_PARTIAL` or the stop
code. Counters (derived by `GROUP BY outcome` over items, never from listing rows): `total`, `found`,
`blockingRetryable` (NOT_FOUND + ACCESS_DENIED + TRANSIENT_ERROR), `blockingNonRetryable`
(SELLER_MISMATCH + PERMANENT_ERROR), `pending`, plus per-outcome and per-origin breakdowns and
`listingsCreated`, `observationsAppended`. Example: 100 enumerated, 95 FOUND, 5 TRANSIENT_ERROR ⇒
PARTIAL.

Runtime (ADR-0024 G-04, not the listing summary): a run with ≥ 1 successful provider call ⇒
`MarkAvailable`; stop `PROVIDER_UNAVAILABLE`/`PROVIDER_RATE_LIMITED` ⇒ `UNAVAILABLE` with
classification; guard/credential stops do not touch runtime. Written only in the fenced finish
transaction and only if `ConfirmedOperationId` still equals `confirmed_operation_id_at_start`.
Sync never writes grants.

**E.7 Retry (FAILED_ONLY).** `POST /marketplace-listing-syncs/{runId}/retry` creates a **new** run:
`kind = FAILED_ONLY`, `retry_of_run_id = runId`, `root_run_id = original.root_run_id`. Allowed when the
original is terminal (`PARTIAL` or `FAILED`), belongs to the account, and has ≥ 1 retryable item
(else `LISTING_SYNC_NOTHING_TO_RETRY` 409). The server copies the original's retryable items
(`NOT_FOUND, ACCESS_DENIED, TRANSIENT_ERROR`) as `origin = RETRY_TARGET`, `PENDING` items of the new
run, in the same transaction that creates the run; no external IDs are accepted from clients. The
original run and its items stay immutable. FAILED_ONLY skips enumeration and missing reconciliation.
After a restart the failed IDs survive in run items and the retry works without rescanning.

**E.8 Listing-sync summary (resource-specific, derived).** Never stored on or derived from
`MarketplaceAccountConnection` (authorization/runtime). Computed per account at read time:
`activeRun`, `latestRun`, `latestFullRun`, `lastSucceededFullRunAt`, and
`openBlockingItemCount` = blocking items of `latestFullRun` whose `external_listing_id` has no `FOUND`
item in any later run with the same `root_run_id`. `health`:
`NEVER_RUN` (no FULL run) · `IN_PROGRESS` (active run) · `HEALTHY` (latest FULL `SUCCEEDED`, or
`PARTIAL` with `openBlockingItemCount = 0`) · `ATTENTION_REQUIRED` (latest FULL `PARTIAL` with open
items) · `FAILED` (latest FULL `FAILED` and no later FULL). The legacy generic account triple
`MarketplaceAccount.SyncState/LastSyncAttemptAt/LastSuccessfulSyncAt` is **not written** by S8C.2 and
is no longer displayed as listing health (debt S8C2-07). The UI never shows "sincronizado" while
open blocking items exist.

### F. HTTP, send certainty, rate limit (resolves G-08)

**F.1 Normalized failure (Commerce).**
`enum MarketplaceFailureClassification { NON_RETRYABLE, RETRYABLE, RATE_LIMITED, AUTH_RENEWAL_REQUIRED, USER_ACTION_REQUIRED, UNKNOWN }`;
`enum MarketplaceSendCertainty { NOT_SENT, SENT_AND_RESPONSE_RECEIVED, SENT_OR_UNKNOWN }`;
`sealed record MarketplaceProviderFailure(Classification, string SafeCode, SendCertainty, string? ProviderErrorCode, DateTimeOffset? RetryAfterUntil)`;
`sealed class MarketplaceProviderCallException(MarketplaceProviderFailure f) : Exception(f.SafeCode)`.
`MarketplaceRequestNotSentException` remains the only NOT_SENT signal for token operations.

**F.2 Send-certainty boundary (normative; S8C2-G01, GPT-6 Astra G01_CLOSED).**

- `MarketplaceRequestNotSentException` may be thrown **only** from an adapter-controlled path that
  exits **before `HttpClient.SendAsync` is invoked**. The only NOT_SENT family is pre-invocation:
  local validation failure; request/form materialization failure; a local guard rejects;
  cancellation observed and handled before `SendAsync`. The proof is "`SendAsync` was never invoked",
  never "the network probably failed before transmission".
- Once `SendAsync` is invoked, **any** exception without a complete usable provider response is
  `SENT_OR_UNKNOWN`, regardless of subtype, message or timing: `HttpRequestException` (any
  `HttpRequestError`, including `ConnectionError`/`NameResolutionError`/`SecureConnectionError`),
  `OperationCanceledException`, timeout, DNS/TCP/TLS/proxy failure, connection refused or reset,
  inner `SocketException`, headers received with a truncated body. Wire history is never inferred
  from the exception type.
- A complete provider response (success or error) is `SENT_AND_RESPONSE_RECEIVED`. A complete error
  response does **not** prove the refresh token was not consumed.
- The adapter implements the boundary with a single flag set immediately before `SendAsync`; the
  classification reads only that flag and whether a complete response was obtained.
- **Cancellation**: once `SendAsync` has begun, caller cancellation never restores NOT_SENT.
  Terminalization (operation decision, connection state, audit) uses a separate bounded cleanup
  token (`CancellationTokenSource(TimeSpan.FromSeconds(10))`, not linked to the caller). If the
  terminal state cannot be committed, the operation stays `PENDING` (unresolved) and the S8C.1
  fences keep provider execution blocked until the operation-row arbiter resolves it.
- These rules apply identically to the code exchange (ADR-0024 callback paths) and to refresh.
  ADR-0024's failure-certainty semantics are unchanged; this is its implementation, not an amendment.

**F.3 Refresh outcome matrix (normative; D-02).** V = previous confirmed version, R1 = stored
refresh token, R2 = returned refresh token.

| # | Case | Provider invocations | R1 uses | Candidate bytes / version | Receipt | Operation decision | Connection | RequiredAction |
|---|---|---|---|---|---|---|---|---|
| 1 | local validation fails pre-`SendAsync` | 0 | 0 | none | none | FAIL_CLOSED `REFRESH_NOT_SENT` | CONNECTED preserved, runtime UNAVAILABLE | CHECK_PROVIDER_ACCOUNT |
| 2 | form/serialization fails pre-`SendAsync` | 0 | 0 | none | none | FAIL_CLOSED `REFRESH_NOT_SENT` | CONNECTED preserved, UNAVAILABLE | CHECK_PROVIDER_ACCOUNT |
| 3 | cancellation observed pre-`SendAsync` | 0 | 0 | none | none | FAIL_CLOSED `REFRESH_NOT_SENT` | CONNECTED preserved, UNAVAILABLE | CHECK_PROVIDER_ACCOUNT |
| 4 | exception immediately after `SendAsync` begins | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 5 | `OperationCanceledException` after invocation | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 6 | timeout | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 7 | `HttpRequestError.ConnectionError` | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 8 | DNS/TCP/TLS failure surfaced by `SendAsync` | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 9 | connection reset | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 10 | headers received, body truncated | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 11 | 2xx with fully validated R2 | 1 | 1 | exact R2 material, V+1 | (op, ref, V+1) | CONFIRMED `REFRESHED` | CONNECTED V+1, `access_expires_at`, `last_refresh_at` | NONE |
| 12 | 2xx malformed/incomplete (missing R2, bad `expires_in`, `user_id` ≠ secret's) | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 13 | `invalid_grant` / `unauthorized_client` | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_REJECTED` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 14 | `invalid_client` / `unauthorized_application` | 1 | 1 | none | none | FAIL_CLOSED `PROVIDER_APP_CREDENTIALS_INVALID` | REAUTHORIZATION_REQUIRED | CONTACT_ADMIN |
| 15 | 429 with `Retry-After` | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_RATE_LIMITED` | REAUTHORIZATION_REQUIRED; `RuntimeRetryAfterUntil` recorded as diagnostic | REAUTHORIZE |
| 16 | 429 without `Retry-After` | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_RATE_LIMITED` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 17 | 5xx | 1 | 1 | none | none | FAIL_CLOSED `REFRESH_OUTCOME_UNKNOWN` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |
| 18 | R2 valid, first CAS fails, second CAS (same R2, same operation ID) succeeds | 1 | 1 | exact R2, V+1 (2 CAS attempts) | (op, ref, V+1) | CONFIRMED `REFRESHED` | CONNECTED V+1 | NONE |
| 19 | R2 valid, persistent store failure | 1 | 1 | none confirmed (2 CAS attempts, R2 buffers disposed) | none | FAIL_CLOSED `REFRESH_PERSISTENCE_FAILED` | REAUTHORIZATION_REQUIRED | REAUTHORIZE |

Other 4xx: `REFRESH_REJECTED`, no replay (no official statement proves non-consumption).
A local persistence failure never repeats the token POST; exactly one extra CAS of the identical R2
with the same operation ID; a second-attempt `VERSION_CONFLICT` is resolved by `UseConfirmedAsync`
with (this operation ID, V+1): a match proceeds to confirmation, anything else is row 19. A receipt
proves persistence, never authorization; a failed DB confirmation after a successful CAS goes to the
existing operation-row arbiter (REFRESH receipt path only under its exact guards, otherwise fail
closed). Material with no refresh token or a missing/unparseable secret ⇒ no invocation,
`REAUTHORIZATION_REQUIRED` with `REFRESH_TOKEN_UNAVAILABLE` (authorization is unrenewable; nothing
was sent). RequiredAction adds `PROVIDER_APP_CREDENTIALS_INVALID → CONTACT_ADMIN` (checked first
within REAUTHORIZATION_REQUIRED); the new `REFRESH_*`, `PROBE_IDENTITY_MISMATCH` and
`PROVIDER_CREDENTIAL_REJECTED` codes map to `REAUTHORIZE`.

**F.4 Clients and safe-read policy.** Named `IHttpClientFactory` clients `MercadoLivre.OAuth` (POST
`/oauth/token` only) and `MercadoLivre.Api` (GET only), compile-time base `https://api.mercadolibre.com/`,
`Timeout = Infinite` with per-attempt linked deadlines (token 20 s; reads 10 s), default HTTP logging
removed (`RemoveAllLoggers()`), no cookies, no redirects, `Accept: application/json`,
`User-Agent: Verce3D/1.0`, bearer added per request. `MarketplaceSafeReadExecutor` (hand-written, no
new package): GET only, ≤ 3 attempts within 30 s, delays 250 ms then 500 ms + injected jitter
0..100 ms, retries `RETRYABLE` and `RATE_LIMITED`; `Retry-After` (seconds or future HTTP-date, invalid
ignored) is a lower bound and, when beyond the remaining budget, the executor returns `RATE_LIMITED`
with `RetryAfterUntil` instead of sleeping; cancellation honored; no circuit breaker; POSTs never
use it.

**F.5 Throughput.** Per-provider in-flight cap (§E.1); each execution issues its calls sequentially.

**F.6 Classification (ML).**

| Response | Classification | Safe code | Certainty |
|---|---|---|---|
| exit before `SendAsync` (validation, materialization, guard, pre-send cancellation) | NON_RETRYABLE | `PROVIDER_REQUEST_NOT_SENT` | NOT_SENT |
| any exception after `SendAsync` invoked, or incomplete response | RETRYABLE (reads) | `PROVIDER_UNREACHABLE` / `PROVIDER_TIMEOUT` | SENT_OR_UNKNOWN |
| 408, 5xx | RETRYABLE | `PROVIDER_UNAVAILABLE` | SENT_AND_RESPONSE_RECEIVED |
| 429 | RATE_LIMITED | `PROVIDER_RATE_LIMITED` | SENT_AND_RESPONSE_RECEIVED |
| 401 (reads) | AUTH_RENEWAL_REQUIRED | `PROVIDER_TOKEN_REJECTED` | SENT_AND_RESPONSE_RECEIVED |
| 403 (reads) | USER_ACTION_REQUIRED | `PROVIDER_ACCESS_FORBIDDEN` | SENT_AND_RESPONSE_RECEIVED |
| token 400 `invalid_grant` / `unauthorized_client` / `invalid_operator_user_id` | USER_ACTION_REQUIRED | `PROVIDER_GRANT_REJECTED` | SENT_AND_RESPONSE_RECEIVED |
| `invalid_client`, `unauthorized_application` | NON_RETRYABLE | `PROVIDER_APP_CREDENTIALS_INVALID` | SENT_AND_RESPONSE_RECEIVED |
| other 4xx | NON_RETRYABLE | `PROVIDER_REQUEST_REJECTED` | SENT_AND_RESPONSE_RECEIVED |
| 2xx unparseable/invalid | NON_RETRYABLE | `PROVIDER_RESPONSE_INVALID` | SENT_AND_RESPONSE_RECEIVED (for token POSTs treated as consumption-unknown) |

Reads retry per §F.4 regardless of certainty (GET is idempotent); token POSTs never retry.
`ProviderErrorCode` only from the allow-list `invalid_grant, invalid_client, invalid_scope,
invalid_request, unsupported_grant_type, forbidden, local_rate_limited, unauthorized_client,
unauthorized_application, invalid_operator_user_id, not_found` (ADR-0024 §9 policy). Bodies capped at
1 MiB, parsed into internal DTOs, never logged/persisted. One structured log per call: provider,
operation (`OAUTH_EXCHANGE|OAUTH_REFRESH|USERS_ME|ITEMS_SEARCH_PROBE|ITEMS_SCAN|ITEMS_BULK`), status,
classification, certainty, attempt, duration, VERCE account ID, correlation ID, and for `ITEMS_BULK`
`requestedIdCount`, `envelopeCount`, `usableEnvelopeCount` and an element status-code histogram (the
L-07 evidence source) — never URL/query, IDs, headers, bodies, tokens, secret, code, state or
exception messages.

### G. Capability, grant, runtime

**G.1 Structural authority.** The S8C.2 migration upserts `(MERCADO_LIVRE, LISTINGS_READ)` to
`SUPPORTED/DISCOVERY/verified_at 2026-09-24T00:00:00Z` (`INSERT … ON CONFLICT … DO UPDATE`, inserting
the provider row if missing), so fresh and upgraded databases agree; `CommerceSeedService` stays
insert-if-missing. All other capabilities of all providers stay `UNKNOWN`. Down restores
`UNKNOWN/MANUAL/null`. Never changed at runtime.

**G.2 Guards** (at start/retry and inside every execution before provider calls):
account exists and Active (`MARKETPLACE_ACCOUNT_INACTIVE` 409) · SalesChannel exists, Active,
Marketplace, not DIRECT (`MARKETPLACE_ACCOUNT_CHANNEL_INVALID` 422) · listing reader registered
(`MARKETPLACE_PROVIDER_NOT_SUPPORTED` 409) · provider `LISTINGS_READ = SUPPORTED`
(`COMMERCE_CAPABILITY_UNAVAILABLE` 409, reason `CAPABILITY_UNSUPPORTED`) · account `LISTINGS_READ =
GRANTED` (same, `GRANT_UNKNOWN`/`GRANT_DENIED`) · authorization CONNECTED with confirmed
operation/reference/version, no PENDING credential operation, no provider callback fence
(`MARKETPLACE_AUTHORIZATION_NOT_USABLE` 409) · runtime ≠ UNAVAILABLE and `RuntimeRetryAfterUntil`
null or past (`COMMERCE_OPERATION_UNAVAILABLE` 503 + `Retry-After`) · at start/retry only: no active
run for the account (`LISTING_SYNC_ALREADY_ACTIVE` 409 with the active RunId; enforced by the partial
unique index). UNKNOWN runtime permits the first attempt; grant UNKNOWN blocks sync but never
connect/probe.

**G.3 Credential executor.** `MarketplaceCredentialExecutor.ExecuteAsync<T>` (Infrastructure):
(1) proactive refresh inside the margin; (2) provider in-flight slot; (3) provider gate **shared**
lease; (4) guards (business `LISTINGS_READ`, or the ADR-0024 probe requirement that bypasses runtime
UNAVAILABLE and grants but not Retry-After); (5) `UseConfirmedAsync`; (6) send; (7) release. On
`AUTH_RENEWAL_REQUIRED`: forced refresh then one retry. A lease never spans more than one call, so a
callback's exclusive lease interleaves. Store `NOT_FOUND`/`CORRUPTED_OR_UNDECRYPTABLE` ⇒
`REAUTHORIZATION_REQUIRED` (`PROBE_CREDENTIAL_UNREADABLE`); `STORE_UNAVAILABLE` ⇒ run stop.

**G.4 Failure classes stay separate.** Credential invalid ⇒ reauthorization; 403 ⇒ run stop
`PROVIDER_ACCESS_FORBIDDEN`, CHECK_PROVIDER_ACCOUNT on the sync status, never reauthorization;
provider failure ⇒ run stop + runtime UNAVAILABLE; rate limit ⇒ run stop + persisted Retry-After;
listing missing ⇒ item outcome; capability unsupported/unknown ⇒ start refused with reason.
Authorization RequiredAction (connection) and listing-sync required action (sync status) are
separate fields and surfaces.

### H. Data model (one migration `AddS8C2MarketplaceListingSync`, written by the implementation)

**H.1 `commerce.marketplace_listing_sync_run`** — Commerce technical workflow table, owned by the
listing-sync workflow (not by MarketplaceAccount; writing it never bumps the account Version).

| Column | Type / contract |
|---|---|
| id | uuid PK (v7) |
| marketplace_account_id | uuid FK marketplace_account RESTRICT |
| provider_code | varchar(64) FK marketplace_provider RESTRICT |
| kind | varchar(16) CHECK `FULL|FAILED_ONLY` |
| status | varchar(16) CHECK `QUEUED|RUNNING|SUCCEEDED|PARTIAL|FAILED` |
| phase | varchar(24) null CHECK `ENUMERATING|RECONCILING_MISSING|DETAILING` |
| root_run_id | uuid not null FK self RESTRICT (FULL: own id) |
| retry_of_run_id | uuid null FK self RESTRICT |
| requested_by_user_id | uuid, logical Platform reference |
| requested_at / updated_at | timestamptz |
| started_at / finished_at / heartbeat_at / lease_until | timestamptz null |
| lease_token | uuid null |
| attempt_count | int ≥ 0; max_attempts int 1..10 (default 3) |
| enumeration_completed | boolean default false |
| safe_result_code | varchar(64) null |
| last_failure_classification | varchar(32) null |
| provider_error_code | varchar(64) null (allow-listed) |
| retry_after_until | timestamptz null (diagnostic) |
| confirmed_operation_id_at_start | uuid null |
| version | bigint ≥ 1 |

Checks: RUNNING ⇔ `lease_token` and `lease_until` non-null; terminal ⇔ `finished_at` and
`safe_result_code` non-null, `lease_token` null; `attempt_count <= max_attempts`; FULL ⇔
`retry_of_run_id IS NULL AND root_run_id = id`; FAILED_ONLY ⇒ `retry_of_run_id` non-null and
`enumeration_completed = false`; FULL SUCCEEDED/PARTIAL ⇒ `enumeration_completed`.
Indexes: unique `(marketplace_account_id) WHERE status IN ('QUEUED','RUNNING')`;
`(status, requested_at) WHERE status IN ('QUEUED','RUNNING')` for dispatch; `(status, lease_until)
WHERE status = 'RUNNING'`; `(marketplace_account_id, kind, requested_at DESC)`; `(root_run_id,
requested_at)`; `(retry_of_run_id)`. Ordering by `requested_at` (tie: `created_at` metadata), never by
id. Runs are never deleted in S8C.2 (history never cascades).

**H.2 `commerce.marketplace_listing_sync_run_item`** — per-run durable identity, no payload.

| Column | Type / contract |
|---|---|
| id | uuid PK (v7) |
| sync_run_id | uuid FK marketplace_listing_sync_run RESTRICT |
| external_listing_id | varchar(200) |
| origin | varchar(24) CHECK `ENUMERATED|KNOWN_NOT_ENUMERATED|RETRY_TARGET` |
| outcome | varchar(24) CHECK `PENDING|FOUND|NOT_FOUND|ACCESS_DENIED|SELLER_MISMATCH|TRANSIENT_ERROR|PERMANENT_ERROR` |
| marketplace_listing_id | uuid null FK marketplace_listing RESTRICT |
| safe_error_code | varchar(64) null |
| provider_error_code | varchar(64) null (allow-listed) |
| attempt_count | int ≥ 0 |
| listing_created / observation_appended | boolean default false |
| created_at / updated_at | timestamptz; completed_at timestamptz null |

Checks: `outcome = 'PENDING'` ⇔ `completed_at IS NULL`; `FOUND` ⇒ `marketplace_listing_id` non-null
and `safe_error_code` null; non-FOUND terminal ⇒ `safe_error_code` non-null. Unique
`(sync_run_id, external_listing_id)`; index `(sync_run_id, outcome, origin, external_listing_id)`
(detail keyset and counters; origin rank is applied in the query); index `(marketplace_listing_id)`. Written only inside fenced run transactions.

**H.3 `commerce.marketplace_listing` additions:** `latest_observation_fingerprint char(64) null`;
`linkage_source varchar(24) null` CHECK `MANUAL|DETERMINISTIC_SKU` and CHECK
`(linkage_state = 'LINKED') = (linkage_source IS NOT NULL)`; `sku_mapping_id uuid null` FK
sku_mapping RESTRICT with CHECK `(linkage_source = 'DETERMINISTIC_SKU') = (sku_mapping_id IS NOT NULL)`;
`auto_link_suppressed boolean not null default false`; `variation_count int null CHECK >= 0`.
Index `(marketplace_account_id, sync_state)`. Backfill: LINKED rows `linkage_source = 'MANUAL'`;
`latest_observation_fingerprint` from the row with the greatest `ingested_at` per listing, null when
that maximum is shared by rows with different fingerprints (UUID order never used). Existing
`listing_url`, `provider_observed_at`, `sync_error` (catalog codes for provider writes) are written
by sync. No quantity column.

**H.4 `commerce.marketplace_account_sku_mapping`** — Commerce aggregate root, `[Auditable]`.

| Column | Type / contract |
|---|---|
| id | uuid PK (v7) |
| marketplace_account_id | uuid FK marketplace_account RESTRICT |
| external_sku | varchar(200), normalized |
| product_id | uuid, logical Catalog reference (no physical FK) |
| active | boolean |
| confirmed_by_user_id / confirmed_at | uuid (logical Platform) / timestamptz |
| invalidated_at / invalidated_by_user_id | timestamptz null / uuid null |
| invalidated_reason | varchar(32) null CHECK `OPERATOR_DEACTIVATED|REPLACED_BY_NEW_MAPPING` |
| created_at / updated_at / created_by / updated_by | application metadata |
| version | bigint |

Checks: `active` ⇔ `invalidated_at IS NULL AND invalidated_reason IS NULL`. Unique
`(marketplace_account_id, external_sku) WHERE active`; index `(marketplace_account_id, active)`;
index `(product_id)`. Rows are never deleted.

**H.5 `commerce.marketplace_listing_observation`:** `provider_observed_at` DROP NOT NULL;
`variation_count int null CHECK >= 0`.

**H.6 Data and down.** §G.1 upsert. Down drops H.1/H.2/H.4 and the H.3/H.5 columns, sets null
`provider_observed_at` to `ingested_at` before restoring NOT NULL (lossy, disposable databases only)
and restores capability UNKNOWN. Certified on disposable PostgreSQL: fresh zero→head, S8C.1 head →
S8C.2 upgrade with representative data, down to S8C.1, re-upgrade.

### I. Observations

Append-only children, no payload. **Fingerprint v2**: lowercase hex SHA-256 of UTF-8
`"v2|" + concat(field)` where each field is `"{length}:{value}"` or `"-1:"` for null, in order
ExternalSku, Title, ObservedPrice (invariant, two decimals), ObservedStatus, ProviderNativeStatus,
VariationCount. Freshness, URL and run IDs excluded; raw JSON never hashed. An observation is appended
only when the fingerprint differs from `latest_observation_fingerprint`; `A → B → A` across runs
yields three rows, identical repeats none. **ObservationKey** `ps:{runId:N}:{fingerprint}`; an item
reaches FOUND once per run (fenced), so a listing is applied at most once per run; the database unique
key is a backstop. Field table:

| Column | Meaning | Null means | Bounds |
|---|---|---|---|
| external_sku | authoritative SELLER_SKU | none authoritative | ≤ 200 |
| title_snapshot | title | absent | ≤ 500 |
| observed_price | listing `price`, BRL (not promotional sale price) | unknown — never 0 | numeric(18,2) > 0 |
| observed_status | §C.2 | never | enum |
| provider_native_status | bounded native status | absent | ≤ 200 |
| variation_count | provider variation count | pre-S8C.2 rows only | ≥ 0 |
| provenance | `PROVIDER_SYNC` | never | enum |
| provider_observed_at | provider `last_updated` | unknown — never now | timestamptz |
| ingested_at | VERCE commit time | never | timestamptz |
| observation_key / fingerprint | idempotency / v2 | never | ≤ 200 / 64 hex |

No personal data. Retention unchanged (180 days, latest kept).

### J. API and frontend

| Method / path under `/api/commerce` | Policy | Contract |
|---|---|---|
| `POST /marketplace-accounts/{id}/listing-syncs` | `commerce:manage` + antiforgery | starts FULL; 202 run DTO; §G.2 errors |
| `POST /marketplace-listing-syncs/{runId}/retry` | `commerce:manage` + antiforgery | creates FAILED_ONLY from the original run's durable items; 202; `LISTING_SYNC_NOTHING_TO_RETRY` 409 |
| `GET /marketplace-accounts/{id}/listing-sync` | `commerce:read` | `{availability:{canStart, blockedReason, requiredAction, retryAfterUntil}, summary (§E.8)}` |
| `GET /marketplace-listing-syncs/{runId}` | `commerce:read` | run DTO with derived counters |
| `GET /marketplace-listing-syncs/{runId}/items?outcome=&page=&pageSize=` | `commerce:read` | paginated items (keyset `external_listing_id`) in the repository envelope `{items, page, pageSize, total}`, `pageSize` 1..100: externalListingId, origin, outcome, safeErrorCode, marketplaceListingId, retryable |
| `GET /marketplace-accounts/{id}/sku-mappings?active=&page=` | `commerce:read` | mappings with product code/name via composition |
| `POST /marketplace-accounts/{id}/sku-mappings` | `commerce:manage` + antiforgery | `{externalSku, productId, replaceMappingId?, replaceMappingVersion?}`; 201; `SKU_MAPPING_CONFLICT` 409, `SKU_MAPPING_PRODUCT_INELIGIBLE` 422 |
| `POST /sku-mappings/{mappingId}/deactivate` | `commerce:manage` + antiforgery | `{version}`; 204 |

Run DTO: `runId, accountId, kind, status, phase, rootRunId, retryOfRunId, requestedAt, startedAt,
finishedAt, attemptCount, enumerationCompleted, counters, safeResultCode, requiredAction`.
`blockedReason` catalog: `ACCOUNT_INACTIVE, CHANNEL_INVALID, PROVIDER_NOT_CONFIGURED,
CAPABILITY_UNSUPPORTED, GRANT_UNKNOWN, GRANT_DENIED, AUTHORIZATION_NOT_USABLE, RUNTIME_UNAVAILABLE,
RATE_LIMITED, SYNC_ACTIVE`. Clients never send external IDs for retry. No ML detail, scroll ID,
provider text, token, reference or seller PII in any DTO. Published Items adds `listingUrl,
providerNativeStatus, variationCount, linkageSource`; header "Anúncios observados nos provedores
(somente leitura)." UI: Marketplace Accounts shows the listing-sync panel (health, counts, start,
"Tentar novamente itens com erro" on the latest retryable run, failed-item list, progress polling
every 5 s while active) separately from the authorization status/RequiredAction block; Published Items
shows native status, safe error, linkage source, "Estoque: não sincronizado", and for a LINKED listing
with a SKU an explicit "Confirmar SKU → Produto para esta conta" action; a mapping list lets
Owner/Operator deactivate. Viewer reads only.

### K. Security and provider validation

No client secret, token, code, state, scroll ID or seller PII in Git, frontend, DTOs, logs, AuditLog
or observations. Fenced explicit audit rows `LISTING_SYNC_REQUESTED`, `LISTING_SYNC_RETRY_REQUESTED`,
`LISTING_SYNC_FINISHED` (entity `commerce.marketplace_listing_sync_run`, allowlisted kind/status/
counters/safe code). SKU mappings use generic audit. CI never needs credentials or internet: the real
connector runs against a fake `HttpMessageHandler` with official-contract-shaped fixtures, plus
Testcontainers and the S8C.1 test-owned E2E host. **CI proves only that the application chunks detail
reads at `DetailBatchSize` (20); it never proves the provider accepts 20.** Live validation is a
manual human gate with a dedicated ML test seller in production (no sandbox); test-fixture
preparation (TP-1..TP-4, PREP-PAUSED-01, PREP-CLOSED-01 — all outside VERCE, never an S8C.2 write)
and checklist L-01..L-14 with Mandatory/Conditional/Optional classes are frozen in the handoff.
**L-07 proves only that the application batch of 20 is accepted**: it requires ≥ 20 distinct valid
listings on that test seller and reads the first real 20-ID `ITEMS_BULK` request of the first FULL
sync from the safe call log. If it fails, the batch size is not silently lowered: safe evidence is
recorded, the maximum stays UNKNOWN, and a controlled correction is opened. Mixed per-element bulk
semantics and the "known locally, absent from enumeration ⇒ direct re-read" branch are mandatory
automated tests; their live evidence is optional/conditional respectively. Delivery status with all
automated gates green: **`IMPLEMENTED_PENDING_LIVE_PROVIDER_VALIDATION`**. The live gate passes when
every Mandatory check passes, every Conditional check passes or is NOT_APPLICABLE with a documented
reason, and no `LIVE_PROVIDER_CONTRACT_FINDING` was raised; an independent review of that evidence
may then declare S8C.2 complete.

## Alternatives considered

1. **ML-specific token/session tables.** Rejected: duplicates ADR-0024 authority.
2. **Inferring NOT_SENT from connect/DNS/TLS exception types.** Rejected (Astra G01): wire history
   cannot be proven after `SendAsync` is invoked.
3. **Unfenced batch writes validated only at run finish.** Rejected (G02): a stale worker could
   commit after reclaim.
4. **Deriving failures from listing rows.** Rejected (G03): unknown IDs have no row and would vanish.
5. **Manual links as account-wide SKU authority.** Rejected (G04): a link proves one listing only.
6. **SUCCEEDED with listing errors / account SYNCED on partial data.** Rejected (G05).
7. **Treating 20 as the bulk maximum.** Rejected (G06): not officially stated.
8. **RETRY_WAIT run state.** Removed: redundant once item retries are new runs and crash recovery is
   reclaim.
9. **Offset paging; persisting scroll IDs; per-item `/items/{id}`; interleaving details with scroll.**
   Rejected: 1000-result cap; 5-minute opaque cursor; 20× calls; scroll expiry risk.
10. **Storing quantity with a certainty flag.** Rejected: ranged values indistinguishable.
11. **Inferring INACTIVE/deleted from absence or 404.** Rejected: no official semantics.
12. **Auto-link by Product code or title; variation tables.** Rejected: different namespaces; no consumer.
13. **Scheduled polling / webhooks.** Rejected for S8C.2: no cadence evidence; listener and
    signature evidence missing.
14. **Outbox for sync; Polly.** Rejected: runs need user-visible fenced state; the frozen policy is
    small and must exclude token POSTs.
15. **PKCE via encrypted verifier in Commerce.** Rejected by ADR-0024; deferred.
16. **Account-owned summary columns.** Rejected: would bump the account Version on every run and mix
    resource health with authorization; the summary is derived.

## Consequences

Positive: real listing reads through certified S8C.1 machinery; stale workers cannot commit; every
failed ID is durable and retryable without rescanning; SKU authority is explicit; run, listing and
summary states agree.

Negative / accepted: ML cannot be activated in Production until a certified production store exists.
Freshness depends on human-triggered runs. NOT_FOUND stays blocking until live evidence justifies a
reclassification, so a deleted listing keeps its account in ATTENTION_REQUIRED. A `/users/me` failure
right after a new-account exchange fails every ML account closed, and a 429/5xx on refresh forces
reauthorization (ADR-0024 conservative rules). Sync updates bump listing Versions (operator may see
409). The legacy account sync triple remains unused. PKCE not used. Stock unavailable.

## Compliance checks

- Architecture: no ML type/URL/DTO in Commerce; `IMarketplaceListingReader` and
  `ICommerceProductEligibilityReader` are the only new ports; ML DTOs internal; no Shopee/TikTok
  adapter; no provider-code branch outside the adapter, seed/migration and tests; fake HTTP handler
  absent from `Verce.Api`'s closure.
- The normative matrices §F.3 (19 rows), §E.5 and the handoff's G02/G03/G04/G06 tests.
- PostgreSQL tests for §C–§E and §H; migration fresh/upgrade/down/re-upgrade.
- S8C.1 regression (RT-01/02/03, store, callback security, audit/logging, fake isolation, S8C.1
  Playwright) green after D-01..D-03 and again at the end.
- Security: captured logs, persisted AuditLog, DTOs and DB rows free of secrets, tokens, code, state,
  scroll IDs, URL queries and seller PII.
- Live checklist evidence recorded before S8C.2 is declared complete.
