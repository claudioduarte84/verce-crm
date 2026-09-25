# S8C.1 — Consolidated Implementation Handoff

Status: architecture candidate pending final independent Astra regate. This is one future implementation
macro-mission. [ADR-0024](architecture/ADR-0024-s8c1-marketplace-connector-authorization-foundation.md)
is normative for all G-01..G-08; DATA-MODEL and STATE-MACHINES specify its exact schema/transitions.
No production code or migration is delivered by this architecture correction.

## Scope and fixed choices

Mandatory: two semantic provider ports (authorization and inspection), typed registry, local
protected store/CAS, session and credential-operation orchestration, safe errors/HTTP policy,
status/probe API/UI, isolated fake vertical journey and acceptance tests. Optional: none.
Deferred: every real provider adapter, cloud store, multi-instance provider execution, resource
sync/ingestion, live fees and S8C.2–S8C.6 DTOs. Preserve IChannelFeeProvider/ChannelFeeQuote and
LocalFeeRuleProvider unchanged. S8C.4 separately designs account-aware live quotes without fake
local rule IDs.

## Stream A — Commerce model, one migration and S8B upgrade

Implement DATA-MODEL's exact session, durable MarketplaceAccountOperation arbiter, owned
connection and grant metadata in one S8C.1 migration. Use explicit root Version and IOwnedBy
for connection mutations; session and technical operation each have their own concurrency token.
Implement state/browser SHA-256 hashes, conditional one-time claim and terminal checks/indexes.
Keep provider-account identity uniqueness and channel immutability. Connection stores only the
current confirmed operation ID/version, with MarketplaceAccount's current reference; remove the
proposed duplicate credential-operation marker fields. Operation rows carry kind, progress phase,
irreversible decision, cleanup state, candidate/previous reference and version, expected root
Version, account/session relationships and due cleanup metadata. No candidate is current.

One migration backfills all S8B accounts NOT_CONNECTED/UNKNOWN with null verified/version
metadata, clears arbitrary legacy credential references, removes ConnectionState/LastError/
generic LastFailureAt, and preserves Active, sync fields, IDs, channels, listings, observations,
offers and existing audit. Manual grants retain LEGACY_MANUAL provenance. No provider seeds
are promoted; FAKE is never in normal seeds. Update API generated contracts for the deliberate
legacy endpoint/state break. Migration down is conservative and cannot restore erased pointers;
re-auth is required after downgrade.

## Stream B — infrastructure and security orchestration

Create one Infrastructure.Marketplaces project with inward references; it is not another business
module. Registry and only the two required ports are composed at Api. No provider HTTP DTOs in
Commerce. Store owns actual secret version; Commerce confirms receipts. Public input can never
choose a credential reference. CreateStaged/UseConfirmed/CAS/Revoke/DeleteCandidate plus internal
bounded housekeeping metadata enumeration follow ADR typed outcomes and ownership checks.

Use the fixed external absolute CredentialStoreRoot and DataProtection:DevKeyDirectory with
RepositoryRoot/content/wwwroot/reparse validation, user ACLs, stable Verce3D application name and
marketplace DP purpose. One encrypted file per reference, per-operation staging, exclusive root
handle, independent store CAS lock, temp-flush-atomic-replace, no torn reads. Corrupt/lost material
returns typed failure and reauth, not startup deletion of account history. Confirmed lost K1
requires an explicit bound recovery operation and fresh K2 reference/version 1; never reset K1
to version 1. Old K1 cannot regain authority if restored from backup. Missing safe local root may
be recreated with validated ACL; unsafe path/ACL still fails startup. Preserve Production
certificate validation. Startup validates each CONNECTED account's current receipt/decryptability
in bounded batches before provider execution; unchecked status is projected unusable, and each
confirmed lost account independently becomes REAUTHORIZATION_REQUIRED/runtime UNKNOWN. Local
store and test fake are absent from normal Production registration.

Implement one-time CLAIMED callback, browser/actor/reconnect binding, staged installation and
one operation-row resolver for callback, refresh, compensation, startup and Quartz. Commit the
operation before provider code/refresh exchange. Lock operation -> account -> session through
final DB commit. A lost commit reply is resolved under the row lock, never by one negative read.
Operation CONFIRMED alone makes material executable with the matching current connection.
CONNECT_NEW/RECONNECT require their entire callback transaction; a store receipt never completes
them after restart. REFRESH may confirm the exact receipt only under account/reference/version
guards. DISCONNECT commits REVOKED and operation CONFIRMED before cleanup. Exchange/refresh never
use generic retries. One shared account-ID single-flight serializes refresh/reconnect,
reference-generation replacement and disconnect; the store separately locks the reference CAS;
after provider success, at most one extra local persistence attempt of identical R2, no second
provider call. Foreign CAS conflicts and after-send ambiguity fail closed.

