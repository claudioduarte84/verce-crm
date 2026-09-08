# ADR-0010 — Audit Strategy

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

The system must be able to answer, later: **who changed what, and when** — particularly for
costs, fees, quotes, statuses, settings and document templates. The brief also states a
constraint: do not turn every table into event sourcing; event sourcing is **not** a requirement.

## Decision

**Two complementary layers, deliberately distinct.**

### Layer 1 — Business history (domain data)

Purpose-built tables that are part of the domain, shown to users, and read by reports:

| Table | Records |
|---|---|
| `quoting.quote_status_history` | every quote status transition, with trigger and reason |
| `production.production_order_status_history` | every production transition |
| `inventory.supply_cost_history` | cost windows per supply |
| `inventory.filament_price_history` | price windows per filament |
| `inventory.stock_movement` | every stock change (append-only by nature) |
| `quoting.quote_revision` | the revision chain itself |
| `pricing.fee_rule_version`, `energy.energy_tariff_version` | temporal rule/tariff data |

These are not "audit" in the compliance sense — they are the domain's memory. Conversion rate,
variance and price explanation all depend on them, which is why they are modeled explicitly
rather than left to a generic log.

### Layer 2 — Generic audit log (technical)

`platform.audit_log`, written by an **EF Core `SaveChanges` interceptor** for entities marked
`[Auditable]`. **One row per changed entity per save wave** — see §Multi-wave below.

`audit_id` (uuid v7), `occurred_at`, `operation_started_at`, `user_id`, `user_display_name`
(denormalized so it survives user deletion), `entity_schema`, `entity_table`, `entity_id`,
`operation`, `changed_columns text[]`, `old_values jsonb`, `new_values jsonb`,
`correlation_id`, `request_id`, `wave_index`, `source` (`API` / `JOB` / `CLI` / `SYSTEM`).

### Multi-wave alignment *(re-gate correction G, 2026-09-07)*

