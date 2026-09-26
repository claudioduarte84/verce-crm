# S8C.2 — Implementation Handoff (Mercado Livre listing reads)

Status: **architecture frozen and approved; ready for implementation** (independent Codex Sol final
procedural regate, 2026-09-26). Implementation is **NOT COMPLETE** and has not started. Normative source:
[ADR-0025](architecture/ADR-0025-s8c2-mercado-livre-listing-read-integration.md); ADR-0024 remains
normative for everything credential-related and is **not amended**. Section references (§A…§K) point
into ADR-0025. This file directs ONE implementation macro-mission after the regate. No code or
migration is delivered by the architecture mission.

Baseline: `01e5d6a68cce2e83744e5c280faab903afcdc4cd`. Do not start S8C.3. Do not commit, push or tag
unless the mission explicitly orders it. Review record: S8C2-G01: GPT-6 Astra reviewed — G01_CLOSED.

## 0. Fixed scope

In: S8C.1 defect fixes D-01..D-03, Mercado Livre (MLB) authorization adapter on the existing port,
G-08 HTTP layer with the Astra send boundary, listing read port, fenced durable sync runs with
durable run items, FULL and FAILED_ONLY runs, normalized observations, explicit SKU mappings and
`SKU-LINK-1`, provider-neutral API/UI, test-owned ML-shaped fake HTTP, live-validation preparation.

Out (all NO): LISTINGS_WRITE/publication, inventory read/write, orders, Sale ingestion or mutation,
fees, shipping, analytics, ads, webhooks/notifications, scheduled provider polling, Shopee, TikTok,
fuzzy matching, automatic Product creation, automatic Offer creation/linking, PKCE, Production
activation of ML, cloud credential store, horizontal scaling.

## Stream A — S8C.1 fixes first, then ML auth + HTTP (strict order)

**A1 — D-01 (expiry persistence).** `MarketplaceAuthorizationCompletion` gains a trailing
`DateTimeOffset? AccessExpiresAt = null`. `MarketplaceAccount.InstallConfirmedCredential(reference,
version, operationId, now, accessExpiresAt, isRefresh)` → `Connection.Confirm(...)` sets
`AccessExpiresAt` and, when `isRefresh`, `LastRefreshAt = now`. Callback passes
`completion.AccessExpiresAt`; refresh passes `MarketplaceRefreshCompletion.AccessExpiresAt`;
receipt-only refresh recovery passes `null` (forces refresh on next use).

**A2 — D-02 (refresh per ADR-0025 §F.2/§F.3, Astra G01).**
- `RefreshAsync(Guid accountId, bool force = false, Guid? rejectedConfirmedOperationId = null, CancellationToken ct)`;
  force semantics §A.7.
- Catch `MarketplaceRequestNotSentException` ⇒ operation `FAIL_CLOSED("REFRESH_NOT_SENT")` +
  cleanup, **no** `RequireReauthorization`, `RecordRuntimeFailure("UNKNOWN","REFRESH_NOT_SENT")`.
- Catch `MarketplaceProviderCallException` ⇒ map its safe code to §F.3 codes (`REFRESH_REJECTED`,
  `PROVIDER_APP_CREDENTIALS_INVALID`, `REFRESH_RATE_LIMITED`, `REFRESH_OUTCOME_UNKNOWN`) and call
  `ResolvePendingAsync(operationId, code)`; any other exception ⇒ `REFRESH_OUTCOME_UNKNOWN`.
- Missing/unparseable secret or missing refresh token ⇒ no provider call,
  `RequireReauthorization("REFRESH_TOKEN_UNAVAILABLE")`, operation FAIL_CLOSED.
- CAS failure (`WRITE_FAILED`/`STORE_UNAVAILABLE`/`VERSION_CONFLICT`) ⇒ exactly one more CAS of the
  identical R2 bytes with the same operation ID and expected V; if that returns `VERSION_CONFLICT`,
  `UseConfirmedAsync(account, provider, ref, V+1, operationId)`: SUCCESS ⇒ treat as receipt and
  confirm; otherwise `ResolvePendingAsync(operationId, "REFRESH_PERSISTENCE_FAILED")`. Never a second
  POST. Zero R2 buffers in `finally`.
- All terminalization after `SendAsync` may have started uses
  `using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10))` (not linked to the
  caller). If the terminal commit fails, leave the operation PENDING (housekeeping resolves it; the
  pending-operation guard blocks execution meanwhile).
- `ResolvePendingAsync(Guid operationId, string safeCode = "OPERATION_RECOVERY_FAIL_CLOSED", CancellationToken ct)`.
- `CommerceEndpoints.RequiredAction`: inside REAUTHORIZATION_REQUIRED, `PROVIDER_APP_CREDENTIALS_INVALID
  → CONTACT_ADMIN` first; `REFRESH_REJECTED, REFRESH_OUTCOME_UNKNOWN, REFRESH_RATE_LIMITED,
  REFRESH_PERSISTENCE_FAILED, REFRESH_TOKEN_UNAVAILABLE, PROBE_IDENTITY_MISMATCH,
  PROVIDER_CREDENTIAL_REJECTED → REAUTHORIZE`; existing mappings unchanged.

**A3 — D-03 (probe).** Probe writes grant source `PROBE_INSPECTION`; refuses with
`COMMERCE_OPERATION_UNAVAILABLE` (503, `Retry-After`) while `RuntimeRetryAfterUntil > now`;
`RecordRuntimeFailure` gains optional `retryAfterUntil` forwarded to `MarkUnavailable`; add
`MarketplaceAccountConnection.RecordRetryAfter(until, now)`. (Probe moves onto the credential
executor in A8.)

**A4 — S8C.1 regression gate.** Re-run and report raw results for: RT-01, RT-02, RT-03 suites;
`LocalProtectedCredentialStoreTests`; `MarketplaceCallbackSecurityTests`; `MarketplaceAuditSecurityTests`
and captured-log tests; `MarketplaceProductionIsolationTests` (fake isolation);
`MarketplaceHousekeepingFairnessTests`; all `Commerce*IntegrationTests`; the S8C.1 marketplace
Playwright suite (`npm run test:marketplace`). Update the fake connector only as needed to supply
`AccessExpiresAt`. **No ML client, option or registration is added before A4 is green.**

**A5 — contracts.** Add §F.1 types, §B.1 listing contracts, `ICommerceProductEligibilityReader`
(`Task<IReadOnlyDictionary<Guid, bool>> GetActiveAsync(IReadOnlyCollection<Guid> productIds, CancellationToken)`,
implemented in `Verce.Api` against Catalog), registry `TryGetListingReader` (constructor takes both
enumerables; duplicate codes or a reader without a same-code connector throw).

**A6 — options and composition.** `MercadoLivreOptions` (`Marketplaces:MercadoLivre`): `Enabled`
(false), `ClientId`, `ClientSecret`, `ClientSecretFile`, `RedirectUri`,
`AccessTokenRefreshMarginSeconds` (300, 0..3600); `IValidateOptions` + `ValidateOnStart` per §A.2;
never logged. `appsettings.json`: only `Enabled=false` and the margin. `AddVerceMercadoLivre` from
`AddVerceMarketplaceInfrastructure`: local-store branch + `Enabled` ⇒ register clients, executor,
connector (one instance, both interfaces), listing-sync services/jobs; non-local-store + `Enabled` ⇒
throw `MARKETPLACE_REAL_PROVIDER_REQUIRES_CERTIFIED_STORE`; `Enabled=false` ⇒ nothing.

**A7 — HTTP layer** (`Verce.Infrastructure.Marketplaces.Http`): named clients §F.4;
`MarketplaceHttpSender` owning the single `sendInvoked` flag set immediately before `SendAsync` and
the §F.2/§F.6 classification (reads only the flag and response completeness — never exception
types); `MarketplaceSafeReadExecutor` with injected `IMarketplaceDelay`/`IMarketplaceJitter` (tests
never sleep); `RetryAfterParser`; 1 MiB bounded body reader; safe structured call log, one Information event per
call from category `Verce.Infrastructure.Marketplaces.Http.MarketplaceHttpSender` with the frozen message
template `MarketplaceProviderCall provider={Provider} operation={Operation} status={HttpStatus} classification={Classification} certainty={Certainty} attempt={Attempt} durationMs={DurationMs} account={AccountId} correlation={CorrelationId}`
and, for `ITEMS_BULK`, ` requestedIdCount={n} envelopeCount={n} usableEnvelopeCount={n} histogram={code:count;…}`
(`status=none` when no response; `classification=NONE` on success); no other key is ever added.