Serialize callback exchanges per provider in the single-instance coordinator. A provider async
execution gate uses a shared lease across each final DB/credential guard and provider send;
callback takes its exclusive lease before EXTERNAL_IN_FLIGHT and holds it through terminal
resolution. Acquire the provider gate before account/reference locks. A late response may update
runtime/grants only if its confirmed operation and root Version remain current. A durable
provider-wide fence from pre-send EXTERNAL_IN_FLIGHT through terminal decision denies other
credential execution while returned identity is unknown. Duplicate CONNECT_NEW under
UNKNOWN/MAY_SUPERSEDE_EXISTING impact makes existing X REAUTHORIZATION_REQUIRED, including
inactive X; two new sessions for X can leave the winner requiring explicit reconnect. RECONNECT
X->Y never rebinds history and fails affected accounts closed. An unknown-identity crash fails
all accounts for that provider closed at startup before releasing the fence. Future adapters may
claim PRESERVES_EXISTING only with separately reviewed official evidence. Fake mode invalidates
the previous X credential deterministically; normal Production has no fake. After-send ambiguity
with unknown returned identity uses the same provider-wide fail-closed resolution as a crash,
before the fence may be released.

Quartz startup and every 15 minutes, batches 100, performs expiry, abandoned-claim resolution and
secret/session cleanup through the same terminal arbiter; no provider polling. Due-time/keyset
selection and exponential retry eligibility let healthy later rows progress past one undeletable
old row. Keep failed deletion evidence and alert after 24h.
Implement typed normalized failure/send certainty, local catalog-only messages, bounded recognized
external identifiers, callback/token query/body suppression, safe audit and read retry budgets.
Production cloud store and distributed coordination stay gated.

Fake lives in test-support/harness only. It uses real ports/registry, session DB, local protected
store and normal routes, plus disposable FAKE fixtures. Never put a fake enable switch, seed or
helper route into production. Add a fake HTTP handler to exercise the actual mapper/retry policy.
Do not mark session/operation entities generically auditable: emit explicit allowlisted transaction
audit and verify actual persistent AuditLog excludes hashes, browser nonce, protected handles,
references, code, tokens, provider text and PII. Explicitly suppress CredentialReference in the
existing auditable MarketplaceAccount's generic old/new JSON and changed-column payload too.

## Stream C — APIs, permissions and Marketplace Account UI

Implement the exact ADR route table: new authorization, bound reauthorize, provider callback,
Owner session result, safe connection status, disconnect and mandatory controlled probe.
Owner+antiforgery on mutations; callback uses session/browser/actor checks without requiring the
Owner cookie. Probe is the explicit grant-refresh/recovery action; separate grant-refresh
endpoint and scheduler are deferred. Do not implement an arbitrary returnUrl.

Retire account POST with 410 and no legacy body binding. Restrict PUT to DisplayName/Version,
reject supplied provider/external/channel/credential properties. Retire manual grant POST with
410. Preserve activation/deactivation and histories. Update generated DTOs, no credential
reference/version/hash/path in output.

UI shows intent, connected/unconfigured/reauth/revoked state, historical runtime timestamps,
grants and required action with local pt-BR labels. Pending auth and active credential operation
are derived workflow indicators. Owner connects/probes/reconnects/disconnects. Operator/Viewer
see safe reads only. Disconnect 202 means access already denied and local deletion pending;
204 means deletion confirmed. Test harness alone exposes fake selection.
Show safe `RECONNECT_EXISTING_ACCOUNT` for a duplicate new authorization and
`REAUTHORIZE_AFTER_STORE_LOSS` for confirmed K1/key loss; both use existing Owner routes and
never expose internal references. For a mistyped legacy ExternalAccountId, preserve/deactivate
its account and authorize the correct identity separately when allowed. Do not reassign history
without a later administrative decision. Ordinary business execution includes existence,
Active and Marketplace Kind of SalesChannel as well as account/capability/credential/runtime
guards. Provider-wide callback fencing also blocks probe/execution until resolved.

## Stream D — verification and hardening

Mandatory deterministic proofs, using IClock/barriers rather than sleeps:

1. Domain/state matrix: UNKNOWN first attempt, UNAVAILABLE probe recovery, reconnect preserves
   usable credentials before send, disconnect history and capability separation.
2. PostgreSQL: concurrent callback -> one exchange; actor/browser/provider mismatch; replay and
   expiry; concurrent sessions same identity; rollback/fresh-context conflict handling; root
   concurrency; late callback cannot defeat cleanup; audit transaction and staged compensation.
   Add real PostgreSQL barriers for callback commit response lost but committed, rollback, commit
   still pending while resolver starts, resolver versus normal completion terminal lock races,
   cleanup/startup racing callback, actor permission loss and terminal session. Every operation
   has exactly one terminal decision. REFRESH receipt without DB confirmation may recover only
   under its exact guards; RECONNECT/CONNECT_NEW receipt without full committed session must
   fail closed. No confirmed material may be deleted and no failed callback resurrected.
