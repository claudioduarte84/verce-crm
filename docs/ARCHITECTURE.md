# ARCHITECTURE — Verce 3D | Laboratório de Custos

Companion documents: [DOMAIN-MODEL](DOMAIN-MODEL.md) · [DATA-MODEL](DATA-MODEL.md) ·
[CALCULATION-RULES](CALCULATION-RULES.md) · [STATE-MACHINES](STATE-MACHINES.md) ·
[SECURITY](SECURITY.md) · [ADRs](architecture/)

---

## 1. Assessment of the proposed baseline

The baseline given by the orchestrator (ASP.NET Core 10 + React/Vite + PostgreSQL + EF Core +
Quartz.NET + Playwright PDF + modular monolith) is **accepted without structural change**.
It fits the problem: a single-operator system with heavy relational, historical and
transactional needs, no throughput problem, and a strong requirement for deterministic
arithmetic and auditability.

Points reviewed explicitly:

| Baseline choice | Verdict | Note |
|---|---|---|
| Modular monolith | Keep | Deployment simplicity plus a single transactional boundary is exactly what quote/production/stock consistency needs. |
| Clean Architecture (pragmatic) | Keep, with limits | Four layers per module only where the module has real domain logic. Thin modules (Settings, Audit) stay two-layer. Ceremony is not architecture. |
| EF Core primary, Dapper later | Keep | EF Core owns writes forever. Dapper is authorized for reporting reads only, from S12 onward, behind a `Reporting` module boundary. |
| PostgreSQL | Keep | `numeric`, `jsonb`, `daterange` + `btree_gist` exclusion constraints and partial indexes are all used by this design. |
| Quartz.NET | Keep | Needed for quote expiration, outbox draining, AI runs. Persist the job store in PostgreSQL so schedules survive restarts. |
| Playwright/Chromium PDF | Keep | Only realistic way to get CSS-faithful, page-accurate PDFs from HTML. Runs in-process on the API host in v1. |
| No Redis/RabbitMQ/Kafka | Keep | See [ADR-0012](architecture/ADR-0012-domain-events-and-outbox.md): in-process events plus a database outbox cover every asynchronous need at this scale. |

**Deviations/additions introduced by this architecture** (each justified in an ADR):

1. A **transactional outbox** table for post-commit side effects (ADR-0012).
2. **UUID v7 identifiers** instead of bigserial (ADR-0011).
3. A **single shared `DbContext`** with schema-per-module instead of one context per module,
   with boundary enforcement moved to architecture tests (ADR-0001, §5 below).
4. A `SUPERSEDED` quote state, required to satisfy "only the current revision can be
   approved" while preserving every revision (ADR-0004, STATE-MACHINES).
5. **Versioned brand assets** with role-based assignment, so replacing a logo cannot rewrite an
   issued document (ADR-0015, added by the branding addendum).
6. **Render snapshots** on generated documents (resolved context + HTML + resolved asset
   versions), extending the snapshot principle from money to layout, branding and content
   (ADR-0016, added by the branding addendum).

---

## 2. System context

```
┌──────────────────────────────────────────────────────────────────┐
│  Browser (React + TypeScript + Vite)                             │
│  same origin, cookie session, no tokens in JS-accessible storage  │
└───────────────┬──────────────────────────────────────────────────┘
                │ HTTPS  /api/*
┌───────────────▼──────────────────────────────────────────────────┐
│  Verce.Api  (ASP.NET Core 10)                                    │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ Modules (in-process)                                       │  │
│  │ Customers · Catalog · Inventory · Costing · Pricing        │  │
│  │ Quoting · Sales · Production · Energy · Documents          │  │
│  │ Finance · Reporting · AI · Settings                        │  │
│  └────────────────────────────────────────────────────────────┘  │
│  Platform: persistence · identity · audit · outbox · scheduler   │
│            document storage · data protection                    │
└───────┬─────────────────┬──────────────────┬─────────────────────┘
        │                 │                  │
┌───────▼──────┐  ┌───────▼───────┐  ┌───────▼────────────┐
│ PostgreSQL   │  │ Chromium      │  │ OpenAI API         │
│ (app + jobs) │  │ (Playwright)  │  │ (server-side only) │
└──────────────┘  └───────────────┘  └────────────────────┘
                                      ┌────────────────────┐
                                      │ Smart plug (S10+)  │
                                      │ provider, TBD      │
                                      └────────────────────┘
```

