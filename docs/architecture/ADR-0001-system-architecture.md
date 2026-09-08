# ADR-0001 — System Architecture

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0
- **Deciders:** Architecture agent (Claude Opus 5), under orchestrator baseline

## Context

Verce 3D is an operational system for a small 3D-printing shop: one organization, fewer than
ten users, an expected first-year volume under 5 000 quotes. It must handle strongly relational
data (customers, materials, recipes, quotes, revisions, production, sales), deterministic
monetary arithmetic, immutable history, and a document pipeline — while staying cheap to
operate and easy for a single developer (or a single agent per sprint) to reason about.

The orchestrator supplied a baseline: ASP.NET Core 10, React/TypeScript/Vite, PostgreSQL,
EF Core, Quartz.NET, Playwright PDF, Docker Compose, modular monolith, no microservices, no
distributed infrastructure without objective need.

## Decision

**Accept the baseline.** Build a modular monolith with pragmatic Clean Architecture:

1. **One deployable backend, one PostgreSQL database.** No microservices, no message broker,
   no distributed cache.
2. **Fourteen modules** (ARCHITECTURE §3), each owning a PostgreSQL schema, structured as
   `Contracts` / `Domain` / `Application` / `Infrastructure`. Thin modules may collapse layers.
3. **A single `VerceDbContext`** mapping every module, with entity configurations physically
   located in the owning module and discovered by assembly scanning.
4. **Boundaries enforced by tests, not by process isolation**: `Verce.Architecture.Tests`
   (NetArchTest) asserts the dependency direction, bans cross-module type references outside
   `*.Contracts`, and bans cross-module navigation properties.
5. **Communication upward is by domain event**, never by direct call. `Quoting` does not call
   `Production`; it raises `QuoteApproved`.
6. **Cross-module foreign keys exist at the database level** (`ON DELETE RESTRICT`) but
   generate no navigation property.
7. **Reporting and AI are read-only consumers** and may read across modules.

## Alternatives considered

### A. Microservices — rejected
Would impose network boundaries, distributed transactions and multiple deployments on a system
with one user group and no scaling pressure. Quote approval creating a production order is a
single-transaction requirement; splitting it would demand sagas to solve a problem the design
does not have. The brief also forbids it, and the brief is right.

### B. One `DbContext` per module — rejected, with a caveat
This is the textbook modular-monolith answer and it does enforce boundaries by construction.
Rejected because:

- The single most important consistency rule in the product — approving a quote creates exactly
  one production order, atomically — spans two modules. With separate contexts this needs
  shared-connection/shared-transaction plumbing (`DbContext` sharing a `DbConnection` and
  enlisting in an explicit `IDbContextTransaction`), which is real complexity that every
  handler must get right, forever, to buy boundary enforcement that a test can provide directly.
  The multi-wave unit of work
  ([ADR-0012 §2](ADR-0012-domain-events-and-outbox.md#3-the-unit-of-work-algorithm)) makes this
  sharper still: a command runs several `SaveChanges` waves across module boundaries within one
  transaction, and **every wave must use the same context and the same transaction**. Per-module
  contexts would have to be coordinated across every wave.
- Migrations across contexts against one database need careful ordering and a custom history
  table per context.
- The system is a monolith by decision, not by accident. Paying distributed-persistence costs
  while keeping monolith deployment is the worst of both.

**Caveat / migration path:** schemas are already separated per module, and no cross-module
navigation properties exist. Splitting a module out later means moving its schema and its
configurations — mechanical, not architectural. If a future sprint finds boundary erosion that
tests failed to catch, revisit this decision with evidence.

### C. Vertical slices with no layering — rejected
Attractive for CRUD, wrong here. The cost and pricing engines are genuine domain logic with
invariants that must be unit-testable in isolation and must never touch I/O. A domain layer
earns its keep in `Costing`, `Pricing` and `Quoting`. It does not in `Settings` — which is why
thin modules are allowed to collapse layers rather than perform ceremony.

### D. Event sourcing — rejected
See [ADR-0010](ADR-0010-audit-strategy.md). The requirement is "who changed what and when",
answered by an audit log. Event sourcing would add projection rebuilds and event versioning to
a small product, and the brief explicitly excludes it.

## Consequences

**Positive**
- One transaction spans any set of modules when the domain needs it.
- One deployment, one connection string, one migration history, one log stream.
- Local development is `docker compose up` plus `dotnet run`.
- Refactoring across module boundaries is cheap while the domain is still being learned.

**Negative**
- Module boundaries depend on discipline plus tests rather than on compilation barriers. A
  careless `using` can cross a boundary; the architecture test is the only thing that catches
  it. That test is therefore **not optional** in any sprint.
- A single `DbContext` grows large. Mitigated by per-module configuration files and by the
  schema separation.
- Scaling is vertical only. Accepted deliberately (ARCHITECTURE §13 documents the volumes that
  make this safe, and the threshold that would force a revisit).

**Neutral**
- Dapper is authorized for reporting reads from S12, behind the `Reporting` module. Writes stay
  in EF Core permanently.

## Compliance checks

- `Verce.Architecture.Tests` fails the build on: a cycle between modules, a reference from one
  module's `Domain`/`Application`/`Infrastructure` to another module's non-`Contracts`
  assembly, a navigation property crossing modules, `float`/`double` in a domain assembly,
  `DateTime.Now`/`DateTime.UtcNow` outside `IClock` implementations.
- It also fails on: a synchronous domain event handler taking `IDbContextFactory<>`,
  `IServiceScopeFactory`, `IServiceProvider`, `HttpClient`, `IDocumentRenderer` or `IAiClient`,
  or calling `BeginTransaction` (ADR-0012 §5); a persisted type not in exactly one PK category
  (ADR-0011 §1); a query ordering by `Id` (ADR-0011 §1.1).