3. S8B upgrade fixture: real committed prior schema with accounts/listings/observations/offers,
   safe metadata backfill, pointers never interpreted, data preserved. Fresh DB, up/down on
   disposable DB and EF drift; down cannot restore discarded secret pointers.
4. Store: outside-path/ACL/reparse validation, restart with stable keys, changed/lost key ring,
   file corruption, torn-write prevention, root second-process refusal, CAS conflict, orphan
   staging cleanup and injected DB failure at every commit boundary.
   Begin at K1 version >1; separately lose file, corrupt ciphertext, lose local key ring and
   remove the credential root. Each affected account becomes REAUTHORIZATION_REQUIRED and an
   explicit bound recovery confirms K2/version 1. Probe succeeds, old K1 cannot execute even if
   backup/key ring reappears; crashes before/after K2 confirmation obey the operation arbiter.
5. Refresh: parallel callers one provider call, same-R2 retry recovers, unrecoverable failure
   reauth, matching versus alien receipt/CAS, definitive invalid refresh, proven pre-send failure,
   after-send ambiguity, restart with pending marker and waiter reload. No blind replay.
6. Security: Owner/Operator/Viewer, unsafe legacy fields rejected, 32-byte unpredictable states,
   browser binding, no code/state/error_description/PII/raw provider messages in in-memory
   logger sink, API, audit and DB; safe catalog text and bounded IDs. Inspect actual persisted
   AuditLog rows for StateHash, BrowserBindingHash, protected handles, CredentialReference, tokens, PII and
   provider raw errors. Fake invalidates previous credentials on same-identity new authorization;
   test active/inactive duplicate X, two new sessions to X, explicit reconnect X and X->Y mismatch,
   plus proven-preserving versus UNKNOWN impact. Barrier-test a business send racing callback
   fence establishment and a late response trying to restore availability after fail-closed.
   Test a permanently failing cleanup row followed
   by healthy due rows; later rows must progress.
7. HTTP: actual factory/message-handler pipeline proves 429 and both Retry-After forms, 5xx/4xx,
   bounded attempts/budget, cancellation and no retry of token POST/ambiguous writes.
8. Production host: no fake registration/routes/seed; injecting fake fails startup. Preserve
   existing production certificate validation. E2E test fixtures never target the normal DB.
9. Full vertical E2E: Owner fake start -> API restart -> callback -> verified account/credential/
   grants, CONNECTED + UNKNOWN -> probe AVAILABLE -> expiry/concurrent refresh -> unrecoverable
   store write -> REAUTHORIZATION_REQUIRED -> bound reconnect -> disconnect with deletion and
   retained history. Restart also proves confirmed credentials survive intact store/key ring.
   Extend with K1 version >1 -> store/key loss -> REAUTHORIZATION_REQUIRED -> explicit K2
   recovery -> CONNECTED -> successful probe -> old K1 backup restoration remains non-executable.
10. Run repository architecture/unit/integration/frontend/OpenAPI/typecheck/lint/build and isolated
    Playwright gates; report raw failures. No internet, seller credentials or real provider login.

## G-01..G-08 acceptance map

| Gate | Normative ADR sections | Proof |
|---|---|---|
| G-01 | 2 | session binding/claim/races/compensation/Quartz |
| G-02 | 3 | typed ownership/CAS/atomic files/keys/loss |
| G-03 | 4 | receipt and send-certainty failure matrix |
| G-04 | 5 | minimal states and UNKNOWN/probe behavior |
| G-05 | 6 | legacy upgrade/DTO retirement/history |
| G-06 | 7 | test-only composition and Production absence |
| G-07 | 8 | fee code unchanged; S8C.4 contract gate |
| G-08 | 9 | local catalog/query suppression/captured-log tests |

Architecture decisions left for implementation: NONE in this corrected candidate. The normative
RT-01/RT-02/RT-03 matrices are in ADR-0024; Stream D must assert their exact operation decision,
connection state, candidate action and execution permission. Library/helper naming and ordinary
coding details may follow repository conventions, but none may weaken these invariants. Independent
Astra final regate must accept this candidate before the implementation macro-mission is authorized.

## Implementation session evidence (2026-09-25, non-binding progress note)

This section records what one implementation session actually verified; it does not amend the
normative text above and does not itself constitute the independent regate Stream D requires.

Done and verified with real PostgreSQL (Testcontainers) and a real disposable database: Stream A's
migration (fresh zero→head, S8B-fixture upgrade with data preservation, and down→up round trip);
most of Stream B's session/operation/credential-store/callback/refresh/probe/disconnect/housekeeping
machinery, including a corrected async `ProviderExecutionGate` and a resolver-vs-callback row-lock
race fix; most of Stream C's route table (begin, reauthorize, callback, status, connection, probe,
cancel, disconnect, 410 retirement); a test-only fake connector. Full backend suite (unit, domain,
architecture, 405 integration tests) green; frontend vitest/typecheck/lint/build/OpenAPI-check green.