Everything is one deployable unit plus PostgreSQL. Chromium is a local process. The OpenAI
call and the future smart-plug call are the only outbound integrations, both server-side.

---

## 3. Module map

Fourteen modules. The baseline list was consolidated where cohesion recommended it:
**Expenses** was absorbed into **Finance** (expenses + expense categories + the
inventory/expense double-counting rule live together), and **Marketplace** is not a module —
channels and fee rules belong to **Pricing**.

| # | Module | Owns | Does not own |
|---|---|---|---|
| 1 | **Customers** | Customer, CustomerAddress | Anything commercial |
| 2 | **Catalog** | Product, ProductRecipe and its components (BOM), product images | Material master data, prices |
| 3 | **Inventory** | Supply, SupplyCategory, Filament, FilamentLot, SupplyLot, cost history, StockMovement, StockCount | Product recipes |
| 4 | **Costing** | CostEngine, CostBreakdown, CostExperiment (Laboratory), cost snapshots | Selling price |
| 5 | **Pricing** | SalesChannel, FeeRule, FeeRuleVersion, PriceBracket, PricingEngine | Cost computation |
| 6 | **Quoting** | Quote, QuoteRevision, QuoteItem, snapshots, status history, numbering | Production, sales |
| 7 | **Sales** | Sale, SaleItem (the financial record of a closed deal) | Quote lifecycle |
| 8 | **Production** | ProductionOrder, ProductionOrderItem, actual consumption | Quote lifecycle |
| 9 | **Energy** | Machine, EnergyTariff(+versions), EnergyConsumptionSession, `IEnergyProvider` | Cost aggregation |
| 10 | **Documents** | DocumentType, binding catalogues, DocumentTemplate(+versions), render pipeline, GeneratedDocument | Business data, brand assets |
| 11 | **Finance** | Expense, ExpenseCategory, accounting treatment rules | Sales revenue records |
| 12 | **Reporting** | Read models, analytical queries, dashboard endpoints | Any write |
| 13 | **AI** | AiSettings, AiInsightRun, AiInsightResult, dataset builder, OpenAI client | Any domain write |
| 14 | **Settings** | CompanyProfile, BrandAsset(+versions), BrandingAssignment, app settings, feature toggles, theme registry | Module-specific config semantics, document layout |

Plus two non-domain assemblies:

- **Verce.SharedKernel** — `Money`, `Percent`, `Grams`, `Kwh`, `Quantity`, `DurationSeconds`,
  `Result<T>`, `IEvent`/`IDomainEvent`/`IIntegrationEvent`, `IClock`, rounding helpers.
  No dependencies.
- **Verce.Platform** — `VerceDbContext`, migrations host, Identity, audit interceptor, outbox
  dispatcher, Quartz host, `IDocumentStorage`, `ISecretProtector`. Depends only on SharedKernel.

### 3.1 Dependency direction

```
                       Reporting        AI
                           │            │   (read-only, may query many modules)
        ┌──────────────────┴────────────┴──────────────────┐
        ▼                                                   ▼
     Sales ──────► Quoting ──────► Pricing ──────► Costing
        │              │               │               │
        │              │               │      ┌────────┼────────┐
        ▼              ▼               ▼      ▼        ▼        ▼
   Production ────► Documents      (channels) Catalog Inventory Energy
        │                                          │
        └──────────────► Customers ◄───────────────┘
                              │
                        Settings, Finance
                              │
                        Verce.Platform
                              │
                       Verce.SharedKernel
```

Hard rules:

1. **Arrows point downward only. No cycles.** Enforced by `Verce.Architecture.Tests`.
2. A module may reference another module's **`Contracts`** assembly only — never its
   `Domain`, `Application` or `Infrastructure`.