**A8 — `MercadoLivreConnector`** (`…MercadoLivre`): Begin §A.3 (wrong redirect path ⇒
`MarketplaceRequestNotSentException("REDIRECT_PATH_INVALID")`, pre-send); CompleteOnce §A.5; Refresh
§A.7; Inspect §A.9; `ReadListingPageAsync` §B.2 (`MercadoLivreScrollCursor`); `ReadListingDetailsAsync`
§B.3 + §C.2 (unknown status ⇒ `UNMAPPED_STATUS`, no facts). Secret JSON v1 via `Utf8JsonReader/Writer`;
JSON numbers via `GetDecimal()/GetInt64()`, never `double`. `MarketplaceCredentialExecutor` §G.3
(signature `Task<T> ExecuteAsync<T>(Guid accountId, MarketplaceExecutionRequirement requirement,
Func<MarketplaceCredentialContext, CancellationToken, Task<T>> send, CancellationToken ct)`;
`MarketplaceCredentialContext(Guid AccountId, string ExternalAccountId, ReadOnlyMemory<byte> Secret,
Guid ConfirmedOperationId)`; guard failures throw `MarketplaceExecutionBlockedException(reason)` with
the §J `blockedReason` codes); `ProbeAsync` routed through it with the probe requirement. Callback
endpoint binds `state`/`code` optional (§A.4).

## Stream B — runs, run items, fencing, reads, observations, SKU authority

**B1 — domain (Commerce).** `MarketplaceListing`: §C.1 methods, `LatestObservationFingerprint`,
`LinkageSource` (`MarketplaceLinkageSource { MANUAL, DETERMINISTIC_SKU }`), `SkuMappingId`,
`AutoLinkSuppressed`, `VariationCount`; `Link` ⇒ MANUAL, clears mapping ID and suppression; `Unlink`
⇒ clears source/mapping, sets suppression; `MarkNeedsReview` clears source/mapping.
`ListingFingerprint.ComputeV2` (§I). Observation `ProviderObservedAt` nullable + `VariationCount`.
`LinkageDecision(State, ProductId?, SkuMappingId?)`. `SkuLinkRule.Decide(sku, variationCount,
IReadOnlyList<(Guid MappingId, Guid ProductId)> activeMappings, IReadOnlyDictionary<Guid,bool> productActive)`
exactly §D.4. New AR `MarketplaceAccountSkuMapping` (`Confirm` factory, `Deactivate(actor, now)`,
`Replace` → `REPLACED_BY_NEW_MAPPING`), `[Auditable]`, never deleted.

**B2 — run and item entities.** `MarketplaceListingSyncRun : TechnicalEntity, ITechnicalTable`
(§H.1) and `MarketplaceListingSyncRunItem : TechnicalEntity, ITechnicalTable` (§H.2); enums
`MarketplaceListingSyncKind {FULL, FAILED_ONLY}`, `MarketplaceListingSyncStatus {QUEUED, RUNNING,
SUCCEEDED, PARTIAL, FAILED}`, `MarketplaceListingSyncPhase`, `MarketplaceListingSyncItemOrigin
{ENUMERATED, KNOWN_NOT_ENUMERATED, RETRY_TARGET}`, `MarketplaceListingSyncItemOutcome {PENDING, FOUND,
NOT_FOUND, ACCESS_DENIED, SELLER_MISMATCH, TRANSIENT_ERROR, PERMANENT_ERROR}`. `RunOutcomeRule.Compute`
exactly §E.6. Retryable set `{NOT_FOUND, ACCESS_DENIED, TRANSIENT_ERROR}`. None `[Auditable]`.

**B3 — migration `AddS8C2MarketplaceListingSync`** exactly §H.1–§H.6 (tables, checks, FKs with
RESTRICT, filtered unique indexes, backfills, capability upsert, down). Regenerate the snapshot. No
other schema change.

**B4 — lease fencing (§E.2), implemented once.** `ListingSyncLease` helper:
- `TryClaimAsync(runId, now)` executes the §E.2 claim SQL with a fresh UUID v7 token; returns the
  token or null.
- `ExecuteFencedAsync(RunLease lease, Func<VerceDbContext, CancellationToken, Task> write, ct)`: opens
  one `IUnitOfWork` transaction; **first statement** is the guard `UPDATE … WHERE id=@runId AND
  lease_token=@token AND status='RUNNING' RETURNING id` via `FromSql`/`ExecuteSqlRaw` on the same
  connection/transaction; 0 rows ⇒ throw `ListingSyncLeaseLostException` before any other statement
  (the UoW rolls back); then runs `write`; commits. Every run-owned write in B5–B7 goes through it —
  there is no other write path. `ListingSyncLeaseLostException` aborts the execution silently (log
  `LISTING_SYNC_LEASE_LOST`, no further writes).
- Dispatcher terminalization of exhausted runs uses the stale-token CAS of §E.2.

**B5 — execution services** (`Verce.Infrastructure.Marketplaces.ListingSync`):
- `MarketplaceListingSyncService`: `StartFullAsync(accountId, actorId)` and
  `RetryAsync(runId, actorId)` — guards (§G.2, no provider call), insert run (+ for retry, copy the
  retryable items of the original as `RETRY_TARGET/PENDING` in the same transaction) + explicit audit
  in one UoW; a unique-index violation ⇒ discard context, new UoW reads the active run, return
  `LISTING_SYNC_ALREADY_ACTIVE` with its ID; then best-effort `IScheduler.TriggerJob(dispatchJobKey)`.
  `GetStatusAsync(accountId)` (availability + §E.8 summary), `GetRunAsync(runId)` (derived counters),
  `GetItemsAsync(runId, outcome, page, pageSize)`.
- `MarketplaceListingSyncDispatchJob` (`[DisallowConcurrentExecution]`): terminalize exhausted
  lease-expired runs; claim due runs (QUEUED, or RUNNING with expired lease) ordered by
  `requested_at` up to `MaxConcurrentRuns − executing`; hand each claimed lease to
  `MarketplaceListingSyncExecutor` (hosted singleton, one DI scope and one `Task` per execution,
  cancelled on host stop).
- `MarketplaceListingSyncExecution.RunAsync(lease)`: §E.3 (FULL) or detail-only (FAILED_ONLY);
  enumeration pages inserted per page with `INSERT … ON CONFLICT DO NOTHING`; known-not-enumerated
  `INSERT … SELECT` in batches of 500; detail chunks of `DetailBatchSize` over PENDING items ordered by
origin rank (ENUMERATED, RETRY_TARGET, KNOWN_NOT_ENUMERATED) then `external_listing_id`, one fenced transaction per
  chunk that loads existing listings (without observations) and active mappings for the chunk's SKUs
  and product eligibility, applies §E.5/§D.4, updates items; §C.4 retry (≤ 3 fresh fenced
  transactions, then `TRANSIENT_ERROR/LISTING_WRITE_CONFLICT`); stop reasons §E.3; finish
  transaction: PENDING → TRANSIENT_ERROR (stop code) if stopped, outcome by `RunOutcomeRule`, runtime
  update guarded by `confirmed_operation_id_at_start`, audit `LISTING_SYNC_FINISHED`.
- Per-provider in-flight semaphore (`MaxInFlightRequestsPerProvider`) inside the credential executor.

**B6 — options `Marketplaces:ListingSync`:** `SchedulingEnabled` true, `DispatchIntervalSeconds` 60
(10..3600), `MaxConcurrentRuns` 2 (1..8), `MaxInFlightRequestsPerProvider` 2 (1..8), `MaxAttempts` 3
(1..10), `LeaseSeconds` 120 (30..900), `MaxListingsPerRun` 200000, `EnumerationPageSize` 100 (1..100),
`DetailBatchSize` 20 (1..20 — an application assumption; see D10/L-07). Tests set
`SchedulingEnabled=false` and drive the dispatcher directly.

**B7 — SKU mapping service.** `ConfirmAsync(accountId, sku, productId, replace?, actor)`: normalize
SKU (§B.3 rules; empty ⇒ 422 `SKU_MAPPING_SKU_INVALID`), check Product active via
`ICommerceProductEligibilityReader` (else 422 `SKU_MAPPING_PRODUCT_INELIGIBLE`), existing ACTIVE
mapping for (account, sku): absent ⇒ create; present and no/incorrect replace ID/version ⇒ 409
`SKU_MAPPING_CONFLICT`; matching replace ⇒ deactivate old (`REPLACED_BY_NEW_MAPPING`) + create new in
one UoW. `DeactivateAsync(mappingId, version, actor)`. Neither touches listings.

**B8 — retention.** Unchanged; only adjust for nullable `ProviderObservedAt`; not scheduled (debt S8C2-03).

## Stream C — APIs and UX

**C1 — endpoints** exactly ADR §J (sync start, retry, status, run, items, SKU mappings list/confirm/
deactivate). Antiforgery first on mutations; unknown JSON properties ⇒ 400; guard codes ⇒ §G.2
statuses; `Retry-After` header (ceiling seconds) for `RATE_LIMITED`. Responses carry local codes only.

**C2 — DTOs:** `ListingSyncRunResponse`, `ListingSyncCountersResponse`, `ListingSyncStatusResponse`
(availability + summary), `ListingSyncRunItemResponse` (the items endpoint returns the repository paged envelope
`{items, page, pageSize, total}` with `pageSize` clamped to 1..100, like Published Items),
`SkuMappingResponse`; Published Items and
`ListingResponse` gain `listingUrl, providerNativeStatus, variationCount, linkageSource`. Regenerate
OpenAPI/frontend types; contract check must pass. The account list DTO keeps `syncState` for
compatibility but the UI no longer renders it as listing health.