## Second implementation session evidence (2026-09-25, gap-closing pass)

A second session closed most of the "not done" list above. Now also done and verified with real
PostgreSQL/real filesystem/real browser: the RT-02 matrix (8 tests — duplicate identity active/
inactive X, two concurrent CONNECT_NEW sessions, RECONNECT X→X and X→Y mismatch,
PRESERVES_EXISTING vs UNKNOWN/MAY_SUPERSEDE impact, ambiguous-after-send fail-all-provider-accounts,
proven-not-sent preserves unrelated accounts); the RT-03 matrix (6 tests — K1 missing/corrupt,
unconfirmed-higher-K1 never CAS'd from, old K1 restored-after-K2 never regains authority, missing
credential root recovers, multi-account key-ring loss recovers independently); the credential-store
fault-injection suite (18 tests); callback security (9 tests, including a real in-memory captured-log
assertion); audit security (2 tests against actual persisted `AuditLog` rows); housekeeping
fairness (1 test); production isolation (4 tests, including a new fail-closed startup guard against
a misconfigured test-only connector). The Marketplace Account frontend UI was extended with real
connection-status detail, a Probe action, and distinct duplicate-identity/store-loss prompts (10
component tests, all passing). The full pre-existing Playwright suite — including a spec legitimately
updated because it asserted the now-410'd manual account-creation UI — passes 41/41 against a real
disposable Postgres container, a real compiled `Verce.Api` process and a real Chromium browser.

**Still not done, and — for the Playwright authorization journeys specifically — provably not
buildable without a new architecture decision:** the focused/store-loss/duplicate-auth Playwright
journeys the original mission asked for cannot be driven through this repository's existing
Playwright harness, because it exercises the real unmodified `Verce.Api` process and G-06
deliberately keeps the fake connector out of that process's dependency graph. See the OPEN
DECISION below for the proposed extension point — **not implemented, for human/architecture
review only.** Refresh's full send-certainty/retry-once matrix (beyond RT-02's proven-not-sent
distinction) and any HTTP 429/Retry-After/retry-budget proof remain undone — see
`docs/ARCHITECTURE-DEBT.md`'s "Before any real (non-fake) marketplace connector adapter" entry for
why that is a deliberate, recorded deferral, not an oversight.

## Third implementation session evidence (2026-09-25, RT-01 extension + deferral documentation)

A human explicitly reviewed the second session's report and approved a third, focused round: (1)
extend RT-01 as far as practical using the established barrier pattern; (2) do NOT implement G-08's
HTTP layer — record its deferral in `docs/ARCHITECTURE-DEBT.md` instead (done — see that file's
"Before any real (non-fake) marketplace connector adapter" entry); (3) do NOT design/implement a
test-only connector-injection seam into the real `Verce.Api` host — write it up as an OPEN DECISION
for human review instead (done — see the section immediately below).

RT-01 went from 2 to **10 of ~14** listed scenarios, all against real PostgreSQL, using two new
techniques beyond the barrier already proven last session: (a) a raw `NpgsqlConnection`-held
`FOR UPDATE` transaction that lets a test prove a concurrent resolver call genuinely BLOCKS at the
PostgreSQL level (not merely "returns the right answer eventually") before releasing it as either
a committed CONFIRMED mutation or a rollback, certifying both "commit succeeds but reply lost" and
"commit rolls back" from the SAME underlying row-lock-arbitration proof; (b) `TestClock`-driven
abandonment for the bounded housekeeping sweep, certifying "startup finds a receipt without a
committed session" as a real sweep-query outcome rather than a direct `ResolvePendingAsync` call.
This surfaced and fixed one real production gap — `ResolvePendingAsync` had no REFRESH-receipt-only
confirmation path (ADR-0024 explicitly allows one; the implementation always failed REFRESH
recovery closed instead, which was safe but not literally ADR-complete) — now implemented with the
exact guard set the ADR specifies (root Version, previous reference/version, candidate
reference/version all matching exactly) and certified by two new tests (valid guards confirm;
incompatible guards fail closed). It also surfaced one real TEST-infrastructure gap (not a
production defect): `PostgresFixture.CreateContext()`'s raw `DbContextOptionsBuilder` never
attaches `AggregateVersionInterceptor`, so a raw-context domain mutation silently does not bump
`AggregateRoot.Version` the way the production `IUnitOfWork` pipeline always does — two new tests
initially failed against this until rewritten to drive the mutation through the real `PUT`
endpoint instead. Full regression re-run at the end: Release build 0/0; full non-integration
backend suite green; full `Verce.IntegrationTests` **461/461**; full Playwright **41/41**.

## RESOLVED — browser-level Playwright marketplace journeys (fourth session, 2026-09-25)