One command runs as several save/dispatch waves inside one transaction
([ADR-0012 §3](ADR-0012-domain-events-and-outbox.md#3-the-unit-of-work-algorithm)). The audit
model must describe that honestly rather than pretend a command is one save.

**One logical operation = one `correlation_id`.** Every row produced by any wave of that command
carries it, so "what did this action change?" is a single indexed query.

**Multiple waves may legitimately produce multiple rows for the same entity.** If the user's
command sets a quote to `APPROVED` in wave 1 and a handler stamps a field on the same quote in
wave 2, those are **two real state transitions**, and collapsing them would hide that a handler
altered what the user wrote. They are distinguished by `wave_index` and grouped by
`correlation_id`; the audit UI shows them as one operation with an ordered sequence.

**No artificial duplication.** The interceptor runs once per `SaveChanges` and emits rows only
for entries EF reports as changed *in that wave*. An entity written in wave 1 and untouched
afterwards produces exactly one row. Each row carries its own `audit_id` (UUID v7) so a row is
identifiable and a retry inside one wave cannot silently double-insert.

**Timestamps — two, deliberately.** `occurred_at` is the **actual UTC instant of that
mutation**, so the sequence within a command is truthful; `operation_started_at` is the
command's own start, identical on every row of the correlation. Using a single operation-level
timestamp would erase wave ordering; using only the mutation timestamp would make grouping
depend on clock proximity. Both are cheap.

**Audit failure fails the business transaction.** Audit rows are written by the same interceptor,
into the same transaction, with the same `SaveChanges`. If audit persistence fails, the command
fails and rolls back. For this product — where the audited entities are costs, prices, quote
approvals, fee rules and settings — a silently unaudited change to a price is worse than a
failed request. There is no "best effort" audit path and no classification of audited entities
as non-critical.

**Actors.** `source` is `API` (a user request), `JOB` (Quartz; `user_id` is null), `CLI` (an
administrative command such as `bootstrap-owner` or `recover-owner`), or `SYSTEM` (an internal
change with no attributable human — rare).

> **`MIGRATION` was removed** from this enum. Migrations do not pass through the `SaveChanges`
> interceptor, so listing it implied a capability that does not exist. Schema and seed
> operations are recorded by EF Core's own migration history table, and — if operational
> visibility is ever wanted — by a separate `platform.migration_log`, never as entity-audit
> rows.

Audited entities (v1): `Supply`, `Filament`, `FeeRule*`, `EnergyTariff*`, `Product`,
`ProductRecipe`, `Quote*`, `Sale`, `ProductionOrder`, `Expense`, `AppSetting`,
`DocumentTemplate*`, `AiSettings`, `User`/`Role`.

**Not audited:** `StockMovement` and `AuditLog` itself — both already append-only, so auditing
them would double storage for zero additional information. Reads are not audited, with one
exception: **exports are** (they move confidential data out of the system).

### Rules

1. **Append-only.** No endpoint updates or deletes audit rows. `Owner` can read; nobody can write.
2. **No FK to `user`.** Audit must survive user deletion; hence the denormalized display name.
3. **Secrets are recorded as changed, never as values.** `AiSettings.ApiKeyEncrypted` audits as
   `***` in both `old_values` and `new_values`. The *fact* of the change is the security signal;
   the value is not, and storing it would defeat
   [ADR-0008](ADR-0008-ai-integration-and-secret-handling.md).
4. **Correlation id** ties every row written by one request together, so "what did this action
   actually change?" is one query.
5. **`source`** distinguishes a user action from an automatic one (the expiration job writes
   `JOB`), so nobody has to explain why "the system" changed a status at 3 a.m.
6. **Retention:** 24 months, then archived to JSON before pruning (S16).
7. **Sensitive operations** (fee rule change, tariff change, AI settings, user deactivation,
   template publish, backup restore) require `Owner` **and** a reason recorded with the entry.

### Why an interceptor rather than triggers

Database triggers would catch changes made outside the application, which is a genuine
advantage. Rejected because triggers cannot know the **application user** (only the database
user), and "who" is the primary question this feature must answer. Passing the user id into the
session (`SET LOCAL app.user_id`) can bridge that, at the cost of logic split between C# and
PL/pgSQL, invisible to tests and to code review. The interceptor keeps audit in one place, in
the language the team works in, with the user identity naturally available.

Accepted consequence: **direct database edits are not audited.** The mitigation is procedural —
schema changes go through migrations, and production database access is restricted.

## Alternatives considered

- **Event sourcing** — rejected, and the brief says so too. It answers a different question
  ("what happened, in order, as the source of truth") at the cost of projections, rebuilds and
  event versioning forever. The requirement here is "who changed what and when", which a change
  log answers directly. Where the domain genuinely needs ordered history — quote revisions,
  status transitions, stock movements — it is modeled explicitly in Layer 1, which is
  event-sourcing's benefit applied surgically rather than universally.
- **Temporal tables / system-versioned rows** — PostgreSQL has no native support; extensions or
  trigger-based history would duplicate every table's schema and complicate migrations.
- **Auditing everything** — rejected: volume without value. An audit row for every stock
  movement duplicates an append-only table; audit rows for reads would dwarf the data.
- **Logging to files only** — rejected: not queryable, not retained reliably, not joinable to
  entities, and easy to lose.

## Consequences

**Positive:** "who changed this price?" is one indexed query; business history stays meaningful
and user-facing while technical audit stays uniform; storage grows only with meaningful changes;
no event-sourcing tax.

**Negative:**
- Changes made directly in the database bypass audit entirely (mitigated procedurally, stated
  openly).
- `old_values`/`new_values` JSONB grows with wide entities — bounded by auditing only changed
  columns.
- The interceptor must correctly resolve the current user in background jobs, where there is
  none; those rows carry `user_id = null` and `source = 'JOB'`, which is information, not a gap.

## Compliance checks

- Test: updating a supply cost writes one audit row with the correct `changed_columns`,
  `old_values` and `new_values`.
- Test: updating the AI key writes an audit row whose values are `***`.
- Test: no API route can update or delete `platform.audit_log`.
- `MultiWaveAuditSameCorrelation` · `WaveIndexPreserved` · `AuditFailureRollsBackTransaction` ·
  `SystemJobActorRecorded` · `SingleWaveEntityProducesOneRow` — see
  [ROADMAP S1](../ROADMAP.md#s1-acceptance-test-contracts-mandatory).