**C3 — pt-BR catalog** (frontend `marketplaceCatalog.ts`):

| Code | pt-BR |
|---|---|
| `ACCOUNT_INACTIVE` | Conta inativa. Ative a conta para sincronizar. |
| `CHANNEL_INVALID` | O canal de vendas desta conta está inativo ou inválido. |
| `PROVIDER_NOT_CONFIGURED` | Este provedor não está configurado neste ambiente. |
| `CAPABILITY_UNSUPPORTED` | Leitura de anúncios não é suportada para este provedor. |
| `GRANT_UNKNOWN` | Permissão de leitura de anúncios ainda não verificada. Use "Sondar disponibilidade". |
| `GRANT_DENIED` | O provedor negou a leitura de anúncios. Verifique a conta no provedor. |
| `AUTHORIZATION_NOT_USABLE` | A autorização desta conta não está utilizável. Siga a ação indicada na conta. |
| `RUNTIME_UNAVAILABLE` | O provedor está indisponível para esta conta. Use "Sondar disponibilidade". |
| `RATE_LIMITED` | Limite de requisições do provedor atingido. Tente novamente após {hora}. |
| `SYNC_ACTIVE` | Já existe uma sincronização em andamento. |
| `QUEUED` / `RUNNING` | Na fila / Em andamento |
| `SUCCEEDED` / `PARTIAL` / `FAILED` | Concluída / Concluída com pendências / Falhou |
| `NEVER_RUN` / `IN_PROGRESS` / `HEALTHY` / `ATTENTION_REQUIRED` | Nunca sincronizada / Em andamento / Anúncios em dia / Requer atenção |
| `LISTING_NOT_FOUND_AT_PROVIDER` | Anúncio não encontrado no provedor (último estado conhecido mantido). |
| `LISTING_ACCESS_DENIED` | O provedor negou acesso a este anúncio. |
| `LISTING_SELLER_MISMATCH` | O anúncio pertence a outro vendedor no provedor. Verifique antes de sincronizar novamente. |
| `LISTING_RESPONSE_INVALID` | Resposta do provedor inválida para este anúncio. |
| `LISTING_STATUS_UNMAPPED` | Status do provedor não reconhecido; anúncio não atualizado. |
| `LISTING_READ_FAILED` / `LISTING_READ_REJECTED` | Falha ao ler este anúncio. / O provedor rejeitou a leitura deste anúncio. |
| `LISTING_WRITE_CONFLICT` | Conflito ao gravar este anúncio. Tente novamente. |
| `RUN_ENDED_BEFORE_ITEM_PROCESSED` | A sincronização terminou antes de ler este anúncio. |
| `RUN_EXECUTION_ATTEMPTS_EXHAUSTED` | Execução interrompida repetidamente. Inicie uma nova sincronização. |
| `LISTING_ENUMERATION_INCOMPLETE` | Não foi possível listar todos os anúncios do provedor. |
| `LISTING_VOLUME_EXCEEDS_LIMIT` | Volume de anúncios acima do limite configurado. |
| `PROVIDER_CREDENTIAL_REJECTED` | Credencial recusada pelo provedor. Reautorize a conta. |
| `PROVIDER_ACCESS_FORBIDDEN` | O provedor recusou o acesso. Verifique a conta no provedor. |
| `PROVIDER_UNAVAILABLE` / `PROVIDER_RATE_LIMITED` | Provedor indisponível. / Limite de requisições do provedor atingido. |
| `CREDENTIAL_STORE_UNAVAILABLE` | Armazenamento de credenciais indisponível. Contate o administrador. |
| `PROVIDER_APP_CREDENTIALS_INVALID` | Credenciais do aplicativo VERCE inválidas. Contate o administrador. |
| `LISTING_SYNC_NOTHING_TO_RETRY` | Não há itens com erro que possam ser tentados novamente. |
| `SKU_MAPPING_CONFLICT` / `SKU_MAPPING_PRODUCT_INELIGIBLE` / `SKU_MAPPING_SKU_INVALID` | Já existe um mapeamento ativo para este SKU. / Produto inexistente ou inativo. / SKU inválido. |
| `MANUAL` / `DETERMINISTIC_SKU` | Vinculado manualmente / Vinculado automaticamente por mapeamento de SKU confirmado |

**C4 — MarketplaceAccountsPage:** authorization block (S8C.1, unchanged semantics) and a separate
"Sincronização de anúncios" block: health, availability reason, counts from the latest run,
"Sincronizar anúncios" (Owner/Operator), "Tentar novamente itens com erro" (Owner/Operator, when the
latest terminal run has `blockingRetryable > 0`, calls retry with that run ID), failed-item list
(paginated items endpoint, blocking outcomes), progress polling every 5 s while active, and a "SKUs
confirmados" list with deactivate (Owner/Operator). Viewer: read only. The generic account
`syncState` label is removed from the account list.

**C5 — PublishedItemsPage:** header §J; native status beside ERROR; variation count; "Estoque: não
sincronizado"; safe error text; linkage-source badge; external link (`rel="noopener noreferrer"`,
`target="_blank"`) only for `listingUrl`; for LINKED rows with `externalSku`, an explicit
"Confirmar SKU → Produto para esta conta" action (Owner/Operator) that calls the mapping endpoint.
Manual link/unlink unchanged.

## Stream D — verification (mandatory, deterministic, no sleeps, no internet)

**D1 — S8C.1 regression** (A4 list) green after A1–A3 and again at the end.

**D2 — G01 refresh matrix.** One test per §F.3 row 1–19 through the real workflow, real
`LocalProtectedCredentialStore` (with fault-injecting decorator for rows 18–19), real named client and
a scripted primary handler (throw before/after invocation, truncated body, status codes). Each test
asserts: provider invocation count; old refresh-token usage count (handler records R1 values);
candidate bytes/version in the store; receipt identity; operation decision + safe code; connection
state; RequiredAction. Plus code-exchange tests: post-invocation exceptions follow the ADR-0024
fail-closed path, pre-invocation `REDIRECT_PATH_INVALID` is NOT_SENT; cancellation after invocation
with cleanup-token terminalization; terminal-commit failure leaves the operation PENDING and
execution blocked.

**D3 — G02 stale worker race** (real PostgreSQL, barriers, `TestClock`):
1. A claims (T1), completes a provider detail read, and pauses at a barrier immediately before
   `ExecuteFencedAsync` for its chunk.
2. Advance the clock past `lease_until`.
3. B reclaims (T2 ≠ T1, `attempt_count` 2) and commits a valid chunk for the same items.
4. Release A: its guard affects 0 rows; A throws `ListingSyncLeaseLostException`.
Assert unchanged after step 4: listing rows (facts, sync state, Version), observations, run items
(outcomes, attempt counts), run counters/phase/status, account/connection, AuditLog rows. Variant:
A paused **after** its guard inside the transaction — B's reclaim blocks on the row lock and, after
A commits, re-evaluates `lease_until`; no stale write is possible either way. Also: a stale token
cannot finish or terminalize a run; an old token never validates after reclaim.

**D4 — G03 failed identity:** 100 enumerated; 95 FOUND; 5 previously unknown IDs return element 500
twice ⇒ `TRANSIENT_ERROR`, no listing rows. Run = PARTIAL, counters found 95 / blockingRetryable 5.
Restart the host (new DI container, same DB): the 5 items persist. `POST retry` ⇒ new FAILED_ONLY run
with `retry_of_run_id` = original, exactly 5 `RETRY_TARGET` items, no enumeration call; handler now
returns them ⇒ 5 FOUND, listings created, new run SUCCEEDED, original run and items unchanged,
summary HEALTHY.

**D5 — G04 SKU authority:** manual link does not create a mapping (and a later import with the same
SKU stays UNLINKED); explicit S→P ⇒ exact Product-only auto-link with `sku_mapping_id`; provider SKU
change on a linked listing keeps its link and the new SKU links nothing; deactivated mapping stops
future auto-link without unlinking history; two ACTIVE rows injected by raw SQL with the index
dropped in a disposable DB ⇒ NEEDS_REVIEW; inactive Product ⇒ NEEDS_REVIEW; `VariationCount > 1` ⇒
NEEDS_REVIEW; operator unlink ⇒ suppression prevents re-link; conflict/replace API; Viewer 403;
Offer never created or linked.

**D6 — G05 direct re-read matrix:** for a known listing absent from a complete enumeration and for
an unknown enumerated ID, each outcome FOUND, NOT_FOUND, ACCESS_DENIED, SELLER_MISMATCH,
TRANSIENT_ERROR, UNMAPPED/MALFORMED asserts: run item outcome/code/origin; listing facts and sync
state per §E.5 (or no row); run outcome per §E.6; summary health and `openBlockingItemCount`; retry
eligibility (items copied or not by retry). Also: incomplete enumeration ⇒ FAILED, no missing
reconciliation, no listing change; SUCCEEDED FULL leaves no listing of the account in ERROR.