3. `Reporting` and `AI` are read-only consumers; they may read any module's data through
   contracts or dedicated read models, and may write nothing outside their own tables.
4. Cross-module references are **IDs**, never navigation properties.
5. Upward communication happens through **domain events**, never direct calls.
   (`Quoting` does not call `Production`; it raises `QuoteApproved`, which `Production`
   handles.)

### 3.2 Module internal layout

```
Verce.Modules.Quoting/
├── Contracts/         public DTOs + interfaces other modules may use
├── Domain/            aggregates, value objects, domain events, invariants (pure C#)
├── Application/       use-case handlers, validators, transaction scripts, event handlers
└── Infrastructure/    EF configurations, repositories, external adapters, endpoints
```

Thin modules (Settings, Finance, Customers) may collapse `Domain` + `Application`.
Endpoints are minimal APIs grouped per module and registered by the module's
`IModuleRegistration` implementation; `Verce.Api` is a composition root with no business code.

---

## 4. Layering and use-case flow

```
HTTP endpoint (Infrastructure)
   → validation (FluentValidation, shape only)
   → use-case handler (Application)
        → loads aggregates via repositories (roots only, never child sets)
        → invokes domain behaviour (Domain)  ← all business rules live here
        → UnitOfWork.Execute: ONE transaction, ONE DbContext, N save/dispatch waves
        → outbox messages written in the same transaction, dispatched after commit
   → response DTO
```

