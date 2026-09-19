# ROADMAP — Verce 3D | Laboratório de Custos

The macro sequence S0–S17 is **frozen by the orchestrator**. This document adds technical
detail, dependencies and exit criteria per sprint. It does not reorder anything.

**Review of the frozen order:** the sequence is architecturally sound. Two observations, raised
as information rather than as requests to reorder:

1. **S4 (Cost Engine) precedes S5 (Product BOM).** Correct as ordered: building the engine
   against `CostInput` — a plain record with no persistence — forces the purity the design
   depends on. S4 must ship the engine and the Laboratory against ad-hoc components, and S5
   then supplies the engine with data resolved from a real BOM. If S4 were built after S5 it
   would almost certainly grow database access.
2. **S10 (Energy) after S6–S9 (Quote/Sales/Production).** Energy is part of the cost formula
   from S4 onward. The resolution is that S4 ships energy **as a value in `CostInput`** (a kWh
   number and a price per kWh, typed by the operator or estimated from machine power), while
   S10 ships tariff versioning, sessions and the smart-plug provider. Sprints before S10 must
   therefore not hard-code an energy price: they read the seeded default tariff. This is a
   sequencing constraint on S4, not a reordering request.

---

## S0 — Architecture *(this sprint)*

Deliver: `docs/` specification, ADRs, `CLAUDE.md`.
No code, no migrations, no commits.

**Exit:** an implementation agent can begin S1 without rediscovering the domain, the cost
model, quote versioning, the fee model, state machines, document architecture, security
boundaries or persistence strategy.

---

## S1 — Foundation

**Goal:** an empty but correct skeleton that runs.

### S1.0 — Repository baseline (the FIRST execution step)

Before any project, file or package is created:

1. **Verify** whether the workspace is already a Git repository (`git rev-parse --git-dir`).
   As of the end of S0 it is **not** — S0 produced documentation only, under no version control.
2. **Initialize** Git if absent.
3. **Create a `.gitignore`** appropriate to the stack **before** the first build ever runs:
   `bin/`, `obj/`, `node_modules/`, `dist/`, `.vs/`, `.idea/`, `*.user`,
   `appsettings.*.Local.json`, `.env`, `*.pfx`, `*.p12`, `secrets.json`,
   **`.dataprotection/`**, `TestResults/`, `playwright-report/`, `/artifacts/`.
   Getting this in first is what stops a key ring or a local secret from ever entering history.
4. **Establish the initial branch** (`main`).
5. **Do not commit.** No commit, push or tag until the orchestrator authorizes it at a gate —
   the standing rule in [CLAUDE.md §5](../CLAUDE.md) applies to S1 exactly as it did to S0.

Everything below happens inside that repository.

### S1.1 — Solution and platform