**D7 — G06 chunking:** fake-handler test with 45 PENDING items asserts bulk requests of 20, 20, 5 IDs
and the exact frozen `attributes` string; `DetailBatchSize=7` produces 7-ID chunks. This proves
application chunking only; it is **not** evidence that ML accepts 20 (that is L-07).

**D8 — adapter contract tests** (`MercadoLivreConnectorContractTests`): auth URL/encoding; token
form fields; usable-2xx validation; `/users/me` identity/site checks and PII discarded; grant probe
200/403/500; every §F.6 row; Retry-After seconds/date/invalid/over-budget; 3-attempt budget;
cancellation; token POST never retried; scan first/continuation (latest scroll_id)/termination
(empty, null)/duplicates/invalid IDs/stall/cursor-invalid; bulk element outcomes incl. missing,
id/seller/site mismatch, and one response mixing 200, 404, 403, 500 and missing elements (mandatory
here; mixed responses are not a live pass condition); every §C.2 row and unknown/absent status ⇒ UNMAPPED_STATUS; native format;
SELLER_SKU only; price rules; permalink allow-list; title truncation; `last_updated` absent ⇒ null;
malformed JSON; 1 MiB cap.

**D9 — PostgreSQL and security:** migration fresh / S8C.1-head upgrade with seeded listings,
observations, links / down / re-upgrade / EF drift; first import; idempotent re-import (no
observation); A→B→A = 3 observations; concurrent manual `POST /published-items` uniqueness; manual
linkage and NEEDS_REVIEW preserved; one active run per account (409 with RunId); two accounts run
concurrently and independently; crash reclaim resumes without rescanning a completed enumeration;
exhausted attempts ⇒ FAILED with PENDING → TRANSIENT_ERROR; 429 stop ⇒ persisted
`RuntimeRetryAfterUntil`, start 503; account deactivated/disconnected mid-run ⇒ stop, listings
retained; retention keeps latest; account isolation. Security: captured logs, persisted AuditLog, DTOs
and DB rows contain no secret, token, code, state, scroll ID, URL query or `/users/me` PII;
architecture tests (no ML type/URL in Commerce; DTOs internal; fake handler absent from `Verce.Api`
closure; no `MERCADO_LIVRE` literal outside adapter, seed/migration, tests); Production + `Enabled=true`
fails startup; `Enabled=false` registers nothing.

**D10 — frontend Vitest:** connect ML; sync start (Owner, Operator); Viewer no buttons; polling;
PARTIAL shows retry and failed items; FAILED shows sync required action separately from the
authorization RequiredAction; health never "em dia" with open items; blocked reasons; SKU mapping
confirm/deactivate; Published Items fields, linkage-source badge, stock text.

**D11 — Playwright** (`tests/e2e/specs/s8c2-mercadolivre-listings.spec.ts`, via
`playwright.marketplace.config.ts` and `tests/Verce.Marketplaces.E2EHost`): the host sets
`Marketplaces:MercadoLivre:{Enabled=true, ClientId=900000001, ClientSecret=e2e-only-secret,
RedirectUri=<host origin>/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback}`, replaces
only the two named clients' primary handlers with `FakeMercadoLivreHttpHandler`, and maps test-only
routes `POST /__e2e__/mercadolivre/state` (fixture items/faults) and
`GET /__e2e__/mercadolivre/observations/{externalId}` (count). Playwright redirects navigation to
`https://auth.mercadolivre.com.br/authorization*` to the real callback with the fake code and original
state. Journey: connect → CONNECTED; sync → SUCCEEDED; Published Items lists items; second sync ⇒ no
duplicates, no observations; A→B ⇒ +1 observation, back to A ⇒ third; two unknown IDs fail ⇒ PARTIAL
with failed items shown; fault cleared ⇒ "Tentar novamente itens com erro" ⇒ new run SUCCEEDED, health
HEALTHY; confirm SKU mapping on a linked listing ⇒ a newly appearing listing with that SKU auto-links.
S8C.1 marketplace journeys and the main suite still pass. No real internet.

**D12 — live-checklist support (no domain semantics).** (a) An automated API integration test runs the
LIVE-PAGE-01 algorithm (implemented once as a test
helper and mirrored step by step in the operator procedure) against `/published-items` and
`/marketplace-listing-syncs/{runId}/items` with 150 locally visible listings of one account, choosing
X and Y so that X is beyond page 1 and Y is on a different page from X; the helper finds both, reports
`pages ≥ 2`, `missingCount = 0`, `duplicateCount = 0`, never exceeds `ceil(total/100)` requests, and
the test asserts identity-based lookup without asserting any order. (b) **Scanner self-test matrix.** Stream D delivers `tests/live-validation/Invoke-S8C2LeakScan.ps1`
(LIVE-SCAN-01) and the xUnit class `tests/Verce.IntegrationTests/LiveValidation/LeakScanSelfTests.cs`,
which runs the script with `pwsh -NoProfile -File` (PowerShell 7+) against synthetic fixtures only.
Every row below is mandatory. Common setup:

- **Disposable DB fixture:** a Testcontainers PostgreSQL container (random name) holding a database
  named `verce_s8c2_live`, migrated to head with the real EF migrator and seeded with safe rows:
  one account + connection (CONNECTED), one listing with normalized facts, one observation, one
  SUCCEEDED run and a PARTIAL run, run items including one `TRANSIENT_ERROR/LISTING_READ_FAILED`,
  one ACTIVE SKU mapping, and the generic `platform.audit_log` rows those writes produce.
- **Docker shim:** `tests/live-validation/fixtures/docker-shim.cmd`, passed as `-DockerExecutable`,
  records every invocation's arguments to a file and returns scripted `inspect` output; guard rows
  use only the shim, so no row ever contacts `verce-postgres`.
- **Synthetic secret store:** a temporary `secrets.json` passed as `-UserSecretsFile` whose
  `Marketplaces:MercadoLivre:ClientSecret` is the synthetic value `ST04-synthetic-client-secret-7f3a`
  (never a real secret).
- **Exit semantics:** `0` = `PASS` (every count 0), `1` = `FAIL` (≥ 1 match), `2` = `REFUSED` or
  `INPUT_INVALID` (a guard or argument error; nothing scanned). Stdout carries only the frozen summary
  JSON (LIVE-SCAN-01 "Output"); stderr stays empty.
- Unless stated, each FAIL fixture starts from the ST-01 clean fixture plus exactly the planted item.