**Status: implemented and passing.** The OPEN DECISION below (preserved for its historical
record — what was considered and explicitly rejected) proposed loading the fake connector INTO
the real `Verce.Api` process via an opt-in environment switch, `Assembly.LoadFrom` and a startup
validation gate. The human reviewed that proposal and explicitly declined it — "Preserve
ADR-0024/G-06 fully. Do not authorize a fake-injection seam in the Production host/artifact" —
and instead directed a structurally different approach that needs none of the new attack surface
listed below: a **separate, test-owned executable**, `tests/Verce.Marketplaces.E2EHost`, that
reuses `Verce.Api`'s own real `Program` class (the same `WebApplicationFactory<Program>` reuse
mechanism the 27 in-process integration tests already use) but binds a genuine Kestrel server
instead of the in-memory `TestServer` transport, so a real Chromium browser driven by Playwright
can reach it over real sockets. `Verce.Api.csproj` gained **zero** new code, no new environment
switch, no new endpoint, and no new `if (Development)` branch — every existing composition
decision (`AddVerceMarketplaceInfrastructure`'s Development+`CredentialStoreRoot`-opt-in gate,
`MarketplaceProductionIsolationTests`) is exactly what it was before this session and is what
already made this possible; the fake connector is wired in purely through EXTERNAL DI
substitution (`WebApplicationFactory.ConfigureTestServices`, the identical mechanism
`MarketplaceTestHarness` already used for RT-01/02/03), which only a caller holding a
`WebApplicationFactory<Program>` builder — never a real deployed `Verce.Api.dll`/exe — can invoke
at all. `MarketplaceProductionIsolationTests` gained a new static/compile-time assertion
(`Verce_Api_assembly_never_references_any_project_the_fake_connector_lives_in`) that `Verce.Api`'s
own compiled assembly's `GetReferencedAssemblies()` never names `Verce.IntegrationTests` or
`Verce.Marketplaces.E2EHost`, and that no transitively-loaded assembly in `Verce.Api`'s reference
closure defines a `FakeMarketplaceConnector` type — passing, confirming G-06's isolation guarantee
survives this new project's existence.

The new host's own `Program.cs` maps exactly one test-only control route,
`POST /__e2e__/marketplace/scenarios/{sessionId}` (via a project-local `IStartupFilter`, never
touching `Verce.Api`'s pipeline), matching G-06's own text: "Any deterministic fault/consent
helper is mapped by the isolated test host only; Production has no such routes." It also inserts
one test-only `MarketplaceProvider` catalog row (`FAKE`/"Fake E2E Provider") into its own
disposable database after real seeding completes, so the real frontend's provider dropdown has
something to select — again matching G-06: "Test host registers it through the real
registry/ports and inserts test-only FAKE provider/capability fixtures into its disposable
PostgreSQL. Normal seed has no FAKE row." (confirmed: `CommerceSeedService`, Verce.Api's own real
seed, was not touched and still seeds only MERCADO_LIVRE/SHOPEE/TIKTOK_SHOP).

A new, separate Playwright config, `tests/e2e/playwright.marketplace.config.ts`
(`npm run test:marketplace`), points its `webServer` at this new host instead of
`src/Verce.Api`, reusing `e2e-env.cjs`'s identical hardened PostgreSQL-safety contract (same
`VERCE_E2E_POSTGRES_CONTAINER` requirement, same protected-container/protected-database checks,
same run-lock). The existing `playwright.config.ts` (and its 41/41 suite) is unchanged except one
line excluding the new spec file from its own default project — it still targets the real,
unmodified `Verce.Api` artifact exactly as before. `tests/e2e/specs/s8c1-marketplace.spec.ts`
implements all three mandated journeys, each via real form fills, a real POST, a real
`page.route`-intercepted top-level navigation to the fake's non-resolvable
`fake-marketplace.test` authorization URL redirected (absolute, same-origin as the SPA) straight
to `Verce.Api`'s own real callback route, and real assertions against the rendered UI:

- **Focused flow** — connect a new account end to end, then a voluntary "Reautorizar" reconnect,
  both landing CONNECTED with no error state.
- **Store-loss K1→K2 recovery** — after a real connect, Node deletes the confirmed credential
  envelope file(s) directly from the isolated credential-store root (harness-side fault
  injection, explicitly sanctioned by the mandate), then drives a real "Sondar disponibilidade"
  probe through the UI and asserts the account correctly shows `REAUTHORIZE_AFTER_STORE_LOSS`
  guidance, then drives the real "Recuperar credencial (reautorizar)" recovery button to a
  successful reconnect, and confirms a NEW confirmed credential file exists afterward (never a
  silent reuse of the lost one).
- **Duplicate authorization** — two separate "Conectar conta" flows for the same external
  identity: the second is real-HTTP-confirmed to fail closed, no second account is ever created,
  and the first account is shown with the real `RECONNECT_EXISTING_ACCOUNT` guidance and button.

