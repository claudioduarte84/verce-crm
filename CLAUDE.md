# CLAUDE.md — Verce 3D | Laboratório de Custos

Project charter and working rules for any agent (human or model) operating on this repository.

---

## 1. What this product is

**Verce 3D | Laboratório de Custos** is a web system for managing a small 3D-printing
manufacturing and personalization operation: customers, materials (filaments and supplies),
product recipes (BOM), cost simulation, pricing per sales channel, quotes with immutable
revisions, sales, expenses, production orders, labels, documents, dashboards and AI insights.

It must be **simple to use from day one** and **structurally ready to grow**.
Anticipated complexity is not scalability.

Primary language of the product UI: **Portuguese (pt-BR)**.
Primary language of code identifiers, database objects and technical docs: **English**.

---

## 2. Non-negotiable architectural rules

1. **Modular Monolith.** One deployable backend, one database. No microservices.
2. **No distributed infrastructure** (RabbitMQ, Redis, Kafka, service bus) without an ADR
   proving an objective need. In-process domain events plus a database outbox are enough.
3. **Money is never `float`/`double`.** `decimal` in C#, `numeric` in PostgreSQL.
   See [ADR-0002](docs/architecture/ADR-0002-money-precision-and-rounding.md).
4. **Percentages are stored as fractions** (`0.175000` = 17.5%), never as `17.50`.
   **Narrow exception:** from S4, `costing.default_wastage_rate` stores percentage points
   (`5` = 5%) under [ADR-0018](docs/architecture/ADR-0018-s4-stateless-cost-laboratory-and-acquisition-basis.md).
5. **Historical documents are immutable.** Changing a filament price, a supply cost, an
   energy tariff or a marketplace fee must never change an already-created quote.
   See [ADR-0003](docs/architecture/ADR-0003-cost-and-price-snapshots.md).
6. **Editing a quote never mutates a revision.** Edit = clone + change + new revision.
   See [ADR-0004](docs/architecture/ADR-0004-quote-numbering-and-revisioning.md).
7. **Marketplace rules are data, never code.** No `if (channel == "Shopee")` anywhere.
   See [ADR-0005](docs/architecture/ADR-0005-marketplace-fee-rules.md).
8. **Estimated cost and actual cost are different first-class concepts.** Variance is always
   derived, never stored. See [ADR-0006](docs/architecture/ADR-0006-estimated-vs-actual-cost.md).
9. **The OpenAI API key never reaches the frontend, never reaches a log, never sits in
   plaintext.** See [ADR-0008](docs/architecture/ADR-0008-ai-integration-and-secret-handling.md).
10. **Configuration over hard-code.** Validity days, rounding policy, label sizes, AI model
    names, themes — all configurable.
11. **No cross-module navigation properties.** Modules reference each other by ID and through
    `*.Contracts` interfaces only. Enforced by architecture tests.
12. **Calculations are deterministic and explainable.** Every computed price must be able to
    render the breakdown that produced it.
13. **An issued document is evidence.** Changing a logo, the company phone, a template or a
    theme must never alter a document already generated. Re-rendering inserts a new document; it
    never overwrites one. See [ADR-0016](docs/architecture/ADR-0016-document-render-snapshots.md).
14. **Brand assets are versioned; documents reference the version, never the asset.**
    See [ADR-0015](docs/architecture/ADR-0015-brand-assets-and-application-branding.md).
15. **No visual asset or template is hard-coded.** Logos resolve from Settings; the default
    proposal is seed data. No `import logo from './logo.png'` anywhere.
16. **Template bindings are a closed catalogue**, not string substitution — and it is a security
    boundary: no template can print `internal_notes`, a unit cost or a margin.
17. **Never trust an uploaded file.** Magic bytes, real decode, re-encode, content-addressed
    name. No SVG in v1. See [SECURITY §8.1](docs/SECURITY.md).

**Added by the S0 gate corrections (2026-09-07):**

18. **One command = one transaction = one `DbContext` = N save waves.** A synchronous event
    handler may create aggregates, so `SaveChanges` runs once **per wave**, not once per command.
    A handler must never open its own `DbContext`, scope or transaction, and must never perform
    I/O. See [ADR-0012 §2](docs/architecture/ADR-0012-domain-events-and-outbox.md).
19. **Never catch a PostgreSQL unique violation and continue.** The transaction is already
    aborted (`25P02`) and everything after it fails. Use `ON CONFLICT … DO NOTHING` or an
    explicit `SAVEPOINT`.
20. **Anything required by a transactional invariant is a synchronous event, never an outbox
    message.** The outbox guarantees eventual delivery, not atomicity.
21. **Concurrency is an explicit `version` on the aggregate root**, bumped by any change to the
    root *or any of its children*. `xmin` is not used — it cannot see child-only mutations.
    Load and modify children **through their root**.
22. **PK convention is by category, not "everything is a uuid"**: domain/master data → UUID v7;
    system reference data → textual `code`; technical/framework tables → whatever they require;
    join tables → composite. **UUID v7 is identity, never ordering — never `ORDER BY id`.**
    See [ADR-0011 §1](docs/architecture/ADR-0011-identifiers-and-concurrency.md).