**The unit of work runs multiple `SaveChanges` calls, not one.** A synchronous event handler may
create or modify another aggregate, and those changes must be persisted by a later wave inside
the same transaction. The full algorithm is
[ADR-0012 §2](architecture/ADR-0012-domain-events-and-outbox.md#3-the-unit-of-work-algorithm):

```
BEGIN TRANSACTION
  wave 0: command mutates aggregates
  loop:
      collect synchronous + post-commit events
      exit when no events AND no pending writes      ← quiescent
      guard waveIndex <= MAX_EVENT_WAVES (8)
      SaveChanges()                                   ← audit + version interceptors
      dispatch synchronous events (handlers may mutate / raise more)
  write outbox rows; SaveChanges()
COMMIT
```

Any exception anywhere rolls the whole thing back. There is no partial commit.

- **No business logic in endpoints.** Endpoints map HTTP to a handler and back.
- **No domain logic in EF configurations.**
- Handlers return `Result<T>`; domain rule violations map to HTTP 409/422 with a stable
  `errorCode` the frontend can translate to pt-BR.
- `IClock` is injected everywhere; no `DateTime.Now` in domain or application code.

---

## 5. Persistence strategy

### 5.1 One database, one DbContext, one schema per module

A single `VerceDbContext` maps every module. Each module owns a PostgreSQL schema
(`customers`, `catalog`, `inventory`, `costing`, `pricing`, `quoting`, `sales`, `production`,
`energy`, `documents`, `finance`, `ai`, `settings`, `platform`).

Rationale (full form in [ADR-0001](architecture/ADR-0001-system-architecture.md)):
a single context gives a single transaction across modules — which quote approval →
production order creation genuinely needs — with none of the connection-sharing machinery
that per-module contexts require at this scale. The boundary protection that per-module
contexts would have provided is instead enforced by architecture tests plus the ban on
cross-module navigation properties. Migration path if the system ever splits: schemas are
already separated, so extracting a module means extracting its schema.

- Entity configurations physically live in the owning module (`Infrastructure/Persistence`)
  and are discovered by assembly scanning.
- Cross-module foreign keys **are** created at database level (referential integrity matters
  more than purity here) with `ON DELETE RESTRICT`, but generate **no** navigation property.
- Migrations live in `Verce.Platform.Migrations`, one migration per sprint change set.

### 5.2 Identifiers, concurrency, deletion

- **PK by table category** ([ADR-0011 §1](architecture/ADR-0011-identifiers-and-concurrency.md)):
  domain entities and master data use `uuid` (UUID v7, app-generated); system-defined reference
  tables use a stable textual `code`; technical/framework tables (`quote_number_counter`,
  `data_protection_keys`, Identity, Quartz) use whatever their algorithm or framework requires;
  join tables use a composite FK pair. Categories are declared by marker interfaces, so the
  architecture test is executable and needs no per-table exception list.
- **UUID v7 is identity and index locality, never business ordering.** Never `ORDER BY id`.
  Business keys (`quote_number`, `sku`) are separate unique columns. A dedicated persisted
  sequence may stabilize ordering without becoming identity: Customer pagination uses the
  internal immutable `creation_sequence` defined by ADR-0011 §1.2.1, never its UUID.
- **Optimistic concurrency** via an explicit `version bigint not null default 1` on every
  aggregate root, advanced **once per Unit of Work** by
  an interceptor whenever **any** entity inside the aggregate changes — root, child or
  grandchild. `xmin` is **not** used: it changes only when the root's own physical row is
  updated, so a child-only edit would leave it untouched and two concurrent transactions editing
  different children of one aggregate would both commit
  ([ADR-0011 §2](architecture/ADR-0011-identifiers-and-concurrency.md)).
  Children are always loaded and modified **through their root** — repositories expose roots,
  never child sets — which is what makes the bump reliable.
- **Soft delete** (`deleted_at timestamptz null`, global query filter) only on master data
  referenced by history: Customer, Product, Supply, Filament, SalesChannel, Machine,
  ExpenseCategory, DocumentTemplate. Master data additionally has `is_active` for
  "no longer offered but not deleted".
- **Transactional records are never deleted**: Quote, QuoteRevision, Sale, ProductionOrder,
  StockMovement, Expense, AuditLog, GeneratedDocument. They are canceled, never removed.
- **Application-owned domain and master-data tables** carry `created_at`, `created_by`,
  `updated_at`, `updated_by` (`timestamptz` UTC; `uuid` user references), populated by an
  interceptor. **Framework-owned tables** (Identity, Quartz, `data_protection_keys`) **keep
  their framework schema and are not modified** to satisfy this convention; append-only tables
  that already record their own instant do not duplicate it. `docs/DATA-MODEL.md` is the
  authority for physical shape. The **business** audit trail is `platform.audit_log`
  ([ADR-0010](architecture/ADR-0010-audit-strategy.md)), not these convenience columns.

### 5.3 Time

- All instants are `timestamptz` stored in **UTC**.
- The organization timezone (default `America/Sao_Paulo`) is a setting, applied at the edges:
  formatting, report bucketing, and **business dates**.
- **Business dates** (`quote_date`, the daily numbering counter, expiration date) are `date`
  columns computed in the organization timezone, not UTC. Rendering the number `260906-1`
  from a UTC date at 22:00 BRT would produce the wrong day; the numbering service therefore
  takes the local date from `IClock.OrganizationToday()`.

### 5.4 Reads

Writes always go through EF Core. Reporting reads are allowed to bypass the domain:

- S1–S11: EF Core projections (`Select` into DTOs, `AsNoTracking`).
- S12+: Dapper inside `Reporting.Infrastructure` for aggregate queries, plus database views
  (`reporting.v_*`) for the stable metrics. **No materialized report tables in v1** — the
  transactional model is designed to derive every metric in [DATA-DICTIONARY](DATA-DICTIONARY.md).
  Materialization becomes justified only when a measured query exceeds ~500 ms.

---

## 6. Domain events and asynchronous work

Two distinct mechanisms ([ADR-0012](architecture/ADR-0012-domain-events-and-outbox.md)):

**Synchronous domain events** (`IDomainEvent`) — raised by aggregates into an **in-memory pending
buffer**, drained into a per-wave snapshot, then dispatched **between save waves inside the same
transaction** (§4). Used when the side effect must be atomic with the cause. Example:
`QuoteApproved` → create `ProductionOrder`. If the production order fails, the approval rolls
back.

The buffer contract closes the acknowledgement question: an event is **drained out of the
aggregate before the save**, dispatched from the frozen snapshot only, and never returned to a
buffer — so within one Unit of Work each event instance is dispatched **exactly once**. Events
raised by handlers land in the buffer and belong to the **next** wave. `EventId`, `OccurredAt`,
`CorrelationId` and `CausationId` are stamped at construction; `WaveIndex` at dispatch
([ADR-0012 §2](architecture/ADR-0012-domain-events-and-outbox.md)).

Three rules make this safe, all enforced by architecture tests:

1. **One `DbContext`, one transaction.** A handler may not inject `IDbContextFactory<>`,
   `IServiceScopeFactory`, `IServiceProvider`, or call `BeginTransaction()`. A handler with its
   own context would write outside the transaction and survive a rollback.
2. **No I/O.** `HttpClient`, `IDocumentRenderer` and `IAiClient` are banned from synchronous
   handlers — those belong to outbox consumers.
3. **No swallowed exceptions.** A handler that fails fails the command. Partial application of a
   wave is the inconsistency this design exists to prevent.

**Integration events** (`IIntegrationEvent`) — written to `platform.outbox_message` in the same
transaction, claimed after commit by a Quartz job using `FOR UPDATE SKIP LOCKED`. Used when the
effect must not be able to fail the business transaction, or is slow: rendering a PDF, running
an AI insight, polling a smart plug, sending mail.

Lifecycle `PENDING → PROCESSING → PROCESSED | FAILED`, with a **fencing token** so a stale worker
cannot overwrite a live one, leases for crash recovery, a bounded retry ladder, a terminal
`FAILED` state carrying an administrative **disposition**, per-consumer idempotency keys and
Owner-only manual requeue
([ADR-0012 Part II](architecture/ADR-0012-domain-events-and-outbox.md#part-ii--the-transactional-outbox)).

The two interfaces are **distinct types**: a concrete event implements exactly one, so routing an
invariant-critical effect through the outbox is a compile-time question, not a review question.

> **An operation required to hold a transactional invariant must never go through the outbox.**
> The outbox guarantees eventual delivery, not atomicity.

Initial event catalogue: see [DOMAIN-MODEL §9](DOMAIN-MODEL.md#15-domain-events).

### 6.1 Scheduled jobs (Quartz.NET, PostgreSQL job store)

| Job | Cadence | Purpose |
|---|---|---|
| `ExpireQuotesJob` | hourly | Move eligible revisions to `EXPIRED` (see STATE-MACHINES §2.4) |
| `OutboxDispatcherJob` | every 15 s | Claim and process `outbox_message` (`FOR UPDATE SKIP LOCKED`) |
| `OutboxLeaseReclaimJob` | every 1 min | Return crashed `PROCESSING` rows with expired leases to `PENDING` |
| `OutboxRetentionJob` | daily | Prune `PROCESSED` after 90 days; **never** prunes a `FAILED` message whose disposition is `ACTIVE` |
| `OutboxStallProbe` | with the readiness probe | Measure `now() - available_at` over **eligible** messages only (§ADR-0012 25.1) |
| `AiInsightScheduledRunJob` | configurable, off by default | Periodic insight generation (S13) |
| `EnergyPollJob` | S10, off by default | Poll the smart-plug provider |
| `DatabaseBackupJob` | S16 | `pg_dump` to the configured storage target |

Jobs are idempotent and safe to run concurrently with a user session; each takes an advisory
lock on its job key so two instances never overlap.

---

## 7. Calculation architecture

The cost and price engines are **pure, deterministic, side-effect-free** classes in
`Costing.Domain` and `Pricing.Domain`. They take a fully materialized input record and return
a breakdown. They never query the database, never read the clock, never read configuration
directly.

```
CostInput  ──► CostEngine  ──► CostBreakdown  ──► PricingEngine ──► PriceBreakdown
(resolved      (pure)          (itemized,        (pure)             (fee, margin,
 material                       explainable)                         suggested price,
 prices,                                                             expected profit)
 tariffs,
 machine
 rates)
```

Resolution of "which price/tariff/fee applies at instant *t*" happens in the Application
layer (`CostInputBuilder`, `FeeRuleResolver`) **before** the engine runs, so the engine has no
temporal dependency and is trivially unit-testable. This separation is what makes snapshots
possible: the resolved input *is* the snapshot.

Both engines carry a `CalculationEngineVersion` constant persisted with every snapshot, so a
future formula change can be detected rather than silently reinterpreting old records.
See [CALCULATION-RULES](CALCULATION-RULES.md).

---

## 8. Snapshot architecture (summary)

Full decision in [ADR-0003](architecture/ADR-0003-cost-and-price-snapshots.md).

**Hybrid, two-layer:**

1. **Typed relational columns** on `quote_item_cost_snapshot` for every value that drives the
   math or is aggregated in reports (material cost, energy cost, machine cost, labor cost,
   wastage, total cost, commission percent, fixed fee, desired margin, suggested price, final
   price, expected profit, effective margin). Queryable, indexable, type-safe.
2. **A `jsonb` `breakdown` document** on the same row holding the full explanation tree: each
   filament component with grams and price/kg at that instant, each supply with quantity and
   unit cost, the tariff version used, the fee rule version used, and the engine version.
   Not queried for reports; rendered for "explain this price" and for the PDF.

Referenced master data is additionally captured by ID **and** by denormalized display name, so
a renamed or deleted product still renders correctly on an old document.

---

## 9. Document engine and branding (summary)

Full decisions in [ADR-0007](architecture/ADR-0007-document-template-engine.md) (pipeline,
bindings, conditionals, pagination), [ADR-0015](architecture/ADR-0015-brand-assets-and-application-branding.md)
(brand assets, uploads, logo resolution) and [ADR-0016](architecture/ADR-0016-document-render-snapshots.md)
(render snapshots and immutability).

```
Block editor (React, S14)
      ↓ produces
DocumentTemplateVersion.definition (jsonb, schema-versioned, block tree + theme tokens)
      ↓ +
RenderContext (typed, built per DocumentType by a resolver in the owning module,
               validated against that type's BINDING CATALOGUE)
      ↓ block renderers (values HTML-encoded; no string replacement, no expressions)
HTML + CSS  (server-side)
      ↓ Playwright / Chromium  (network blocked, fonts embedded)
PDF bytes
      ↓
GeneratedDocument  =  pdf + rendered html + render_data_snapshot
                      + template_version_id + brand_asset_version_ids
                      (content-addressed by sha256)
```

Three document types in v1: `QUOTE`, `PRODUCTION_ORDER`, `SHIPPING_LABEL` (the label with
`SIMPLE` and `FULL` layout variants). New types are added by registering a `DocumentType`, a
binding catalogue and an `IRenderContextResolver` — no pipeline change.

Four properties are load-bearing:

1. **Bindings are a controlled catalogue**, not free-form token substitution. Unknown paths fail
   at publish time, and paths absent from the catalogue (`internal_notes`, every cost and margin
   field) can never be printed by any template — a security boundary, not a convention.
2. **Templates store intent; documents store resolution.** A `Logo` block says
   "inherit the default"; the generated document records the concrete
   `brand_asset_version_id` it resolved to. Replacing a logo therefore cannot alter an issued
   proposal.
3. **Issued documents are immutable.** Re-rendering inserts a new row; nothing overwrites a
   document a customer received.
4. **Nothing is single-page.** Repeating headers, repeating table headers, page numbering and
   break-avoidance are pipeline features, not template tricks.

The system bootstraps with `VERCE | Proposta Comercial Padrão` as ordinary seed data
([DEFAULT-PROPOSAL-TEMPLATE](DEFAULT-PROPOSAL-TEMPLATE.md)) — **no template is compiled into the
renderer.** Until S14 ships the visual editor, templates are authored as JSON in the same
storage format, so the editor is purely additive.

Application branding (system logo, compact logo, favicon, product name `VERCE 3D`, subtitle
`Laboratório de Custos`) is resolved from Settings via `/api/settings/branding`. **No visual
asset is imported by a React component.**

---

## 10. Frontend architecture

- **React + TypeScript + Vite**, served same-origin by the API in production; Vite dev proxy
  in development. No CORS, no token in `localStorage` — the session is an HttpOnly cookie.
- **Structure mirrors backend modules**: `src/features/<module>/` with `api/`, `components/`,
  `hooks/`, `routes/`. Shared UI in `src/ui/`.
- **Server state**: TanStack Query. **Form state**: React Hook Form + Zod.
  No global store beyond session/theme/settings context.
- **API types are generated** from the backend OpenAPI document; hand-written duplicates of
  backend DTOs are forbidden (drift is the main source of money-formatting bugs).
- **Money crosses the wire as a string**, not a JS number (`"128.40"`), and is formatted with
  `Intl.NumberFormat('pt-BR')`. The frontend **never recomputes prices** — it displays what
  the engine returned. Any client-side arithmetic on money is a defect.
- **Theming by design tokens**: CSS custom properties on `:root`, a `ThemeDefinition` record
  per theme, `data-theme` attribute switching, light/dark aware.
  Themes may change color, radius, density — never behaviour ([ADR-0014](architecture/ADR-0014-frontend-architecture-and-theming.md)).
- **Desktop-first**, responsive down to tablet; accessibility baseline WCAG 2.1 AA
  (keyboard reachable, visible focus, labeled inputs, 4.5:1 contrast in every theme).

---

## 11. Testing strategy

| Level | Tool | Scope |
|---|---|---|
| Domain unit | xUnit + FluentAssertions | Invariants, state machines, suffix algorithm |
| Calculation | xUnit, table-driven | Every rule ID in CALCULATION-RULES, plus golden cases |
| Architecture | NetArchTest | Module dependency direction, no cross-module navigations, no `float` in domain, no `DateTime.Now` |
| Integration | Testcontainers + PostgreSQL | Numbering concurrency, snapshots, outbox, expiration job |
| Frontend unit | Vitest + Testing Library | Components, hooks, formatting |
| E2E | Playwright | Quote → revision → approval → production order → PDF |

Mandatory tests from day one of implementation:

- Concurrent quote creation never produces duplicate numbers (integration, parallel writers).
- Changing a filament price does not change any existing quote total (integration).
- `commission + margin >= 1` is rejected, never computed (unit).
- Revision suffix sequence: 1→(hidden), 2→B, 26→Z, 27→AA, 28→AB (unit).
- Rounding: engine total equals PDF total equals database total equals API payload (integration).

---

## 12. Local environment and deployment

`docker-compose.yml` provides PostgreSQL (and pgAdmin, optional). The API and the frontend run
on the host during development; Chromium is downloaded by Playwright on first run.

Production target v1: a single Linux container (API + static frontend + Chromium) plus a
managed or containerized PostgreSQL, behind TLS. Configuration by environment variables;
secrets by environment variables or the host secret store, never in `appsettings.json`.

Health endpoints: `/health/live` and `/health/ready` (anonymous, **status word only**), plus
`/api/platform/health` (`Owner`, full breakdown). `degraded` returns **HTTP 200** — a failed
outbox message must never make an orchestrator replace the container; only infrastructure
failure, including a stalled dispatcher, returns 503
([OPERATIONS Section 9](OPERATIONS.md#9-health-endpoints)).
Structured logging (Serilog) to stdout with a redaction enricher (SECURITY §6).

---

## 13. Non-functional targets (v1)

| Attribute | Target |
|---|---|
| Data volume, year 1 | < 5k quotes, < 50k stock movements — trivially within a single PostgreSQL |
| Home dashboard load | < 500 ms server time |
| Quote PDF generation | < 3 s |
| Concurrent users | < 10 |
| RPO / RTO | 24 h / 4 h with nightly `pg_dump` (S16) |
| Availability | Business hours; a restart is acceptable |

These targets are the justification for refusing distributed infrastructure. If any is
exceeded by an order of magnitude, revisit ADR-0001 — not before.