**Two genuine implementation defects were found and fixed by these journeys** — neither was ever
reachable by the 27 in-process tests, which never drive a real browser through a real redirect or
read the rendered UI:
1. `CommerceEndpoints.cs`'s callback route redirected to `/commerce/marketplace-accounts`, a path
   with no matching React Router route (`AppRoutes.tsx` only registers `/marketplace-accounts`) —
   every real authorization/reauthorization completion, success or failure, silently stranded the
   Owner's browser on `NotFoundPage` instead of the real Marketplace Accounts screen. Fixed to
   redirect to the real route; the one integration test that had hardcoded the old, wrong target
   (`MarketplaceCallbackSecurityTests.Callback_never_redirects_anywhere_other_than_the_fixed_internal_result_route`)
   was updated to the corrected expectation.
2. `MarketplaceAuthorizationWorkflow.ProbeAsync`'s credential-unreadable branch called
   `RecordRuntimeFailure` (stays `CONNECTED`, `RuntimeAvailability=UNAVAILABLE`) instead of
   `RequireReauthorization` for a genuinely CONFIRMED loss (`NOT_FOUND`/`CORRUPTED_OR_UNDECRYPTABLE`,
   as opposed to a merely transient `STORE_UNAVAILABLE`) — `CommerceEndpoints.RequiredAction`'s
   switch only ever surfaces `REAUTHORIZE_AFTER_STORE_LOSS` when `AuthorizationState` is
   `REAUTHORIZATION_REQUIRED`, which `RecordRuntimeFailure` never sets, so a probe against a
   confirmed-lost local credential incorrectly stayed `CONNECTED` and told the Owner to "check the
   provider account" (`CHECK_PROVIDER_ACCOUNT`) instead of routing them to the real
   store-loss-recovery button and copy that already existed in the frontend for exactly this case.
   Fixed to escalate via `RequireReauthorization("PROBE_CREDENTIAL_UNREADABLE", ...)` for the two
   confirmed-loss outcomes specifically. A related frontend staleness gap surfaced while writing
   the store-loss journey: `MarketplaceAccountsPage.tsx`'s `probe()` handler refreshed the account
   LIST but never the currently-`selected` account object, so the very next action taken on that
   account (reconnecting, right after a probe reveals store loss) would send a now-stale `Version`
   and fail with a spurious concurrency conflict instead of actually starting reauthorization —
   fixed to refresh `selected` from the reloaded list after every probe.

Full regression re-run after all of the above: Release build 0 warnings/0 errors; full
non-integration backend suite (543 tests) green; full `Verce.IntegrationTests` **462/462**
(461 + the new static isolation test); full frontend Vitest suite **129/129**, typecheck, lint,
build all clean; full original Playwright suite **41/41** (unchanged, still against the real
`Verce.Api` artifact); new marketplace Playwright suite **4/4** (1 setup + all 3 mandated
journeys), run three times for stability with no flakes observed.

## OPEN DECISION (historical — superseded above) — proposed test-only connector-injection seam for browser-level Playwright (NOT implemented)

**Status: proposal only, for human + architecture review. No code in this repository implements
any part of this section — and per the resolution above, none ever will; the human explicitly
declined this specific approach in favor of the separate-host design documented above.** Recorded
here because the third gap-closing session was explicitly instructed not to design or build this
seam itself — "architecture is frozen; only the human + an ADR can change it" — but to write up
what it would require, so the human could decide. Preserved verbatim for the record.

**The problem.** `tests/e2e`'s Playwright harness drives the REAL, unmodified `Verce.Api` binary as
a separate OS process (`dotnet run --no-build --configuration Release --project src/Verce.Api`).
ADR-0024 G-06 requires "no Development enable-fake switch in the production app" and "no fake code
is shipped in its project dependency graph" — `Verce.Api`/`Verce.Infrastructure.Marketplaces` never
reference `Verce.IntegrationTests`'s `FakeMarketplaceConnector`, by design. The two facts together
mean there is currently no live or fake provider a real browser driving the real host can complete
a marketplace authorization against, so genuine end-to-end coverage of the callback/RT-01/02/03
behavior through an actual browser is unreachable with today's architecture. (Equivalent coverage
exists today via 27 in-process `WebApplicationFactory`-based PostgreSQL integration tests, which
DO wire the fake connector into a real ASP.NET Core `TestServer` host's DI container — just not a
separately-spawned OS process a real browser talks to over a real socket.)