- Solution layout per [ARCHITECTURE §3](ARCHITECTURE.md#3-module-map): `Verce.Api`,
  `Verce.SharedKernel`, `Verce.Platform`, one project set per module (empty), test projects.
- `Verce.SharedKernel`: `Money`, `Percent`, `Grams`, `Kwh`, `Quantity`, `DurationSeconds`,
  `DateRange`, `Result<T>`, `IEvent`/`IDomainEvent`/`IIntegrationEvent`, `IClock`,
  `Rounding` helpers — **with full unit
  tests**, because everything downstream depends on their rounding behaviour.
- `Verce.Platform`:
  - `VerceDbContext`, schema conventions, UUID v7 generator;
  - **`UnitOfWork` implementing the wave algorithm** of
    [ADR-0012 §2](architecture/ADR-0012-domain-events-and-outbox.md#3-the-unit-of-work-algorithm)
    — multiple `SaveChanges` waves, one transaction, `MAX_EVENT_WAVES = 8`;
  - **`AggregateVersionInterceptor`** advancing the root `version` **once per Unit of Work** on
    any child mutation, with `Added` roots fixed at `1` for the whole creating UoW
    ([ADR-0011 §2](architecture/ADR-0011-identifiers-and-concurrency.md));
  - audit interceptor with `AmbientOperationContext` (`correlation_id`, `request_id`, actor,
    `wave_index`, `source`);
  - **full outbox**: table + attempt history, eligibility-checked claim with
    `FOR UPDATE SKIP LOCKED`, **fencing token**, lease + reclaim sweep, retry ladder with a
    single terminal-attempt rule, `FAILED` with `ACTIVE`/`DISMISSED` disposition, manual requeue,
    retention job, eligibility-based stall detection and the Owner-only diagnostics endpoint;
  - Quartz host, Serilog with redaction, `IDocumentStorage` (local filesystem);
  - **Data Protection**: `PersistKeysToDbContext` → `platform.data_protection_keys`,
    `ProtectKeysWithCertificate(current)` + `UnprotectKeysWithAnyCertificate(ring)` from host
    file mounts, **fail-closed startup validation** in Production (the process exits; it does
    **not** start and report unready), file-system dev fallback
    ([OPERATIONS §3](OPERATIONS.md#3-data-protection-key-ring)).
- ASP.NET Core Identity, cookie auth, roles `Owner`/`Operator`/`Viewer`, permission constants,
  login/logout/me endpoints, antiforgery, security headers, rate limiting.
- **First-Owner bootstrap**: `bootstrap-owner` and `recover-owner` CLI commands with the
  **two-operand secret challenge** (expected from a mounted file, candidate from a silent
  prompt), advisory-lock serialization, `platform.account_setup_token`, `setup_status`, the
  `/setup-account` endpoint, API-surface last-Owner protection and the local break-glass
  exception ([ADR-0009 §6–§10](architecture/ADR-0009-authentication-strategy.md)).
  **No seeded user.**
- Frontend skeleton: Vite + React + TS, routing, layout shell, login page, API client with
  generated types, theme token infrastructure (one theme), pt-BR formatting utilities.
- `docker-compose.yml` (PostgreSQL), health endpoints, CI running build + tests.
- `Verce.Architecture.Tests`: dependency direction, no cross-module navigations, no `float` in
  domain, no `DateTime.Now`, no secret-named properties on response contracts, **PK category
  convention**, **no synchronous handler owning a DbContext/scope/transaction/HttpClient**,
  **no `ORDER BY id`**.

### S1 acceptance test contracts (mandatory)

Frozen by the gate corrections. **S1 is not done until every contract below passes.** Each is
stated as Given / When / Then so the name alone is never the specification. Renumbered and
expanded by re-gate corrections 002 — these are contracts, not a stable catalogue.

#### A. Domain events, buffer and acknowledgement — [ADR-0012 Part I](architecture/ADR-0012-domain-events-and-outbox.md)

| # | Contract | Given / When / Then |
|---|---|---|
| A-1 | `EventSingleDispatchPerWave` | **G** an aggregate raises `E1`; its handler mutates state but raises nothing. **W** the command runs. **T** `E1` is dispatched **exactly once**; the aggregate's buffer is empty at commit. |
| A-2 | `EventCreatedDuringHandlerGoesNextWave` | **G** `E1`'s handler raises `E2`. **W** the command runs. **T** wave 1 dispatches only `E1`, wave 2 only `E2`, each exactly once; no duplicates in either wave. |
| A-3 | `HandlerFailureRollsBackAcknowledgement` | **G** `E1`'s handler throws. **W** the command runs. **T** the transaction rolls back; no aggregate, audit or outbox row persists; a retry begins a **fresh** business transaction with new `EventId`s. |
| A-4 | `PendingWriteWithoutEventStillSaved` | **G** a handler mutates an aggregate but raises no event. **W** the command runs. **T** that mutation is persisted — the loop does not exit while writes are pending. |
| A-5 | `EventCausationChainPreserved` | **G** `E1 → E2 → E3` across three waves. **W** the command commits. **T** all three share one `correlation_id`; `E2.CausationId = E1.EventId`, `E3.CausationId = E2.EventId`; `E1.CausationId` is null. |
| A-6 | `SaveHappensBeforeDispatch` | **G** the `QuoteApproved` handler reads the revision through the ambient context. **W** it is dispatched. **T** it observes `status = APPROVED` already applied within the transaction. |
| A-7 | `WaveLimitExceededRollsBack` | **G** two handlers raising each other's events. **W** the command runs. **T** `DomainEventWaveLimitExceeded` at wave 9; full rollback; the causation chain appears in the log. |
| A-8 | `IntegrationEventsNeverDispatchedInProcess` | **G** a command raises an `IIntegrationEvent`. **W** it commits. **T** no in-process handler ran; exactly one `outbox_message` row exists. |
| A-9 | `IdempotentApprovalNeverAborts25P02` | **G** two concurrent approvals of one revision. **W** both run. **T** exactly one `ProductionOrder`; neither transaction enters PostgreSQL state `25P02`. |
| A-10 | `OutboxRowInRolledBackTransactionNeverDispatched` | **G** a command writes an outbox row then fails. **W** rollback. **T** the dispatcher never sees the message. |

#### B. Aggregate versioning — [ADR-0011 §2](architecture/ADR-0011-identifiers-and-concurrency.md)

| # | Contract | Given / When / Then |
|---|---|---|
| B-1 | `AggregateInitialVersionIsOne` | **G** a new aggregate root. **W** inserted. **T** `version = 1` — never 0. |
| B-2 | `AddedAggregateWithChildrenStartsAtOne` | **G** a root inserted with three children in one transaction. **W** commit. **T** `version = 1`; children produce **no** extra bump; no concurrency predicate is applied on insert. |
| B-2a | `AddedAggregateRemainsVersionOneAcrossWaves` | **G** a new root + child created in wave 1. **W** a synchronous handler modifies **that same root** in wave 2 of the same UoW. **T** committed `version = 1` — the root was registered as handled at creation, so no later wave may bump it. Deriving "was it Added?" from `EntityState` after the first save is the bug this guards. |
| B-2b | `AddedThenConcurrencyAfterCommit` | **G** a root created and committed at `version = 1`. **W** a **new independent request** loads and modifies it. **T** committed `version = 2`, and a stale writer at `1` conflicts. |
| B-3 | `MultipleChildrenSingleVersionBump` | **G** a root at `version = 4`. **W** one command modifies three different children. **T** committed `version = 5` — not 7, not 8. |
| B-4 | `ExistingAggregateBumpsOnceAcrossWaves` | **G** an existing root at `version = 7`. **W** wave 1 modifies a child and wave 2 modifies the same root again. **T** committed `version = 8` — the token is the aggregate's committed revision, not a wave counter. |
| B-5 | `ConcurrentDifferentChildrenConflict` | **G** contexts A and B both load the aggregate at `version = 4`. **W** A modifies child 1 and commits; B modifies **a different child** and commits. **T** **B receives `DbUpdateConcurrencyException` → 409.** Two edits to the *root row* is not a sufficient test. |
| B-6 | `NestedChildResolvesCorrectRoot` | **G** a grandchild (`ProductFilamentComponent` → `ProductRecipe` → `Product`). **W** only the grandchild changes. **T** `Product.version` bumps; no other aggregate is touched. |
| B-7 | `OwnershipRegistryRejectsBrokenChain` | **G** an `IOwnedBy<>` chain that does not terminate at an `IAggregateRoot`, or is cyclic. **W** the app starts. **T** **startup fails**, naming the offending type. |
| B-8 | `NoRepositoryOverNonRootEntity` | **G** the assembly graph. **W** the architecture test runs. **T** no repository or facade type is generic over a non-root entity; reads may still project children freely. |

#### C. Outbox lease, failure and retention — [ADR-0012 Part II](architecture/ADR-0012-domain-events-and-outbox.md)

| # | Contract | Given / When / Then |
|---|---|---|
| C-1 | `OutboxClaimSkipsLocked` | **G** two dispatcher workers and one batch. **W** both claim concurrently. **T** no message is claimed twice; neither worker blocks. |
| C-2 | `StaleLeaseOwnerCannotComplete` | **G** worker A claims (token `TA`) then freezes; the lease expires; the sweep requeues; worker B claims (token `TB`). **W** A attempts completion with `TA`. **T** **0 rows affected**; A logs and abandons; B's state is untouched. |
| C-3 | `StaleLeaseOwnerCannotHeartbeat` | **G** the same setup. **W** A calls `ExtendLease` with `TA`. **T** 0 rows affected; B's `lease_until` is unchanged. |
| C-4 | `AttemptCountIncrementsAtClaim` | **G** a `PENDING` message at `attempt_count = 2`. **W** claimed. **T** `attempt_count = 3` immediately, before the consumer runs. |
| C-5 | `LastAttemptCrashBecomesFailed` | **G** a message claimed on its **final** permitted attempt. **W** the worker crashes and the lease expires. **T** the sweep sets `FAILED` with `failure_reason = LEASE_EXPIRED_ON_FINAL_ATTEMPT` — **not** a further attempt. |
| C-6 | `RetryLadderRespected` | **G** retryable failures with budget remaining. **W** each occurs. **T** `available_at` follows 1 m / 5 m / 30 m / 2 h. |
| C-6a | `RetryableFailureOnFinalAttemptBecomesFailed` | **G** `max_attempts = 5`; the message is claimed for attempt 5 (`attempt_count = 5`). **W** the consumer returns a **retryable** failure. **T** `FAILED` with `failure_reason = RETRY_BUDGET_EXHAUSTED` and `available_at = NULL` — **not** `PENDING`, and **never** an attempt 6. Identical outcome to a crash on that attempt (C-5). |
| C-6b | `ExhaustedMessageCannotBeClaimed` | **G** a row somehow at `attempt_count >= max_attempts` while `PENDING`. **W** the dispatcher runs its claim query. **T** it is **not** claimed — the claim predicate includes `attempt_count < max_attempts`. |
| C-7 | `NonRetryableSkipsLadder` | **G** a consumer throwing `NonRetryableOutboxException`. **W** dispatched. **T** immediate `FAILED`, regardless of remaining attempts. |
| C-8 | `FailureDismissalPreservesHistory` | **G** a `FAILED` message with attempt history. **W** an `Owner` dismisses it with a reason. **T** `failure_disposition = DISMISSED` with actor, timestamp and reason; `last_error` and every `outbox_message_attempt` row **remain**; it stops counting toward `degraded`. |
| C-9 | `ManualRequeuePreservesIdempotency` | **G** a `FAILED` message with attempt history. **W** requeued. **T** same `id` and `idempotency_key`; `execution_generation` incremented; `attempt_count` **reset to 0**; `failure_disposition` cleared; `last_error` and every `outbox_message_attempt` row **preserved**; an audit row written. |
| C-9a | `RequeuedSuccessfulMessageEndsProcessedWithoutDisposition` | **G** a `FAILED`/`ACTIVE` message. **W** it is requeued and the consumer then succeeds. **T** final row is `status = PROCESSED`, `failure_disposition = NULL`, `processed_at` set. There is no `RESOLVED` disposition; success **is** resolution, and history remains in the attempt table. |
| C-9b | `DismissedFailureIsNotClaimable` | **G** a `FAILED` message with `disposition = DISMISSED`. **W** the dispatcher runs. **T** it is never claimed (it is not `PENDING`) and it does not count toward `degraded`. |
| C-9c | `RequeueStartsNewExecutionGeneration` | **G** a message whose generation 1 is exhausted and `FAILED`. **W** an `Owner` requeues it. **T** `execution_generation` becomes **2**, `attempt_count` becomes **0**, status `PENDING`; **every generation-1 attempt row is unchanged**. |
| C-9d | `ClaimAfterRequeueDoesNotCollideWithHistory` | **G** history already contains `(M, generation 1, attempt 1)`, and M has been requeued into generation 2. **W** the first claim of the new round occurs. **T** a row `(M, generation 2, attempt 1)` is inserted **without violating** `UNIQUE (outbox_message_id, execution_generation, attempt_number)`; both rows coexist. *(This is the exact insert that failed before the generation column existed.)* |
| C-9e | `RequeuePreservesBusinessIdempotencyKey` | **G** a `FAILED` message with `idempotency_key = K`. **W** requeued. **T** `execution_generation` changed but `id` and `idempotency_key` are **still K** — a requeue retries the *same* effect, so consumer deduplication must still recognize it. |
| C-9f | `RequeueThenSuccessHistoryIsComplete` | **G** generation 1 failed across 5 attempts. **W** requeue opens generation 2 and its attempt 1 succeeds. **T** the message is `PROCESSED` with `failure_disposition = NULL`; history holds **G1/A1…G1/A5 and G2/A1**, all retained. |
| C-9g | `RequeueFromDismissedAlsoOpensNewGeneration` | **G** a `FAILED` message with `disposition = DISMISSED`. **W** an `Owner` explicitly revives it. **T** same generation rule applies (`+1`, budget reset), disposition cleared, and the revival is audited. |
| C-10 | `UnresolvedFailedNotPurged` | **G** a `FAILED` message with `disposition = ACTIVE`, older than every retention window. **W** the retention job runs. **T** it is **not** deleted. Processed messages older than 90 days **are**. |
| C-11 | `DuplicateDeliveryProducesOneDocument` | **G** the same render message delivered twice. **W** both are processed. **T** exactly one `generated_document` (`render_request_id` conflict); the second reports success without re-rendering. |
| C-12 | `ConsumerTimeoutBelowLease` | **G** the registered consumers. **W** the app starts. **T** every consumer's timeout is strictly less than the lease duration, or startup fails. |

#### D. Owner bootstrap and account concurrency — [ADR-0009](architecture/ADR-0009-authentication-strategy.md)

| # | Contract | Given / When / Then |
|---|---|---|
| D-1 | `ConcurrentBootstrapSingleWinner` | **G** an empty database. **W** two `bootstrap-owner` processes run simultaneously. **T** exactly one Owner exists; the loser blocks on advisory lock `8401001`, then exits non-zero. |
| D-2 | `BootstrapRefusedOnceOwnerExists` | **G** an Owner exists. **W** `bootstrap-owner` runs. **T** it refuses, exits non-zero, and changes nothing. |
| D-3 | `BootstrapSecretRejectedWhenWrong` | **G** Production. **W** the secret is wrong or missing. **T** exit code 2, one generic message that does not distinguish the two, and an audited rejection. |
| D-4 | `BootstrapSecretNeverLogged` | **G** a configured secret. **W** bootstrap both succeeds and fails. **T** the value appears in **no** log sink, exception message or console echo. |
| D-5 | `SetupTokenStoredOnlyAsHash` | **G** an issued token. **W** the database is inspected. **T** only the SHA-256 hash exists; the raw token is in no table and no log. |
| D-6 | `ConcurrentSetupTokenSingleConsumer` | **G** one valid token. **W** two requests submit it simultaneously. **T** exactly one succeeds; the other is rejected with the same generic message as an expired token. |
| D-7 | `SetupTokenSingleUseAndExpiry` | **G** consumed, expired and invalidated tokens. **W** each is presented. **T** all are rejected identically, revealing nothing. |
| D-8 | `PendingSetupCannotLogin` | **G** a freshly bootstrapped Owner with `setup_status = PENDING_SETUP`. **W** login is attempted with any credential. **T** rejected before password verification, indistinguishably from a wrong password. |
| D-9 | `ConcurrentLastOwnerRemovalNeverZero` | **G** exactly two active Owners, A and B. **W** two requests concurrently remove A and B. **T** one succeeds, one fails `LAST_OWNER_PROTECTED`; **at least one active Owner always remains.** |
| D-10 | `RecoveryInvalidatesPreviousRecoveryToken` | **G** an outstanding recovery token. **W** `recover-owner` runs again. **T** the prior token has `invalidated_at` set and no longer works; exactly one valid token remains — the newest. |
| D-11 | `RecoveryInvalidatesSessions` | **G** an active session. **W** recovery or reset completes. **T** `SecurityStamp` is regenerated and the prior cookie stops working. |
| D-12 | `NoSeededUserInAnyMigration` | **G** the migration and seed paths. **W** applied to an empty database. **T** `platform.user` is empty. |
| D-13 | `BootstrapExpectedAndCandidateSecretsAreDistinctSources` | **G** the expected secret is the file at `VERCE_BOOTSTRAP_SECRET_FILE`. **W** `bootstrap-owner` runs. **T** the **candidate** is read from a **no-echo interactive prompt** and is **never** auto-loaded from the expected file, an environment variable or a CLI argument. A build in which both operands resolve to the same source **fails the test** — comparing a value with itself authenticates nobody. |
| D-14 | `RecoverOnlyOwner` | **G** exactly one Owner exists and cannot authenticate. **W** local `recover-owner` runs with a valid recovery secret. **T** it **succeeds**: the `Owner` role is **preserved**, `setup_status` becomes `PENDING_SETUP`, `recovery_started_at` is set, all sessions and prior tokens are invalidated, and exactly one new recovery token exists. **Then** on consuming that token, `setup_status` returns to `ACTIVE` and the system is back to one active Owner. |
| D-15 | `ApiCannotDeactivateOnlyOwner` | **G** one active Owner. **W** the **HTTP API** attempts to deactivate, delete or remove the `Owner` role from that account. **T** rejected with `LAST_OWNER_PROTECTED`. The break-glass CLI (D-14) is the only exception, and this test must not be weakened to accommodate it. |

#### E. Data Protection — [ADR-0008 §2](architecture/ADR-0008-ai-integration-and-secret-handling.md)

| # | Contract | Given / When / Then |
|---|---|---|
| E-1 | `RestoreWithCertificateDecryptsExistingSecret` | **G** a database restored with the same certificate ring. **W** the app starts. **T** the stored OpenAI key decrypts; existing sessions remain valid. |
| E-2 | `ProductionMissingCertificatePreventsHostStartup` | **G** a restored database and **no** usable wrapping certificate. **W** the app starts in Production. **T** **startup validation fails and the process exits non-zero**, naming the recovery command; **no** new key ring is silently generated. |
| E-2a | `ReadinessIsNotExpectedWhenStartupValidationFails` | **G** the same scenario. **W** a probe calls `/health/ready`. **T** it gets a **connection failure, not a 503** — the host never bound HTTP. No requirement anywhere may expect a 503 here. |
| E-2b | `RestoreWithoutCertificateRequiresOfflineRecovery` | **G** the host refusing to start. **W** the operator runs the **offline** `recover-data-protection`. **T** it completes without the web host running, and the host starts normally afterwards. |
| E-3 | `RotationKeepsOldPayloadReadableWithCertificateRing` | **G** a payload protected while certificate 1 was current. **W** certificate 2 becomes current and the unprotect **ring** is `[2, 1]`. **T** the payload still decrypts. |
| E-4 | `RotationCreatesNewKeyWithoutClaimingRewrap` | **G** the ring `[2, 1]` with 2 current. **W** a new DP key is created via `IKeyManager.CreateNewKey(...)`. **T** the **new** key is wrapped with certificate 2, and **every pre-existing key remains wrapped with certificate 1** — nothing re-wraps them, and no assertion claims otherwise. |
| E-4a | `OldCertificateNotAutomaticallyRemoved` | **G** a completed rotation. **W** configuration is inspected. **T** every predecessor certificate is still in the unprotect ring; v1 retires none. |
| E-5 | `ExplicitCryptoRecoveryRestoresOperability` | **G** an unreadable key ring. **W** `recover-data-protection --confirm-destroy-secrets` runs with the recovery secret. **T** the app starts normally afterwards. |
| E-6 | `CryptoRecoveryClearsUnreadableExternalSecrets` | **G** the same. **W** recovery completes. **T** `api_key_encrypted` and `api_key_last_four` are cleared, `is_enabled = false`, `api_key_recovery_required = true`; all sessions invalidated; **passwords still work**. |
| E-7 | `CryptoRecoveryArchivesRatherThanDeletes` | **G** unreadable key rows. **W** recovery runs. **T** every row is present in `data_protection_key_archive`; none was destroyed. |
| E-8 | `CryptoRecoveryRefusesWhenRingCanStillUnwrap` | **G** a ring that *can* open the keys. **W** recovery is attempted. **T** it refuses — the command cannot be used casually. |
| E-9 | `DevProfileNeedsNoCertificate` | **G** the Development profile. **W** the app starts with no certificate. **T** it starts with a file-system ring; the Production path cannot select that provider. |

#### F. Primary keys and classification — [ADR-0011 §1](architecture/ADR-0011-identifiers-and-concurrency.md)

| # | Contract | Given / When / Then |
|---|---|---|
| F-1 | `EveryApplicationTableHasExactlyOneCategory` | **G** the EF model. **W** the architecture test runs. **T** every entity type is classified by exactly one marker **or** one technical-registry entry; zero or two categories fails. |
| F-2 | `DomainAndMasterPKGuid` | **G** `IDomainEntity` and `IMasterData` types. **T** the PK property type is `Guid`. |
| F-3 | `ReferencePKCode` | **G** `IReferenceData` types. **T** the PK is an immutable `string Code`. |
| F-4 | `TechnicalTableClassification` | **G** `ITechnicalTable` types. **T** exempt from PK shape but present in the registry. |
| F-5 | `FrameworkTechnicalEntityAllowedWithoutMarker` | **G** `DataProtectionKey`, Identity and Quartz types, which cannot implement our interfaces. **T** they pass **via registry entries**, not markers, and not via a per-table exception list. |
| F-6 | `JoinCompositePK` | **G** any `IJoinTable`. **T** a composite foreign-key primary key. |
| F-7 | `DataModelFiveCategoriesMatchAdr` | **G** the category table in DATA-MODEL and the categories in ADR-0011 §1. **W** compared. **T** both list the **same five** categories with the same PK shapes; Domain and Master are not collapsed. |
| F-8 | `FrameworkTablesDoNotRequireApplicationAuditColumns` | **G** Identity, Quartz and `data_protection_keys`. **W** the schema is inspected. **T** they retain their framework schema — no `created_by`/`updated_by` were added — and no architecture rule demands them. |
| F-9 | `NoOrderByIdAnywhere` | **G** all queries. **T** none sorts or paginates by `Id`; ordering uses `created_at`, a business date, a sequence or `sort_order`. |

#### G. Audit across waves — [ADR-0010](architecture/ADR-0010-audit-strategy.md)

| # | Contract | Given / When / Then |
|---|---|---|
| G-1 | `MultiWaveAuditSameCorrelation` | **G** a command spanning three waves. **W** it commits. **T** every audit row shares one `correlation_id`, `request_id` and actor. |
| G-2 | `WaveIndexPreserved` | **G** an entity genuinely modified in waves 1 and 2. **W** commit. **T** two rows with `wave_index` 1 and 2, each with its own `occurred_at`, sharing `operation_started_at`. |
| G-3 | `SingleWaveEntityProducesOneRow` | **G** an entity written in wave 1 and untouched afterwards. **W** commit. **T** exactly one audit row — no artificial duplication. |
| G-4 | `AuditFailureRollsBackTransaction` | **G** audit persistence fails. **W** the command runs. **T** the whole business transaction rolls back; there is no best-effort audit path. |
| G-5 | `SystemJobActorRecorded` | **G** the quote-expiration job. **W** it runs. **T** rows carry `source = JOB` and `user_id IS NULL`; **no** row anywhere carries `source = MIGRATION`. |

#### H. Health semantics

| # | Contract | Given / When / Then |
|---|---|---|
| H-1 | `FailedOutboxMessageDoesNotMakeUnready` | **G** an unresolved `FAILED` message. **W** `/health/ready` is probed. **T** **HTTP 200** with status `degraded` — an orchestrator must not replace the container. |
| H-2 | `EligibleOldMessageCanCauseStall` | **G** a `PENDING` message with `available_at = now - 31 min` and budget remaining, threshold 30 min. **W** probed. **T** **HTTP 503**, `unhealthy` — nothing is draining the queue. |
| H-2a | `FutureAvailableMessageDoesNotCauseStall` | **G** a `PENDING` message with `available_at = now + 1 hour` (backoff or future schedule) and nothing else waiting. **W** probed. **T** **not** stalled — `healthy`, HTTP 200. A message serving its backoff is the system working as designed. |
| H-3 | `ReadyExposesNoDetailAnonymously` | **G** an anonymous probe. **T** the body is a status word only — no component names, versions, counts or error text. |
| H-4 | `HealthDetailRequiresOwner` | **G** `/api/platform/health`. **T** 403 for `Operator` and `Viewer`; 200 with the breakdown for `Owner`. |

**Total: 87 acceptance contracts** across eight areas: A events 10 - B versioning 10 -
C outbox 21 - D auth 15 - E data protection 12 - F keys 9 - G audit 5 - H health 5, plus the
module-boundary architecture tests listed above. The count is **derived from the table rows**
and re-derived whenever contracts change; it is never carried forward from a previous report.

**Exit:** login works, an authenticated empty dashboard renders, migrations apply from scratch,
architecture tests pass, `SharedKernel` is fully tested, and **every contract in the A–H
catalogue above passes**.

*(The earlier `T-01`…`T-28` identifiers are obsolete and are no longer referenced anywhere;
they were superseded by the A–H catalogue.)*

---

## S2 — Master Data

**Goal:** the entities that everything else references.

- Customers + addresses (CRUD, search, soft delete, CPF/CNPJ validation).
- Product categories, supply categories, filament materials and brands (lookups).
- Sales channels (`Venda Direta` seeded with a 0% fee rule version — mandatory, see
  [DATA-MODEL §17](DATA-MODEL.md#17-seed-data-s1s2)).
- Machines (Energy module, master data only: power, acquisition, lifetime, maintenance).
- **CompanyProfile** (legal/trade name, document, address, phone, e-mail, website, Instagram,
  WhatsApp, timezone, currency) and the `app_setting` screen.
- **Brand asset library**: `BrandAsset` + immutable `BrandAssetVersion`, the seven asset types,
  and the full upload pipeline of [SECURITY §8.1](SECURITY.md#81-file-uploads-brand-assets-expense-attachments)
  — PNG/JPEG/WebP, magic-byte sniffing, real decode, re-encode, content-addressed storage.
  **SVG is rejected in v1.**
- **BrandingAssignment** for the four roles, seeded with the VERCE assets; the
  `/api/settings/branding` endpoint; the app shell rendering logo, favicon, product name
  `VERCE 3D` and subtitle `Laboratório de Custos` **from settings, never from an import**.
- Audit interceptor proven end to end on one entity.

**Exit:** a customer can be created, edited, deactivated and found by name; every seeded lookup
exists; audit rows appear for a cost-bearing change; **replacing a logo creates a new version
and leaves the previous file untouched**; a `.exe` renamed to `.png` is rejected.

---

## S3 — Supplies & Inventory

> **Delivered scope differs from the plan originally drafted here.** The mission actually
> executed for S3 generalized quantities/units across every supply (not filament grams only) and
> made non-negative stock a hard invariant instead of history-tracked cost — see
> [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) for the full
> reasoning and [DOMAIN-MODEL §3](DOMAIN-MODEL.md#3-inventory-module) for the model as built. What
> follows reflects the actual delivery, not the original plan (which modeled Filament as its own
> aggregate with lot-based cost history).

- `Supply` (Category 1 aggregate root): code, name, category, base unit, minimum stock, active
  flag, optional `FilamentDetails`, cached current stock, cached latest purchase unit cost.
- `SupplyCategory` (Category 3 reference data, seeded): `FILAMENT`, `RESIN`, `PACKAGING`,
  `HARDWARE`, `ELECTRONICS`, `FINISHING`, `CONSUMABLE`, `OTHER`.
- `InventoryMovement` (append-only ledger, child of `Supply`): initial balance (once), purchase
  receipt (with unit conversion and a cost snapshot — informational only, not a costing policy),
  manual increase/decrease, and count-based correction. Non-negative stock enforced via the
  aggregate's own optimistic-concurrency `Version` — no new locking primitive.
- Unit normalization: a closed, explicit conversion table (kg↔g, L↔mL, m↔cm), not a general
  unit-of-measure framework.
- UI: supply list with search/category/active/low-stock filters and pagination; create/edit form
  with conditional filament fields; inventory detail with stock, low-stock state, latest cost and
  movement history; initial-balance, purchase-receipt and manual-adjustment actions.

**What S3 explicitly did not build** (deferred, per the mission's own scope boundary): per-lot
stock tracking, a filament/supply cost-history table, an actual costing policy
(FIFO/LIFO/weighted-average — S4), recipes/consumption (S5+), and quote/production stock blocking
(unchanged from v1's warn-only behaviour).

**Exit (critical test):** a decrease larger than current stock is rejected and stock never goes
negative, including under two genuinely concurrent requests against the same supply (proven
against a real PostgreSQL host, not mocked).

---

## S4 — Cost Engine & Laboratory

**The keystone sprint.** Everything commercial depends on this being right.

- `CostEngine` in `Costing.Domain`: pure, no I/O, implementing
  S4 Cost Laboratory profile in [CALCULATION-RULES](CALCULATION-RULES.md), returning an
  explainable breakdown.
- Generic Supply lines with S3 unit normalization, weighted-average acquisition basis, optional
  manual simulation override, line/scenario/setting wastage precedence and non-blocking stock
  warnings.
- Labor, manually rated machine time, additional direct costs, batch output and estimated unit
  cost. Energy, Printer, Product, Recipe, Production and sale-price concepts remain future scope.
- Stateless Laboratory UI with add/remove material and direct-cost lines; no IDs, persistence,
  save/clone/history or conversion (no Costing schema; ADR-0018's targeted Settings data migration is separate).
- Engine version `1.0.0` returned with each result; generated OpenAPI DTOs are the frontend source.

**Exit:** required arithmetic examples, real PostgreSQL acquisition basis, authorization,
side-effect absence, API/UI workflows and full regression gates are green.

---

## S5 — Product BOM & Pricing

- `Product` + versioned `ProductRecipe` with filament/supply/manual components and process
  parameters; recipe immutability once used; "edit creates revision N+1".
- `CostInputBuilder`: resolves a recipe into a `CostInput` at an instant (this is where
  snapshot values come from).
- `PricingEngine` in `Pricing.Domain`: the single formula, denominator guard, rounding policy,
  fee clamps, bracket resolution algorithm (CR-07.5) — golden tests G4–G9.
- `FeeRule` / `FeeRuleVersion` / `PriceBracket` with the temporal exclusion constraints;
  `FeeRuleResolver`.
- `CostExperiment.ConvertToProduct`.
- UI: product form with BOM editor; per-channel suggested price panel showing cost, fee,
  margin, price and profit side by side.

**Exit:** `PRICING_INVALID_DENOMINATOR` is impossible to bypass; effective margin equals
desired margin under CR-08.5 conditions.

---

## S6 — Quote Engine

- `Quote` / `QuoteRevision` / `QuoteItem` / `QuoteItemCostSnapshot` / `QuoteStatusHistory`.
- Numbering: `quote_number_counter`, the atomic upsert, `YYMMDD-N`, with a **concurrency
  integration test** using parallel writers (ADR-0004).
- Revision suffix algorithm with the boundary tests (1, 2, 26, 27, 28, 53).
- Edit = clone + change + new revision; supersession pointer semantics; per-item re-pricing
  policy (only changed items).
- Full state machine with guards and history; `ExpireQuotesJob`.
- Snapshot writing: typed columns + `breakdown` JSONB.

**Exit (critical test):** create a quote, change every underlying price, reopen the quote —
every number is unchanged and the breakdown still explains it.

---

## S7 — Quote UX & PDF v1

- Quote screens: builder, item editor with per-item discount and channel, revision timeline,
  status actions, "explain this price" panel rendering the stored breakdown.
- Proposal content fields on the revision (title, scope, technical highlights, technical notes,
  out of scope, payment/delivery terms, warranty) with defaults **copied** from settings at issue.
- Document engine v1: `DocumentType`, **binding catalogues**, `DocumentTemplate`,
  `DocumentTemplateVersion`, block renderers, declarative `visibleWhen`, `ItemsTable` repeater,
  pagination (repeating header/footer, repeating table header, page numbers, break-avoidance),
  HTML/CSS, Playwright to PDF.
- **`VERCE | Proposta Comercial Padrão` implemented as the default template** — seeded template
  data, `is_default` for `QUOTE`, PNG logo via `INHERIT_DEFAULT`, quote/customer/company
  bindings. Block tree in [DEFAULT-PROPOSAL-TEMPLATE](DEFAULT-PROPOSAL-TEMPLATE.md).
  **Begin with the §8 visual-extraction task against the reference PDF.**
- `GeneratedDocument` with `purpose`, render snapshot, rendered HTML, resolved brand asset
  versions, content-addressed storage, and issued-document immutability
  ([ADR-0016](architecture/ADR-0016-document-render-snapshots.md)).
- Rendering driven by the outbox, not inline in the approval transaction.
- E2E: create → revise → send → approve → download PDF.

**Exit:** the PDF total equals the screen total equals the database total, byte-for-byte in
value terms; re-rendering an old revision reproduces the same figures; the template renders
correctly at **1, 5 and 25 items** with a repeated table header and `x / y` page numbers;
changing the company phone and replacing the logo leaves an already-issued proposal identical.

---

## S8 — Sales & Expenses

- `Sale` / `SaleItem`, creation from an approved revision (copying frozen lines) and
  standalone; cancellation; `cost_basis = ESTIMATED` initially.
- `Expense` / `ExpenseCategory` with `AccountingTreatment` and the double-counting constraints
  ([ADR-0013](architecture/ADR-0013-expense-inventory-double-counting.md)).
- Lot purchase → optional linked expense (`INVENTORY_PURCHASE`), enforced one-to-one.
- Price brackets and `MinimumFee`/`MaximumFee` activated in the resolver.
- UI: sales list, expense list with treatment badges and an explicit warning when an operator
  categorizes a material purchase as an operating expense.

**Exit:** registering a filament purchase twice (through the lot screen and the expense screen)
is impossible; the monthly cost figure excludes inventory purchases.

---

## S9 — Production & Labels

- `ProductionOrder` created idempotently from `QuoteApproved`; unique on `quote_revision_id`;
  full state machine; planned material explosion snapshot.
- The §3 rules of [STATE-MACHINES](STATE-MACHINES.md#3-altering-an-approved-quote):
  automatic supersession when `QUEUED`, blocking with `PRODUCTION_ORDER_IN_PROGRESS` otherwise.
- Production queue UI (kanban or list), order detail, shop-floor document
  (`PRODUCTION_ORDER` template: items, quantities, filaments, colors, supplies, notes, due date,
  separation list).
- `SHIPPING_LABEL` template with `SIMPLE` and `FULL` variants and configurable page size
  (`page_setup` in millimetres — the same field an A4 proposal uses).
- **Reuse the S7 engine unchanged**: new binding catalogues and resolvers for
  `PRODUCTION_ORDER` and `SHIPPING_LABEL`, new seeded templates, no pipeline change. The label
  demonstrates per-template logo selection — `SPECIFIC_ASSET` → `SYMBOL` — against the
  proposal's full signature and the app's horizontal logo.

**Exit:** approving twice produces exactly one order; the shop-floor document is correct after
the underlying recipe is edited.

---

## S10 — Energy

- `EnergyTariff` + versions with the temporal exclusion constraint; resolver by instant;
  migration of the S4 flat price into a seeded tariff version.
- `EnergyConsumptionSession`, `IEnergyProvider` with `Estimated` and `Manual` implementations.
- `SmartPlugEnergyProvider` **interface and registration only** — no vendor code until the
  device is chosen. `EnergyPollJob` scaffolded and disabled.
- Machine hourly rate derivation (CR-04.1) surfaced in the UI.

**Exit:** a quote created before S10 still resolves its frozen energy price; new quotes resolve
the tariff version valid at issue time.

---

## S11 — Actual Cost Reconciliation

- `ProductionOrderItemActualMaterial` recording, emitting stock `OUT` movements.
- Actual energy via sessions linked to production items.
- `Sale.cost_basis` transitions `ESTIMATED → MIXED → ACTUAL` as items reconcile.
- Variance computation (CR-09) as derived read models; per-item and per-period.
- UI: "informar consumo real" on a production item, variance badges.

**Exit:** the brief's case — estimated 90 g, actual 96 g — reports `+6 g` and `+6,67%`, and the
sale's realized margin updates without touching the quote snapshot.

---

## S12 — Dashboard & Reports

- Home tiles and lists per [DATA-DICTIONARY §4.5](DATA-DICTIONARY.md#45-home-dashboard),
  search by quote number and customer, sorting by date/value/status/customer.
- `reporting.v_*` views; Dapper introduced here if and only if an EF projection is measured
  slow.
- Reports: sales by month/product/channel, product profitability, conversion funnel (CR-12.1
  including the `null` case), material and energy consumption, estimated vs actual, margin
  variance.
- CSV export of each report (permission-gated, audited).

**Exit:** every metric in the data dictionary is derivable and matches a hand-computed control
case; conversion never reports open quotes as failures.

---

## S13 — AI Insights

- `AiSettings` with encrypted key, masked display, model selection, editable prompt.
- `AiDatasetBuilder` with the exclusion rules of [SECURITY §5.4](SECURITY.md#54-data-minimization-for-ai-payloads);
  payload persisted with fingerprint.
- `AiInsightRun` / `AiInsightResult` driven by the outbox; FACT/HYPOTHESIS/RECOMMENDATION
  classification; budget guard; feature disabled by default.
- UI: insights page with priority ordering and a "what was sent" disclosure.

**Exit:** the key never appears in a response or a log (asserted by test); disabling AI removes
every outbound call.

---

## S14 — Low-Code Document Studio

- Block editor producing `DocumentTemplateVersion.definition` JSON — the same format S7/S9
  already render, so **no migration of existing templates**.
- Block library: `Logo`, `Text`, `Header`, `DynamicField`, `RichText`, `TechnicalHighlight`,
  `ItemsTable`, `Totals`, `Terms`, `Notes`, `Image`, `QrCode`, `Separator`, `PageNumber`,
  `Spacer`, `ConditionalSection`.
- **Binding picker driven by the catalogue** — the operator chooses from registered paths and
  cannot type a free-form token; publish-time validation rejects unknown paths.
- `visibleWhen` builder using the closed operator set (no expression editor).
- Create / edit / **duplicate** / version templates; draft → publish → archive; set as default;
  live preview against sample data.
- Page setup editor (size, margins, orientation, header/footer `repeatOn`) including label sizes.
- Document theme token editor (`definition.theme`), separate from application themes.

**Exit:** a template can be edited, duplicated and published without a deploy; documents
generated from older versions still render identically; the default VERCE proposal is editable
through the Studio like any other template — proving it was never renderer code.

---

## S15 — Themes / UX / Accessibility

- Ten themes as design-token sets; `ThemeDefinition` registry; per-user preference.
- Contrast verified ≥ 4.5:1 in every theme, light and dark.
- Keyboard navigation, focus management, ARIA labeling, screen-reader pass on the main flows.
- Performance pass: list virtualization, query caching, bundle splitting.
- UX pass on the frequent paths: new quote, new customer, new experiment in one click.

**Exit:** a theme change alters no behaviour and no calculation; automated accessibility checks
pass on the main flows.

---

## S16 — Security / Audit / Backup

- Audit log UI with entity timeline and diff view; retention job.
- Backup job (`pg_dump` + document files), retention, **and an executed restore drill**.
- Export/import (CSV/JSON) for master data.
- Full security review against the [SECURITY](SECURITY.md) checklist; dependency scan;
  penetration-style review of auth, authorization and the document endpoint.

**Exit:** a restore into a scratch database is documented and reproducible.

---

## S17 — Release Hardening

- Load/soak on realistic volumes; index review against real query plans.
- Error handling and empty-state review across every screen.
- Documentation: operator manual (pt-BR), deployment runbook, incident checklist.
- Migration rehearsal from an empty database and from a seeded one.
- Final quality gate against [CLAUDE.md §7](../CLAUDE.md).

---

## Cross-sprint invariants

These must hold at the end of **every** sprint from S4 onward:

1. Changing any current price never changes a stored quote.
2. `commission + margin ≥ 1` cannot produce a number.
3. Screen, PDF, database and API report identical monetary values.
4. Every quote revision is preserved and reachable.
5. No secret appears in a response or a log.
6. Architecture tests pass.

And from S2 onward (branding addendum):

7. Replacing a brand asset never alters a document already issued.
8. No `generated_document` with `purpose = ISSUED` is ever updated or deleted.
9. No visual asset is imported by a React component; branding comes from settings.
10. No template can print `internal_notes`, a unit cost or a margin.

## Dependency graph

```
S1 ──► S2 ──► S3 ──► S4 ──► S5 ──► S6 ──► S7 ──► S8 ──► S9
                      │       │              │      │     │
                      └───────┴──────────────┴──────┴─────┴──► S10 ──► S11 ──► S12 ──► S13
                                                                                  │
                                                              S14 ◄───────────────┘
                                                              S15, S16, S17 (after S14)
```