| ID | Fixture | Surface | Expected result / exit | Safe evidence asserted (category + count only) |
|---|---|---|---|---|
| ST-01 | **Clean fixture.** Log: default ASP.NET console events only, including `info: Microsoft.AspNetCore.Authorization.DefaultAuthorizationService[1]` / `Authorization was successful.`, one cleanup-sweep message from `Verce.Infrastructure.Marketplaces.MarketplaceCleanupService`, and two provider-call events in the frozen template — `MarketplaceProviderCall provider=MERCADO_LIVRE operation=ITEMS_BULK status=200 classification=NONE certainty=SENT_AND_RESPONSE_RECEIVED attempt=1 durationMs=412 account=<uuid> correlation=<uuid> requestedIdCount=20 envelopeCount=20 usableEnvelopeCount=20 histogram=200:20` and an `OAUTH_REFRESH status=200` event (ML has no provider request ID, so none is logged). DB: the seeded disposable DB. API: saved normalized DTO JSON for `/listing-sync`, a run, its items, `/published-items`, `/connection`, `/sku-mappings` | logs, DB, API | `PASS`, exit 0. An implementation that flags every provider-call log line, or the ASP.NET "Authorization was successful." wording, fails this row (false-positive protection) | every category and every STRUCTURAL sub-counter = 0 on all three surfaces; artifact/row/response counts > 0 |
| ST-02 | **Authorization header leak.** Log event message `Authorization: Basic c3ludGhldGljLXN0MDI=`; DB: `platform.audit_log.new_values_json` row containing `{"authorization":"st02-synthetic"}`; API file with root property `"authorization":"st02-synthetic"` | each surface separately (three sub-cases) and all together | `FAIL`, exit 1 | `AUTH_HEADER ≥ 1` on the planted surface; API sub-case also `STRUCTURAL.API_PROPERTY ≥ 1`; no other category counts except these |
| ST-03 | **Bearer/token leak.** Log: `Bearer st03SyntheticTokenValue0001` and the words `access_token`, `refresh_token`; DB: `commerce.marketplace_listing.title_snapshot = 'APP_USR-ST03-SYNTHETIC'`; API: `"note":"TG-abcdef123456"` | each surface | `FAIL`, exit 1 | `BEARER`, `ACCESS_TOKEN`, `REFRESH_TOKEN` ≥ 1 where planted |
| ST-04 | **Client-secret exact leak.** The synthetic secret value is planted without any label (`note value ST04-synthetic-client-secret-7f3a`) once in the log, once in `commerce.marketplace_listing.sync_error`, once as an API string value | each surface | `FAIL`, exit 1 | `CLIENT_SECRET` known-secret hits = 1 per planted surface (no label pattern involved); ST-10 applies |
| ST-05 | **Raw provider DB payload.** (a) `platform.audit_log.new_values_json` containing the synthetic item JSON `{"seller_id":90000001,"site_id":"MLB","available_quantity":5,"status":"active"}`; (b) the test adds a column `raw_body text` to `commerce.marketplace_listing_observation` in the disposable DB (test setup, not the scanner) | DB | `FAIL`, exit 1 | (a) `RAW_RESPONSE ≥ 1`; (b) `STRUCTURAL.DB_COLUMN = 1` |
| ST-06 | **Raw provider LOG payload** (synthetic, non-secret, no token/Authorization/secret-like value, no real ML data). (a) A provider-call event with an extra field: `MarketplaceProviderCall provider=MERCADO_LIVRE operation=ITEMS_BULK status=200 … histogram=200:1 responseBody={"id":"MLB_TEST_001","title":"Synthetic Listing","status":"active","price":123.45}`; (b) an event from category `Verce.Infrastructure.Marketplaces.MercadoLivre.MercadoLivreConnector[0]` whose message is that same JSON; (c) an event from category `System.Net.Http.HttpClient.MercadoLivre.Api.ClientHandler[101]` | logs | `FAIL`, exit 1 | (a) and (b) `STRUCTURAL.LOG_BODY = 1` each **and** `RAW_RESPONSE = 0` (proves structural, not pattern, detection); (c) `STRUCTURAL.HTTPCLIENT_LOG = 1` |
| ST-07 | **Forbidden nested API property.** (a) `{"account":{"id":"safe-test-account","provider":{"diagnostics":{"raw_payload":{"id":"MLB_TEST_001","status":"active"}}}}}`; (b) `{"items":[{"diagnostics":{"response_body":{"synthetic":true}}}]}`; (c) `{"pages":[[{"x":{"rawBody":"st07-synthetic"}}]]}` (array inside array) | API | `FAIL`, exit 1 | `STRUCTURAL.API_PROPERTY = 1` per file; a root-only walker would report 0 and fails the row |
| ST-08 | **Database `verce` refused.** `-PostgresContainer st-disposable-pg -Database verce -ConfirmDisposable -DockerExecutable <shim>`; variants `-Database verce_other` and a valid name without `-ConfirmDisposable` | DB guard | `REFUSED`, exit 2, error codes `DATABASE_TARGET_FORBIDDEN`, `DATABASE_TARGET_NOT_DISPOSABLE`, `DISPOSABLE_CONFIRMATION_REQUIRED` respectively | shim invocation count = 0 (no `inspect`, no `exec`, no `psql`, no SQL); summary has no surface results |
| ST-09 | **Container `verce-postgres` refused.** (a) `-PostgresContainer verce-postgres -Database verce_s8c2_live -ConfirmDisposable -DockerExecutable <shim>` (also `VERCE-POSTGRES`); (b) `-PostgresContainer 1a2b3c4d5e6f` where the shim's `inspect --format {{.Name}}` returns `/verce-postgres` | DB guard | `REFUSED`, exit 2, error code `OPERATIONAL_CONTAINER_TARGET_FORBIDDEN` | (a) shim invocation count = 0; (b) exactly one `inspect` invocation and zero `exec`/`psql` invocations; zero connections, zero SQL, zero mutation |
| ST-10 | **No echo.** For every run of ST-02..ST-09, capture stdout, stderr and the working directory | all | assertion `PASS` | captured output validates against the frozen summary schema only; it contains none of: the synthetic secret, its SHA-256 (hex, either case) or Base64, `c3ludGhldGljLXN0MDI=`, `st02-synthetic`, `st03SyntheticTokenValue0001`, `APP_USR-ST03-SYNTHETIC`, `TG-abcdef123456`, `MLB_TEST_001`, `Synthetic Listing`, `st07-synthetic`; no new file is written |

Also mandatory for every DB-touching row (ST-01, ST-02..ST-05): the scanner runs `psql` with
`PGOPTIONS=-c default_transaction_read_only=on`, and the test asserts that the seeded tables' row
counts and `pg_stat_database` `tup_inserted/tup_updated/tup_deleted` for `verce_s8c2_live` are
unchanged across the scan.

The scanner and the complete ST-01..ST-10 matrix must be green (in D1–D12) **before** live L-14 is
executed; L-14 cannot be certified with an unverified scanner build.

## Live validation (manual, human; not CI)

The live gate is executed by a qualified operator after all automated gates are green. It needs no
S8C.2 design knowledge: every condition is prepared explicitly and every check has a fixed action,
expected result and evidence. **VERCE performs no Mercado Livre write at any point.** Every step that
creates, pauses, closes or edits a test listing is **test-fixture preparation** done outside VERCE in
the Mercado Livre test-seller UI; it is not S8C.2 functionality.

### Evidence rules

- Record in the worksheet below (a new "Live validation evidence" section of this handoff, filled by
  the operator): UTC timestamps, VERCE commit, ML test-user ID, ExternalListingIds of the fixture
  listings, VERCE RunIds/AccountId, result per check, and the listed observations.
- Never record credentials, tokens, authorization codes, state, scroll IDs, raw request/response
  bodies, emails or other personal data.
- Database evidence is read with plain `SELECT` statements against the **disposable** local database
  `verce_s8c2_live` of TP-4 only (never `verce-postgres`/`verce`). HTTP evidence comes from the VERCE UI, VERCE API
  responses in the browser developer tools, and the safe structured provider-call log (ADR-0025 §F.6).

### Test-fixture preparation (outside VERCE)