**What an extension point would need to look like**, sketched at the level of "what a future ADR
would have to decide," not an implementation plan:
1. A narrow, explicit opt-in switch distinct from `ASPNETCORE_ENVIRONMENT=Development` — e.g. a
   SEPARATE environment variable (`VERCE_E2E_ENABLE_FAKE_MARKETPLACE_CONNECTOR` or similar) that
   `Program.cs` checks in addition to `IsDevelopment()`, so a Development host never silently picks
   this up just by being Development (today's local-store activation already conflates "opt-in
   root configured" with Development; a second orthogonal flag would be needed here, not reused).
2. The fake connector assembly would need to be loadable by the real `Verce.Api` process WITHOUT
   `Verce.Api.csproj` carrying a compile-time `ProjectReference` to a test project — e.g. via
   `Assembly.LoadFrom` against a path supplied only through that same opt-in environment variable,
   so a normal `dotnet publish` never bundles it and a normal `dotnet build`/`dotnet run` without
   the variable set never touches it.
3. The loaded assembly would need to be validated at startup (checksum/strong-name/an explicit
   marker attribute) before being allowed to register an `IMarketplaceAuthorizationConnector`, so
   an attacker who could set an environment variable on the box could not point this loader at an
   arbitrary DLL and get arbitrary code executed inside the real host process — this is the single
   biggest new attack surface the seam would introduce and the main reason it was not built
   under this mission's "no architecture work" constraint.
4. `Program.cs`'s own Production-startup validation would need an explicit, tested assertion that
   this variable is never honored when `IsProduction()` — not merely "the variable is never set in
   Production by convention" — mirroring the same fail-closed discipline
   `MarketplaceProductionIsolationTests` already proves for the in-process DI registration path.
5. The E2E harness's OWN safety contract (`tests/e2e/e2e-env.cjs`'s protected-container/protected-
   database checks) would need no changes — this proposal only concerns which PROVIDER the real
   host can complete an authorization against, never which PostgreSQL target it runs against.

**Risk this seam would introduce, stated plainly:** any mechanism that lets an external signal
(env var, file path, config value) cause the real production binary to load and execute code it
did not ship with is, by definition, a new class of attack surface — even when gated to
Development/opt-in-only, because "gated to Development" is exactly the kind of guard that has
historically been bypassed by a misconfigured deployment (this is the same risk class ADR-0024
G-06 and CLAUDE.md rule 25/33 already treat as unacceptable without explicit, reviewed
justification). It is also, independently, a widening of `Verce.Api`'s trusted dependency
surface at runtime, which cuts against CLAUDE.md's "no visual asset or template is hard-coded... a
closed catalogue" posture applied to connector registration generally.

**Recommendation for the human/architecture reviewer:** decide whether genuine browser-level E2E
coverage of the S8C.1 authorization flow is valuable enough to justify a reviewed ADR addendum
implementing something like the above — or whether the existing 27 in-process integration tests
(RT-01/02/03) plus the 41/41 unmodified-host Playwright suite are accepted as sufficient
certification evidence for this area, in which case this OPEN DECISION can be closed as "accepted,
no seam built" rather than actioned. Either answer is a legitimate outcome of this review; this
implementation session took neither position and built neither.

## Fifth session evidence (2026-09-25, Codex Sol regate response — M-S8C1-001/002/003)

An independent review ("Codex Sol") of the fourth session's delivery returned NEEDS_FIXES with
exactly three blocking findings, none of them touching architecture (RT-01/02/03/credential-store/
callback-security/audit/production-isolation/frontend were all independently accepted as PASS and
were deliberately left untouched this session). All three are now closed with real evidence.

**M-S8C1-001 — migration certification lacked real evidence.** Added
`tests/Verce.IntegrationTests/S8C1/S8BToS8C1UpgradeTests.cs`, two hermetic tests each owning their
own disposable Testcontainers PostgreSQL container:
- `Phase1_fresh_database_migrates_zero_to_head_including_S8C1` — a fresh, empty database migrates
  zero-to-head cleanly, S8C.1 included, no pending migrations left. PASS.
- `Phases2to4_S8B_predecessor_data_upgrades_to_S8C1_fail_closed_then_downgrades_then_reapplies` — a
  SEPARATE database is brought to the real, exact S8B predecessor schema via EF's own migrator
  (`IMigrator.MigrateAsync("20260923131458_AddS8BCommerceFoundation")`, never hand-built partial
  tables), seeded via raw SQL against the real pre-S8C.1 column set (the current C# entity model
  cannot even express `connection_state` anymore, so raw SQL is the only correct way to seed the
  predecessor shape) with one MarketplaceProvider, one MarketplaceAccount carrying legacy
  `connection_state='CONNECTED'`/`last_error`/`credential_reference` values that are obviously
  fictitious markers (`LEGACY-FICTITIOUS-CREDENTIAL-REFERENCE-DO-NOT-USE`, never a real secret),
  one MarketplaceAccountCapability, one ChannelOffer, one MarketplaceListing (LINKED) and one
  MarketplaceListingObservation, plus the required SalesChannel/Product master data via EF
  entities. Then: applies ONLY S8C.1 and asserts every row/FK relationship survives, PLUS the
  required fail-closed state (`NOT_CONNECTED`/`UNKNOWN`, `connection_state`/`last_error`/
  `last_failure_at` columns physically dropped, `credential_reference` cleared, no fabricated
  operation row, no `FAKE` provider row, `marketplace_account_capability.source` defaulted to
  `LEGACY_MANUAL` not silently promoted); migrates back down to the S8B predecessor and asserts the
  Down migration's ACTUAL documented behavior (legacy columns come back but at their safe-unknown
  defaults, never restored to their original values — Up() already discarded those irrecoverably,
  which is what ADR-0024 §6 itself says Down is allowed to do); reapplies S8C.1 on the same
  rolled-back database and confirms it succeeds again with the same fail-closed state re-seeded.
  PASS — both phases, all five sub-phases, first try. No second migration was needed; no genuine
  schema defect was found.