23. **No credential is ever seeded.** The first `Owner` comes from the `bootstrap-owner` CLI plus
    a single-use, hashed setup token. No password on a command line, no HTTP bootstrap route.
    See [ADR-0009 §6](docs/architecture/ADR-0009-authentication-strategy.md).
24. **The Data Protection wrapping certificate lives outside the database**, and startup
    **fails closed** in Production when it is missing. A key ring stored unprotected beside the
    ciphertext it unlocks is security theatre. See [OPERATIONS](docs/OPERATIONS.md).
25. **Every outbox consumer declares an `idempotency_key`.** Delivery is at-least-once, and a
    retried render must never produce a second `ISSUED` document.

**Added by the S0 re-gate corrections (2026-09-07):**

26. **`IDomainEvent` and `IIntegrationEvent` are different types.** A concrete event implements
    exactly one. Events are **drained out of the aggregate before the save**, dispatched from
    that frozen snapshot, and never returned — so each instance is dispatched exactly once per
    Unit of Work. Events raised inside a handler belong to the **next** wave.
    Order is **COLLECT → SAVE → DISPATCH**, everywhere, with no contradicting version.
27. **`version` starts at 1 and advances by exactly one per Unit of Work**, no matter how many
    children changed or how many waves ran. A root created in this UoW **stays at 1 for the whole
    UoW** — it is registered as handled at creation, so never re-derive "was it Added?" from
    `EntityState` after the first save. Ownership is declared with `IOwnedBy<TParent>` and
    resolved by a registry validated at startup — never by naming conventions.
28. **Writes go through the aggregate root; reads do not.** There is no repository over a
    non-root entity. Reads may project child rows freely.
29. **Every outbox worker transition carries `AND processing_token = :token`.** 0 rows affected
    means your lease expired and someone else owns the message: log, abandon, never overwrite.
    `worker_id` is a label, not a fence.
30. **One terminal-attempt rule:** `attempt_count >= max_attempts` yields `FAILED` — identically
    for an explicit retryable failure and for a crash detected by lease expiry. `attempt_count`
    increments **at claim**, a crash never refunds it, and an exhausted message is **not
    claimable**.
31. **`FAILED` carries `ACTIVE` or `DISMISSED`, and only while `FAILED`.** There is no `RESOLVED`
    disposition — a requeued message that succeeds ends at `PROCESSED`. Retention must never
    purge an unresolved failure.
32. **`degraded` health returns HTTP 200.** A failed PDF must not make an orchestrator replace
    the container. Only infrastructure failure returns 503 — and dispatcher-stall detection
    counts **only eligible** messages (`available_at <= now()`), never one serving its backoff.
33. **Bootstrap, Owner-role mutation and crypto recovery serialize on advisory locks**
    (`8401001` / `8401002` / `8401003`). Counting rows that do not exist yet is not a lock.
34. **The Data Protection wrapping certificate is a *ring*, and v1 retires none of it.** There is
    no supported API to re-wrap existing DP keys, so predecessors are kept indefinitely. Never
    claim rotation "migrates" or "re-wraps" key material.
35. **Missing certificate in Production = the host does not start.** Startup validation exits the
    process; it never binds HTTP, so `/health/ready` cannot report it. Startup validation and
    runtime readiness are different mechanisms. Recovery is an **offline** command.
36. **Secret challenges have two operands from two sources**: expected from a mounted file,
    candidate from a silent prompt. Comparing a file with itself authenticates nobody.
37. **`LAST_OWNER_PROTECTED` guards the API surface only.** The local break-glass `recover-owner`
    may recover the sole Owner; it never removes the `Owner` role.
38. **Audit is written in the business transaction; if audit fails, the command fails.** Rows
    from every wave share one `correlation_id` and differ by `wave_index`. There is no
    `MIGRATION` audit source.

---

## 3. Repository layout (target)

```
/
├── CLAUDE.md
├── docs/                       ← specification; the source of truth for the domain
│   ├── PRODUCT-VISION.md
│   ├── ARCHITECTURE.md
│   ├── DOMAIN-MODEL.md
│   ├── DATA-MODEL.md
│   ├── STATE-MACHINES.md
│   ├── CALCULATION-RULES.md
│   ├── DATA-DICTIONARY.md
│   ├── SECURITY.md
│   ├── ROADMAP.md
│   ├── OPERATIONS.md            ← deployment secrets, bootstrap, key ring, backup/restore
│   ├── ARCHITECTURE-DEBT.md     ← deferred decisions with owning sprints — read before a sprint
│   ├── DEFAULT-PROPOSAL-TEMPLATE.md
│   └── architecture/ADR-*.md
├── src/
│   ├── Verce.Api/                     ← ASP.NET Core host (composition root only)
│   ├── Verce.SharedKernel/            ← Money, Percent, Grams, Kwh, Result, DomainEvent
│   ├── Verce.Platform/                ← persistence, auth, audit, outbox, scheduler, storage
│   └── Verce.Modules.<Module>/        ← Domain / Application / Infrastructure / Contracts
├── frontend/                          ← React + TypeScript + Vite
└── tests/
    ├── Verce.<Module>.Tests/          ← xUnit unit tests
    ├── Verce.Architecture.Tests/      ← NetArchTest boundary enforcement
    ├── Verce.Integration.Tests/       ← Testcontainers + PostgreSQL
    └── e2e/                           ← Playwright
```