| ID | Preparation | Recorded in worksheet |
|---|---|---|
| TP-1 | In the ML DevCenter, the owner registers a developer app: PKCE **disabled**; functional permission "Publicação e sincronização" with read access; RedirectUri exactly the TP-4 callback URL (`https://<host>:<port>/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback`). If ML rejects `localhost`, the operator maps a hosts-file hostname to `127.0.0.1`, issues a locally trusted certificate for it with their standard tooling, and uses that origin for both the app and TP-4 | app ID (not the secret), RedirectUri origin, `localhost` accepted yes/no |
| TP-2 | With the owner's app token and the operator's own tooling, create a dedicated MLB test seller via `POST /users/test_user` (`{"site_id":"MLB"}`); keep its credentials in the operator's password manager only | test-user ID, creation date |
| TP-3 | Logged in as the test seller in the ML UI, create **N ≥ 22 active** test listings following ML-TEST rules (title "Item de Teste – Por favor, NÃO OFERTAR!", category "Outros", listing type not gold/gold_premium). Among them, name these fixtures: **X** (single variation, no SKU — future closed listing), **Y** (single variation, no SKU — future paused listing), **A1** and **A2** (both single variation, same `SELLER_SKU` = `VERCE-LV-SKU-1`), **B1..B3** (distinct `SELLER_SKU` values), **V** (≥ 2 variations), **U** (no `SELLER_SKU`). The seller has no other listings | N; ExternalListingId of X, Y, A1, A2, B1..B3, V, U; SKU and variation count of each |
| TP-4 | VERCE local Development host over HTTPS on the TP-1 origin; `Marketplaces:CredentialStoreRoot` set; User Secrets for `Marketplaces:MercadoLivre:ClientId/ClientSecret/RedirectUri`; `Enabled=true`; default `Marketplaces:ListingSync` options (`DetailBatchSize=20`); a disposable local PostgreSQL container that is **not** `verce-postgres`, holding a database named `verce_s8c2_live`; an Owner user; at least one active Product `P-LV-1`; an active Marketplace SalesChannel. A local evidence folder `%LIVE%` outside the repository; the host is started with its console output redirected to `%LIVE%\host.log` (for example `dotnet Verce.Api.dll … *> "$env:LIVE\host.log"`), which is the application-log surface of L-14; whenever a check reads a VERCE API response in the browser developer tools, the operator saves that response body ("Save response", body only, never a HAR) into `%LIVE%\api\` | commit, host origin, container name, database name, Product code |
| PREP-PAUSED-01 | Executed only when L-09 instructs: in the ML seller UI, pause listing Y | Y ExternalListingId, UTC time, action "paused" |
| PREP-CLOSED-01 | Executed only when L-09 instructs: in the ML seller UI, close/finish listing X (the UI's end-listing action) | X ExternalListingId, UTC time, action "closed" |

### Execution order

L-01 → L-02 → L-03 → L-04 → L-05 → L-06 → L-07 (evaluated on the L-06 run) → L-08 → L-10 → L-09
(with PREP-PAUSED-01 and PREP-CLOSED-01) → L-11 (evaluated on the L-09 run) → L-12 → L-13 → L-14.
Optional observations O-01/O-02 are recorded whenever they occur.

### Reusable live procedures

**LIVE-PAGE-01 — locate listings by identity across pages.** Used by L-06 and L-09. Endpoints and
contract (repository envelope `{items, page, pageSize, total}`, `pageSize` clamped to 1..100):
`GET /api/commerce/published-items?marketplaceAccountId=<AccountId>&page=<p>&pageSize=100` (field
`externalListingId`) and `GET /api/commerce/marketplace-listing-syncs/<RunId>/items?page=<p>&pageSize=100`
(field `externalListingId`). Precondition: the account has no active run (the panel shows a terminal
status), so no write happens during the traversal.

1. Input: the set T of target ExternalListingIds from the worksheet; set `found = {}`, `seen = {}`,
   `pages = 0`.
2. Request `page = 1`, `pageSize = 100`; record `total` from that response and compute
   `maxPages = max(1, ceil(total / 100))`.
3. For each returned item: if its `externalListingId` is already in `seen`, increment
   `duplicateCount`; add it to `seen`; if it is in T, add it to `found`. Increment `pages`.
4. Stop at the first of: `found = T`; the response `items` is empty; `items.length < pageSize`;
   `page = maxPages`. Otherwise request `page + 1` and repeat step 3. The traversal never exceeds
   `maxPages` requests.
5. Output (safe): `|T|`, `|found|`, `pages`, `missingCount = |T − found|`, `duplicateCount`; the IDs in
   `T − found` go only into the local worksheet.

API page order is not used as architecture truth; provider scan ordering remains UNKNOWN; the
procedure searches by identity, not position. Duplicate identity is additionally checked in the
database: `SELECT external_listing_id, count(*) FROM commerce.marketplace_listing WHERE
marketplace_account_id = '<AccountId>' GROUP BY external_listing_id HAVING count(*) > 1` returns zero
rows (the unique identity constraint makes any other result a defect).

**LIVE-SCAN-01 — leak scan of live-validation evidence.** Used by L-14. Stream D delivers the
validation-only script `tests/live-validation/Invoke-S8C2LeakScan.ps1` (never shipped, never run
against anything but TP-4 artifacts). Inputs: `%LIVE%\host.log` (surface A), the TP-4 database
(surface B) and `%LIVE%\api\*.json` (surface C). It prints only counts. Parameters: `-LogFile`,
`-ApiResponseDirectory`, `-PostgresContainer`, `-Database`, `-PostgresUser` (default `postgres`),
`-UserSecretsFile` (the local User Secrets `secrets.json`; key `Marketplaces:MercadoLivre:ClientSecret`),
`-ConfirmDisposable`, `-DockerExecutable` (default `docker`). The database is read only through
`<DockerExecutable> exec -i <PostgresContainer> psql -U <PostgresUser> -d <Database> -At` with
`PGOPTIONS=-c default_transaction_read_only=on`. Exit codes: `0` PASS, `1` FAIL, `2` REFUSED/INPUT_INVALID.

- **Database guard:** the script refuses to run unless the database name matches `^verce_s8c2_live$`,
  the container is not `verce-postgres`, and `-ConfirmDisposable` is passed. It issues only `SELECT`
  and `information_schema` reads; no mutation. Order, all before any database access: (1) the
  `-PostgresContainer` value equals `verce-postgres` case-insensitively ⇒ `OPERATIONAL_CONTAINER_TARGET_FORBIDDEN`;
  (2) `-Database` equals `verce` ⇒ `DATABASE_TARGET_FORBIDDEN`, otherwise not matching
  `^verce_s8c2_live$` ⇒ `DATABASE_TARGET_NOT_DISPOSABLE`; (3) no `-ConfirmDisposable` ⇒
  `DISPOSABLE_CONFIRMATION_REQUIRED`; (4) `<DockerExecutable> inspect --format {{.Name}} <PostgresContainer>`
  resolving to `/verce-postgres` (a container ID or alias) ⇒ `OPERATIONAL_CONTAINER_TARGET_FORBIDDEN`.
  Only then is `psql` invoked.
- **Forbidden categories and runtime patterns** (case-insensitive regular expressions, applied to log
  lines, to every text/`jsonb`-cast value of the scanned tables, and to API response text):

| Category | Patterns |
|---|---|
| AUTH_HEADER | `authorization\s*[:=]\s*\S`, `"authorization"\s*:`, `\bAuthorization\s*\[` (header serializations; plain ASP.NET category/label text such as "Authorization was successful" does not match these forms and is the only accepted "Authorization" wording) |
| BEARER | `\bbearer\s+[A-Za-z0-9._~+/=-]{8,}`, `APP_USR-` |
| ACCESS_TOKEN | `access_token`, `accessToken` |
| REFRESH_TOKEN | `refresh_token`, `refreshToken`, `\bTG-[0-9a-f]{6,}` |
| AUTH_CODE | `[?&]code=`, `grant_type=authorization_code`, `"code"\s*:\s*"TG-` |
| CLIENT_SECRET | `client_secret`, `clientSecret`, `ClientSecret\s*[:=]`, plus the known-secret fingerprint check below |
| TOKEN_ENDPOINT | `/oauth/token`, `grant_type=` |
| RAW_REQUEST | `search_type=scan`, `scroll_id`, `scrollId`, `ids=MLB`, `attributes=body\.`, `api\.mercadolibre\.com/` (request material and provider URLs are never logged or stored) |
| RAW_RESPONSE | `"seller_id"\s*:`, `"seller_address"`, `"available_quantity"`, `"sub_status"\s*:`, `"paging"\s*:`, `"results"\s*:\s*\[`, `"status_code"\s*:`, `"body"\s*:\s*\{`, `"site_id"\s*:`, `"nickname"\s*:`, `"email"\s*:`, `"identification"\s*:` (provider JSON key serializations; VERCE audit/DTO JSON uses its own property names) |

- **Known-secret detection without exposure:** the script reads the client secret directly from the
  local User Secrets store file into memory, computes its SHA-256, and slides a window of the secret's
  length over every scanned text, comparing SHA-256 of each window with that fingerprint. It never
  prints, logs or writes the secret or the fingerprint and reports only a match count; the secret is
  never typed into a search command. The fingerprint exists only in the script's memory.
- **Structural raw-payload checks** (in addition to patterns):
  - DB schema: `SELECT table_schema, table_name, column_name FROM information_schema.columns WHERE
    (table_schema = 'commerce' OR (table_schema = 'platform' AND table_name = 'audit_log')) AND
    column_name ~* '(^raw_|payload|_body$|^body$|response_body|request_body|access_token|refresh_token|client_secret|scroll_id)'`
    must return zero rows; scanned tables are exactly `commerce.marketplace_authorization_session`,
    `commerce.marketplace_account`, `commerce.marketplace_account_connection`,
    `commerce.marketplace_account_capability`, `commerce.marketplace_account_operation`,
    `commerce.marketplace_listing`, `commerce.marketplace_listing_observation`,
    `commerce.marketplace_listing_sync_run`, `commerce.marketplace_listing_sync_run_item`,
    `commerce.marketplace_account_sku_mapping` and `platform.audit_log` (all text, `varchar`, `char`
    and `jsonb` columns, generated from `information_schema`).
  - Logs: count lines from the category `System.Net.Http.HttpClient.MercadoLivre` (default HTTP logging
    is removed, ADR-0025 §F.4 — must be 0); every provider-call log event (operation
    `OAUTH_EXCHANGE|OAUTH_REFRESH|USERS_ME|ITEMS_SEARCH_PROBE|ITEMS_SCAN|ITEMS_BULK`) must contain no
    `{`-delimited serialized body (count of such events with a body marker must be 0). Precisely: the
    host uses the default ASP.NET console formatter, so an event is a header line matching
    `^(trce|dbug|info|warn|fail|crit): (?<category>[^\[]+)\[\d+\]` plus its indented message lines up to
    the next header. `STRUCTURAL.HTTPCLIENT_LOG` counts events whose category starts with
    `System.Net.Http.HttpClient.MercadoLivre`. `STRUCTURAL.LOG_BODY` counts (i) provider-call events —
    messages beginning `MarketplaceProviderCall ` in the frozen template of Stream A7 — that contain a key
    outside the frozen key set or any `{`, `[` or `"` character, and (ii) events whose category starts
    with `Verce.Infrastructure.Marketplaces` or `Verce.Modules.Commerce` whose message contains a JSON
    object/array literal (`[\{\[]\s*"[^"]+"\s*:`).
  - API: parse every saved JSON response and collect property names at any depth; names in
    `raw, rawBody, rawPayload, providerPayload, payload, body, requestBody, responseBody, token,
    accessToken, refreshToken, secret, clientSecret, credentialReference, credentialVersion,
    authorization, scrollId, stateHash, browserBindingHash, email` must count 0. The walker descends
    through objects and arrays at any depth (arrays of arrays included) and compares each property name
    case-insensitively after removing `_` and `-` (so `raw_payload` matches `rawPayload`); each hit
    counts in `STRUCTURAL.API_PROPERTY`.
- **Output:** per surface, the number of artifacts/lines/rows/responses scanned and a match count per
  category (AUTH_HEADER, BEARER, ACCESS_TOKEN, REFRESH_TOKEN, AUTH_CODE, CLIENT_SECRET incl. known-secret
  hits, TOKEN_ENDPOINT, RAW_REQUEST, RAW_RESPONSE, STRUCTURAL); overall PASS only if every count is 0.
  Matching content is never printed or copied. No raw body is ever stored to prove its absence.
  STRUCTURAL is reported as its sub-counters `DB_COLUMN`, `HTTPCLIENT_LOG`, `LOG_BODY`, `API_PROPERTY`.
  The only stdout is one JSON summary: `{"result":"PASS|FAIL|REFUSED|INPUT_INVALID","errorCode":<code or
  null>,"surfaces":[{"surface":"LOGS|DATABASE|API","artifactsScanned":n,"unitsScanned":n,"counts":{<category>:n,…}}]}`
  with no other keys.

### Checklist

Classification: **M** = Mandatory, **C** = Conditional, **O** = Optional (see "Completion gate").
There are 14 primary checks L-01..L-14: 13 Mandatory and 1 Conditional (L-11). O-01 and O-02 are
optional sub-observations, not L-checks.
"Failure consequence" F-PROVIDER means `LIVE_PROVIDER_CONTRACT_FINDING` (stop, return for controlled
architecture correction); F-SETUP means a fixture/environment error — correct the preparation and
re-run the check, documenting both attempts; F-DEFECT means a VERCE implementation defect — fix under
the frozen architecture and re-run the affected checks. In all failure cases S8C.2 stays
`IMPLEMENTED_PENDING_LIVE_PROVIDER_VALIDATION`.

| ID | Class | Purpose | Precondition | Action | Expected result | Safe evidence | Failure consequence |
|---|---|---|---|---|---|---|---|
| L-01 | M | Redirect registration works | TP-1 | Save the app with the RedirectUri in the ML DevCenter | Registration saved with exactly the TP-4 callback URL | origin used; `localhost` accepted yes/no; date | Rejected for every origin form: F-PROVIDER |
| L-02 | M | Real OAuth connect through the S8C.1 flow | L-01, TP-2, TP-4 | As Owner, open Contas de Marketplace → "Conectar conta"; choose Mercado Livre, the TP-4 channel, display name `LV Test`; "Autorizar com provedor"; log in to ML as the test seller; approve consent | Browser returns to `/marketplace-accounts?authorization=completed`; the account is listed with authorization CONNECTED and availability UNKNOWN; `GET /api/commerce/marketplace-authorizations/{sessionId}` shows `COMPLETED`/`AUTHORIZED` | UTC time; VERCE AccountId; session outcome code | Consent page error or callback without `code`: F-SETUP if TP-1 differs from ADR-0025 §A.2, else F-PROVIDER; workflow failure: F-DEFECT |
| L-03 | M | Token lifetime metadata | L-02 | `SELECT access_expires_at, identity_verified_at FROM commerce.marketplace_account_connection WHERE marketplace_account_id = '<AccountId>'` | `access_expires_at − identity_verified_at` between 5 h 55 min and 6 h (ML-AUTH `expires_in = 21600`) | the two timestamps and the difference | Different lifetime: F-PROVIDER |
| L-04 | M | Provider identity = test seller | L-02, TP-2 | In Contas de Marketplace read the account line `MERCADO_LIVRE · <externalAccountId>`; compare with the TP-2 test-user ID | Equal; no email, name or document is shown anywhere on the page | both IDs (equal) | Different ID: F-PROVIDER (identity contract) |
| L-05 | M | LISTINGS_READ grant evidence and probe | L-02 | Read "Capacidades verificadas" on the account line; select the account and click "Sondar disponibilidade"; then `SELECT capability_code, state, source FROM commerce.marketplace_account_capability WHERE marketplace_account_id = '<AccountId>'` | Before probe `LISTINGS_READ: GRANTED`; probe message "conta disponível", availability AVAILABLE; SQL shows `LISTINGS_READ / GRANTED / PROBE_INSPECTION` and the other seven `UNKNOWN` | capability rows; availability | `UNKNOWN` grant with TP-1 read permission present: F-PROVIDER; permission missing in TP-1: F-SETUP |
| L-06 | M | First FULL sync, enumeration completeness, import | L-05, TP-3 | (1) In the account's "Sincronização de anúncios" panel click "Sincronizar anúncios"; wait until the panel shows a terminal status. (2) `GET /api/commerce/marketplace-listing-syncs/{runId}`. (3) Run LIVE-PAGE-01 with T = all N TP-3 ExternalListingIds over the run-items endpoint of this RunId, then over Published Items for this account. (4) Run the LIVE-PAGE-01 duplicate query. (5) `SELECT count(*) FROM commerce.marketplace_listing_observation o JOIN commerce.marketplace_listing l ON l.id = o.marketplace_listing_id WHERE l.marketplace_account_id = '<AccountId>'` | Run `SUCCEEDED`; counters `total = N`, `found = N`, `listingsCreated = N`; run items: every TP-3 ID found, origin `ENUMERATED`, outcome `FOUND`; Published Items: every TP-3 ID found exactly once (`missingCount = 0`, `duplicateCount = 0`), all ACTIVE and `UNLINKED`; duplicate query returns zero rows; observation count = N (one initial observation per listing) | RunId; expected count N; found count (run items, Published Items); pages inspected; missing ID count; duplicate identity count; observation count | Missing TP-3 IDs or non-FOUND outcomes: F-PROVIDER (scan/detail contract) unless caused by F-SETUP; duplicates or wrong observation count: F-DEFECT |
| L-07 | M | Application batch 20 is accepted by ML | L-06 run (N ≥ 22 distinct valid active listings, all enumerated; a new account has no locally known listings, so the first detail chunk holds 20 enumerated IDs) | From the safe provider-call log of the L-06 run, take the first `ITEMS_BULK` entry | That entry: `requestedIdCount = 20`, HTTP 200, `envelopeCount = 20`, `usableEnvelopeCount = 20`, element histogram `{200: 20}`; the corresponding 20 items are `FOUND` | UTC time, test-user ID, RunId, `requestedIdCount`, HTTP status, envelope counts, histogram — no raw body, no IDs in the log | Any other result: F-PROVIDER. `DetailBatchSize` is **not** lowered silently; the official maximum stays UNKNOWN and a controlled architecture/config correction is opened |
| L-08 | M | Repeat sync is idempotent | L-06, no external change since | Click "Sincronizar anúncios" again; wait for terminal status; `SELECT count(*) FROM commerce.marketplace_listing_observation o JOIN commerce.marketplace_listing l ON l.id = o.marketplace_listing_id WHERE l.marketplace_account_id = '<AccountId>'` before and after | Run `SUCCEEDED`; counters `found = N`, `listingsCreated = 0`, `observationsAppended = 0`; observation count unchanged (N); Itens Publicados still N rows | RunId; counters; counts before/after | Duplicates or new observations: F-DEFECT |
| L-10 | M | SKU extraction and explicit SKU authority | L-08, TP-3, TP-4 Product `P-LV-1` | (1) In Itens Publicados compare SKU and variation count of A1, A2, B1..B3, V, U with the worksheet. (2) As Owner, "Vincular" A1 to `P-LV-1` (product only). (3) On A1 click "Confirmar SKU → Produto para esta conta". (4) Click "Sincronizar anúncios" and wait for terminal status | (1) A1, A2 show `VERCE-LV-SKU-1`; B1..B3 their SKUs; U and X/Y no SKU; V variation count ≥ 2. (2)–(3) A1 LINKED (manual); mapping listed as active. (4) Run `SUCCEEDED`; A2 LINKED to `P-LV-1` with badge "Vinculado automaticamente por mapeamento de SKU confirmado", no offer; B1..B3, U, V unchanged (`UNLINKED`) | RunId; SKU/variation table; linkage state/source of A1, A2 | Wrong SKU extraction: F-PROVIDER if the attribute shape differs from ADR-0025 §B.3, else F-DEFECT; wrong linkage: F-DEFECT |
| L-09 | M | Externally changed listings are handled after the change | L-10 complete; X and Y imported by L-06 and each has ≥ 1 observation (`SELECT count(*) FROM commerce.marketplace_listing_observation WHERE marketplace_listing_id = '<listing id>'` ≥ 1); their ExternalListingIds are in the worksheet | (1) Execute PREP-PAUSED-01 (pause Y) and PREP-CLOSED-01 (close X) in the ML UI. (2) Click "Sincronizar anúncios"; wait for terminal status. (3) Locate X and Y with LIVE-PAGE-01 (T = {X, Y}) over the run-items endpoint of this RunId — never assuming page 1 — stopping when both are found or the result set is exhausted; for each found item read its `marketplaceListingId` and the listing's observed/native status and sync state in Published Items (LIVE-PAGE-01 with T = {X, Y}) and its observation count by SQL. If X is not among this run's items at all, that is not a checklist failure of the procedure: X is locally known, so it must appear as `KNOWN_NOT_ENUMERATED` (Branch B); its complete absence from the run items is F-DEFECT | **Y**: item outcome `FOUND` (origin `ENUMERATED` or `KNOWN_NOT_ENUMERATED`); listing observed status `PAUSED`, native status begins `paused`; one new observation. **X**: an item exists; **Branch A** (origin `ENUMERATED`): outcome and listing state follow ADR-0025 §E.5 (FOUND ⇒ `INACTIVE`, native begins `closed`, one new observation); **Branch B** (origin `KNOWN_NOT_ENUMERATED`): the reconciliation direct re-read ran automatically — evaluate L-11. In both branches no listing state was changed from absence alone, and the run status/account health agree with §E.6/§E.8 | RunId; pages inspected; for X and Y: ExternalListingId, origin (enumerated yes/no), outcome, safe error code, observed/native status, listing sync state, observation count; run status; account health | Y not FOUND or not PAUSED: F-PROVIDER; state changed without a read, or incoherent run/health: F-DEFECT |
| L-11 | C | Known-but-absent listing gets the automatic direct re-read | X imported by L-06 **and** the L-09 run recorded X with origin `KNOWN_NOT_ENUMERATED`. Otherwise **NOT_APPLICABLE** with reason "provider behavior observed: closed listing still enumerated" | None beyond L-09: observe the reconciliation phase of the L-09 run for X (items endpoint and SQL on the listing) | Exactly one frozen outcome with ADR-0025 §E.5 effects: **FOUND** ⇒ facts applied (INACTIVE/closed), sync SYNCED, run unaffected; **NOT_FOUND** ⇒ listing state unchanged, sync ERROR `LISTING_NOT_FOUND_AT_PROVIDER`, blocking ⇒ run PARTIAL, health ATTENTION_REQUIRED, retryable; **ACCESS_DENIED** ⇒ unchanged, ERROR `LISTING_ACCESS_DENIED`, blocking, retryable; **SELLER_MISMATCH** ⇒ unchanged, ERROR `LISTING_SELLER_MISMATCH`, blocking, not retryable (security finding to record); **TRANSIENT_ERROR** ⇒ unchanged, ERROR `LISTING_READ_FAILED`, blocking, retryable; **UNKNOWN/MALFORMED** ⇒ unchanged, ERROR `LISTING_STATUS_UNMAPPED`/`LISTING_RESPONSE_INVALID`, `PERMANENT_ERROR`, not retryable. If retryable, click "Tentar novamente itens com erro" once and record the new run's outcome for X | RunId; X ExternalListingId; enumerated NO; direct read attempted YES; outcome; listing observed/sync state; item outcome; run terminal status; account health; retry eligibility (and retry RunId/outcome if run) | Effects differing from §E.5: F-DEFECT; SELLER_MISMATCH for the seller's own listing or UNKNOWN/MALFORMED shape: F-PROVIDER. NOT_FOUND alone is **not** a failure (it is the frozen blocking outcome; it informs debt S8C2-06) |
| L-12 | M | Real rotating refresh | L-02; wait until `now > access_expires_at − 300 s` (read L-03 query) | Click "Sincronizar anúncios"; wait for terminal status; `SELECT kind, decision, safe_result_code FROM commerce.marketplace_account_operation WHERE marketplace_account_id = '<AccountId>' AND kind = 'REFRESH' ORDER BY created_at DESC LIMIT 1`; re-run the L-03 query | A `REFRESH` operation `CONFIRMED` / `REFRESHED`; new `access_expires_at` ≈ refresh time + 6 h; authorization CONNECTED; the run is not FAILED with a credential code | operation decision/code; old and new `access_expires_at`; RunId and status | Refresh `FAIL_CLOSED` with a provider rejection or unknown outcome under normal conditions: F-PROVIDER; persistence/confirmation error: F-DEFECT |
| L-13 | M | Probe, disconnect and bound reconnect on the real provider | L-12 | (1) "Sondar disponibilidade". (2) "Desconectar conta". (3) Select the account, "Reautorizar", approve consent as the same test seller | (1) AVAILABLE. (2) REVOKED; the API returned 202 or 204. (3) Browser returns with `authorization=completed`; same VERCE AccountId and external ID, CONNECTED; Itens Publicados still shows the N listings with their linkage | AccountId, states after each step, listing count | Any deviation: F-DEFECT, or F-PROVIDER if the provider rejects the reconnect consent |
| L-14 | M | Verify that S8C.2 live validation produced no forbidden provider secret, credential header, authorization material, or raw provider payload in application logs, persisted database fields, or externally visible API responses | The scanner build used is the one whose D12(b) matrix ST-01..ST-10 is green; L-01..L-13 executed; the host has been stopped so `%LIVE%\host.log` is complete; `%LIVE%\api\` holds the responses saved during L-02..L-13 (at least: `/api/commerce/marketplace-accounts`, `/marketplace-accounts/{id}/connection`, `/marketplace-accounts/{id}/listing-sync`, `/marketplace-listing-syncs/{runId}`, `/marketplace-listing-syncs/{runId}/items`, `/published-items`, `/marketplace-accounts/{id}/sku-mappings`, `/marketplace-authorizations/{sessionId}`) | (1) Run LIVE-SCAN-01 with `-ConfirmDisposable` against surfaces A (`host.log`), B (`verce_s8c2_live`) and C (`api\*.json`). (2) `git status --short` in the repository | Every category count is 0 on every surface: AUTH_HEADER, BEARER, ACCESS_TOKEN, REFRESH_TOKEN, AUTH_CODE, CLIENT_SECRET (including known-secret hits), TOKEN_ENDPOINT, RAW_REQUEST, RAW_RESPONSE, STRUCTURAL (no raw/payload/body/token/secret/scroll column; no default HttpClient log lines; no provider-call event with a serialized body; no forbidden DTO property); `git status` shows no secret-bearing or evidence file inside the repository | UTC timestamp; RunIds of the live session; surfaces scanned; counts of log lines, tables/rows/columns and responses scanned; match count per category per surface; overall PASS/FAIL. Never secret values, matching excerpts or raw provider bodies | Any match: FAIL, security defect opened (F-DEFECT); S8C.2 stays `IMPLEMENTED_PENDING_LIVE_PROVIDER_VALIDATION` and is never marked COMPLETE until a fixed build passes L-14 again; no waiver inside the checklist |
| O-01 | O | Rate-limit behavior | whenever it occurs | Filter the safe provider-call log for `RATE_LIMITED` | Recorded only | count of 429 responses; whether `Retry-After` was present | Never affects completion |
| O-02 | O | Natural mixed bulk response | whenever a bulk entry's histogram has non-200 elements (e.g. L-11 direct re-read) | Record the entry | Recorded only | histogram | Never affects completion; the absence of a mixed response is not a failure |

Mixed per-element `/items/bulk` semantics (200/404/403/other/missing elements in one response) are a
**mandatory automated** contract test (D8), not a live pass condition. The "locally known and absent
from enumeration ⇒ direct re-read" branch is a **mandatory automated** integration test (D6); its live
evidence (L-11) is conditional on the provider producing the precondition.

## Completion gate

| Situation | Status |
|---|---|
| All automated gates (D1–D12, including the D12(b) scanner matrix ST-01..ST-10) green; live checklist not started or not finished | **`IMPLEMENTED_PENDING_LIVE_PROVIDER_VALIDATION`** |
| Any Mandatory check (L-01..L-10, L-12..L-14) not PASS | stays `IMPLEMENTED_PENDING_LIVE_PROVIDER_VALIDATION`; follow the row's failure consequence |
| A Conditional check (L-11) whose precondition did not materialize | `NOT_APPLICABLE` with the documented reason; does not block |
| A Conditional check whose precondition materialized but the result is not PASS | blocks like a Mandatory failure |
| Optional observations (O-01, O-02) | never affect the status |
| Any live behavior contradicting a frozen assumption (20 valid IDs rejected, OAuth/identity/refresh semantics, scan contract, bulk response shape) | `LIVE_PROVIDER_CONTRACT_FINDING`: stop, record safe evidence, return for controlled architecture correction; never work around it |
| All Mandatory PASS, every Conditional PASS or NOT_APPLICABLE with reason, no contract finding | live gate passed; an independent review of the recorded evidence may promote S8C.2 to **COMPLETE** |

## Gate map

| Finding / section | Stream | Proof |
|---|---|---|
| D-01..D-03, G01 (§F.2–F.3) | A1–A4 | D1, D2 |
| §A/§B/§F adapter + HTTP | A5–A8 | D8 |
| G02 fencing (§E.2) | B4 | D3 |
| G03 run items/retry (§E.4, §E.7) | B2, B5 | D4 |
| G04 SKU authority (§D) | B1, B7, C | D5 |
| G05 outcomes/summary (§E.5, §E.6, §E.8) | B2, B5, C | D6, D10 |
| G06 batch/live (§K) and live-checklist procedures | B6, D | D7, D12, live L-07, LIVE-PAGE-01, LIVE-SCAN-01 |
| §H data model | B3 | D9 |
| §J API/UI | C | D10, D11 |

Architecture decisions left for Sonnet: **NONE.** Fixed above or in ADR-0025: the NOT_SENT meaning
and send boundary, the refresh matrix, the fencing token and guard statement, where failed IDs live,
how retry resolves IDs, what creates SKU authority, how run outcome and summary are computed, how
missing-listing outcomes count, that 20 is an unvalidated application batch, and how L-07 executes.
Private helper naming and file layout inside the named namespaces follow repository conventions and
may not weaken any invariant. A contradiction with official provider behavior or ADR-0024 is recorded
as an OPEN DECISION, never resolved by choice.