**M-S8C1-002 — `CommerceHttpIntegrationTests.Published_items_keep_the_account_channel_identity_when_cross_module_channel_data_is_unavailable`
was not hermetic.** It persisted a `MarketplaceAccount` with `ProviderCode="SHOPEE"` without first
persisting the `MarketplaceProvider` row the real `fk_marketplace_account_marketplace_provider_provider_code`
foreign key requires — a correct, unweakened production constraint; only the test was fixed, by
existence-checking and inserting the "SHOPEE" provider itself (mirroring `CommerceSeedService`'s
own idempotent pattern) rather than assuming another test's leftover seed. Proven hermetic: the
single test run completely alone — PASS; the full `CommerceHttpIntegrationTests` class — 8/8 PASS;
the full `Verce.IntegrationTests` suite — 464/464 PASS, no order-dependent FK failures anywhere.

**M-S8C1-003 — the main-suite Playwright run never produced a real final result.** Root-caused by
direct reproduction, not guesswork: `dotnet run --project ...` is a launcher process — Playwright's
webServer teardown only ever held a handle to the launcher, not the process actually holding the
port, mirroring exactly why `s2-restart-persistence.spec.ts` already invokes its own dedicated API
process by compiled DLL directly rather than via `dotnet run`. Both `playwright.config.ts` and
`playwright.marketplace.config.ts` were changed to do the same for their backend webServer entry.
Two real regressions were found and fixed while implementing this, both confirmed by direct
reproduction against a disposable container before being accepted as the final fix:
1. An earlier version of this fix additionally tried to move the frontend's `npm run build` step
   into the config module's own top-level code, reasoning that a one-shot build did not belong
   inside a long-running webServer's own process tree. This was wrong: Playwright re-evaluates the
   config module in every worker process (already documented by this file's own run-lock
   re-entrancy handling), so the build silently reran before nearly every test, and was observed
   to destabilize `vite preview` by rebuilding `dist/` while it was actively serving from it.
   Reverted; the frontend webServer entry's original `npm run build && npm run preview` chain is
   untouched — only the backend entry needed to change, since Playwright owns each `webServer`
   entry's process exactly once for the whole run regardless of worker count.
2. Direct DLL invocation does not read `Properties/launchSettings.json` (a `dotnet run`-only
   mechanism), so `ASPNETCORE_ENVIRONMENT=Development` — implicit under `dotnet run` — had to be
   set explicitly, or the host booted as Production and failed closed on the missing Data
   Protection certificate (correct Production behavior, not a bug). Beyond that, a RELATIVE
   `--contentRoot` value resolves against the DLL's own base directory
   (`AppContext.BaseDirectory`), not the process's working directory — a genuine .NET quirk
   confirmed by direct reproduction — so an ABSOLUTE `--contentRoot` pointing at `src/Verce.Api`
   was required. Getting this wrong left the host's content root at the repo root, where
   `appsettings.Development.json` does not exist (only its bin-output copy does); its
   `Settings:SeedOnStartup=true` silently never took effect, no seed data was ever created, and
   the resulting failures cascaded unpredictably across many unrelated specs that all assume real
   seeded data exists — reproduced directly (confirmed via a raw `psql` count against
   `settings.app_setting`: 0 rows without the fix, 18 rows with it) before being accepted as the
   diagnosis.

   After both fixes: the full main-suite Playwright run completed with a real, confirmed terminal
   result twice in a row — **41 passed (3.4m), exit code 0** both times — and the dedicated S8C.1
   marketplace suite completed with **4 passed (23.4s), exit code 0**. Process/port cleanliness
   was verified directly after each run (`Get-NetTCPConnection`/`Get-CimInstance Win32_Process`
   showed no leftover process on ports 7245-7249/4173 and no orphaned `Verce.Api.dll`/
   `Verce.Marketplaces.E2EHost.dll`/`vite preview` process). No broad `taskkill /IM` was ever used;
   the one manual cleanup this session needed (stopping the two runs that surfaced the bugs above)
   used `taskkill /PID <exact pid> /T /F` against a PID this session's own process tree inspection
   had just identified as its own.

Full regression after all three fixes: Release build 0/0; full non-integration backend suite (543
tests) green; full `Verce.IntegrationTests` **464/464** (462 + the two new migration tests); RT-01/
02/03 rerun **24/24**; Commerce + S8C1 focused suite **86/86**; frontend Vitest **129/129**,
typecheck/lint/build clean; OpenAPI contract check **OK — generated contracts are up to date**;
dedicated S8C.1 Playwright **4/4**; full main Playwright **41/41 twice**, both with confirmed real
exit codes.