---

## 4. Technology baseline (frozen unless an ADR overrides it)

| Concern | Choice |
|---|---|
| Backend | ASP.NET Core 10 / C# |
| Frontend | React + TypeScript + Vite |
| Database | PostgreSQL |
| ORM | EF Core (Dapper only for analytical queries, with justification) |
| Scheduler | Quartz.NET |
| PDF | JSON template → HTML/CSS → Playwright/Chromium |
| Backend tests | xUnit + FluentAssertions + Testcontainers |
| Frontend tests | Vitest + Testing Library |
| E2E | Playwright |
| Local infra | Docker Compose |

---

## 5. Multi-agent workflow

The project runs as a sequence of sprints (S0…S17) defined in
[docs/ROADMAP.md](docs/ROADMAP.md). Each sprint is executed by an agent under an orchestrator.

**Rules for every agent:**

- Read `CLAUDE.md` and the relevant `docs/` files **before** writing code.
- **Read `docs/ARCHITECTURE-DEBT.md` and resolve, in this delivery:**
  (a) rows whose **decision deadline** is your sprint;
  (b) rows marked **`Before S(next)`** — those are an exit requirement of *your* sprint, not the
  next one;
  (c) rows whose **implementation deadline** is your sprint.
  A row marked `Before S6` means S5 cannot close and S6 must not start while it is open.
- Do only the sprint you were assigned. Do not start the next sprint.
- **Never approve your own work.** A different model reviews each delivery.
- **Never generate the prompt for the next agent.** Only the orchestrator does that.
- **Never commit, push or tag** unless the mission text explicitly orders it.
- If you find a genuine architectural defect in the spec, do not silently deviate:
  state the problem, propose the fix, and record it under `OPEN DECISIONS` in your report.
- If the domain spec and the code disagree, **the spec in `docs/` wins** until an ADR changes it.
- Any deviation from an ADR requires a new ADR that supersedes it. ADRs are append-only:
  never rewrite an accepted decision — write a superseding ADR.

**Documentation discipline:** if a sprint changes domain rules, update `docs/` in the same
delivery. Undocumented domain behaviour is a defect.

---

## 6. Coding conventions

- **Domain layer has no EF Core, no ASP.NET, no I/O.** Pure C#.
- **Application layer** orchestrates: use-case handlers, transactions, domain event dispatch.
- **Infrastructure layer** implements repositories, providers, external calls.
- Aggregates are the transactional boundary; mutate one aggregate per transaction where
  practical. Cross-aggregate consistency uses domain events inside the same transaction, or
  the outbox when the side effect must not be able to fail the transaction.
- Prefer **value objects** (`Money`, `Percent`, `Grams`, `Kwh`, `QuoteNumber`) over raw
  primitives in the domain. Persist them as owned types.
- Invalid domain states must be **unrepresentable or rejected at construction**.
  A pricing denominator ≤ 0 raises an error, never a silently wrong number.
- Every rule in `docs/CALCULATION-RULES.md` must have a named unit test referencing its rule ID.
- Database naming: `snake_case`, plural tables, one schema per module.
- C# naming: standard .NET conventions.

---

## 7. Definition of Done for any sprint

- [ ] Behaviour matches `docs/` (or `docs/` was updated as part of the delivery).
- [ ] Domain rules covered by unit tests; calculation rules covered by named tests.
- [ ] Architecture tests pass (no cross-module leakage).
- [ ] No secret in source, in logs, or in any API response.
- [ ] No monetary `float`/`double` anywhere.
- [ ] Migrations reviewed (only in sprints that own schema changes).
- [ ] Final report delivered in the format the mission requested.
- [ ] No commit, push or tag unless explicitly ordered.

---

## 8. Where to look first

| Question | File |
|---|---|
| What are we building and why? | `docs/PRODUCT-VISION.md` |
| How is the system structured? | `docs/ARCHITECTURE.md` |
| What are the aggregates and rules? | `docs/DOMAIN-MODEL.md` |
| What are the tables, keys, indexes? | `docs/DATA-MODEL.md` |
| What states can a quote be in? | `docs/STATE-MACHINES.md` |
| How is cost/price computed exactly? | `docs/CALCULATION-RULES.md` |
| What does this field or metric mean? | `docs/DATA-DICTIONARY.md` |
| Auth, secrets, PII, audit, uploads | `docs/SECURITY.md` |
| How do I deploy, bootstrap, back up, rotate keys? | `docs/OPERATIONS.md` |
| What decisions are deferred, and to which sprint? | `docs/ARCHITECTURE-DEBT.md` |
| What does the default proposal look like? | `docs/DEFAULT-PROPOSAL-TEMPLATE.md` |
| What ships when? | `docs/ROADMAP.md` |
| Why was X decided? | `docs/architecture/ADR-*.md` |
