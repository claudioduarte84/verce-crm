# ADR-0011 — Identifiers, Concurrency and Deletion

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Revised:** 2026-09-07 (gate blockers B-002, B-006) · **2026-09-07 (re-gate corrections B, F)** · **2026-09-09 (Customer total ordering)**
- **Sprint:** S0

> **Scope note.** Architecture contract for S1 to implement. No code exists yet.

## Context

Three cross-cutting persistence choices affect every table and are cheap now, expensive later:
what a primary key is, how concurrent edits are detected, and what "delete" means.

> **Revision history**
>
> - **2026-09-07 (gate B-002).** `xmin` was used as the concurrency token with the claim that the
>   root's token "covers" its children. False: `xmin` changes only when *that physical row* is
>   updated, so two transactions editing different children of one aggregate both committed.
> - **2026-09-07 (gate B-006).** "Every table's PK is `id uuid`" was false against this system's
>   own data model, making the stated architecture test unwritable.
> - **2026-09-07 (re-gate B).** The `version` decision was right but under-specified: no initial
>   value, no `Added`-root semantics, no rule against multiple bumps for multiple children, no
>   concrete child→root ownership mechanism.
> - **2026-09-07 (re-gate F).** The classification rule "if it can appear as a literal in seed,
>   source or a template it is reference data" was **too broad** — by that test almost anything
>   seeded qualifies. Replaced with lifecycle/ownership semantics, plus a complete per-table
>   classification and a realistic marker policy for framework-owned entities.
> - **2026-09-09 (Customer total ordering).** `name, created_at` can tie, while aggregate UUIDs
>   remain prohibited pagination keys. Added an internal, database-allocated
>   `creation_sequence` as the immutable final Customer-list tie-breaker. It is not Customer
>   identity and is never a public Customer number.

---

## 1. Primary keys: five categories with lifecycle semantics

Classification is by **who owns the lifecycle of the row**, not by where its value can appear.

### Category 1 — Domain entity

Has independent domain identity and lifecycle, created and managed by the application/domain.

**PK: `id uuid` (UUID v7, generated in the application).** Marker: `IDomainEntity`.
Aggregate roots additionally implement `IAggregateRoot`; owned children implement
`IOwnedBy<TParent>` (§2.5).

*Examples:* `Customer`, `Product`, `ProductRecipe`, `Quote`, `QuoteRevision`, `QuoteItem`,
`Supply`, `Filament`, `ProductionOrder`, `Sale`, `Expense`, `BrandAsset`, `DocumentTemplate`,
`GeneratedDocument`, `AuditLog`, `StockMovement`.

Why v7 and not v4: v7 embeds a timestamp prefix, so successive inserts land close together in
the B-tree; random v4 keys scatter writes and cause page splits.
Why not `bigserial`: IDs appear in URLs and sequential integers leak business volume;
application-side generation makes the ID available **before** `SaveChanges`, which the
domain-event and outbox flows depend on; fixtures get stable IDs without sequence juggling.

### Category 2 — Master data

**Operator-managed** business configuration and catalogues. The operator creates, renames and
retires these at runtime; the application ships seed rows only as a convenience.

**PK: `id uuid` (UUID v7).** Marker: `IMasterData` (which implies `IDomainEntity` semantics for
the PK test but distinguishes intent).

*Examples:* `SupplyCategory`, `ProductCategory`, `ExpenseCategory`, `FilamentMaterial`,
`FilamentBrand`, `SalesChannel`, `Machine`, `EnergyTariff`.

### Category 3 — Reference data

A **closed, system-owned enumeration** represented relationally only because foreign keys and
metadata need a table. The set is fixed by the application version; the operator cannot add to
it; the **code is the semantic identity**, and changing a code is a breaking change.

**PK: a stable textual `code`.** Marker: `IReferenceData`.

*Examples:* `documents.document_type` (`QUOTE`, `PRODUCTION_ORDER`, `SHIPPING_LABEL`),
`settings.brand_asset_type` (`PRIMARY_LOGO`, …).

> The distinction against Category 2 is **who may create a row**, not where the value appears.
> An operator can invent a new filament material; nobody can invent a new document type without
> a code change, because a resolver and a binding catalogue must exist for it.

### Category 4 — Technical / framework table

Exists for infrastructure or for an algorithm, not for domain identity.

**PK: whatever the technology requires.** Classification is by **`ITechnicalTable`** for
application-owned technical types, and by **infrastructure configuration registry** for
framework-owned types we do not author (§1.2).

*Examples:* `quote_number_counter` (composite `(series, counter_date)` — the allocation
algorithm's key), `data_protection_keys` (framework `int` identity), Identity tables,
Quartz tables.

`platform.outbox_message` is technical **and** carries a `uuid` PK: nothing forces a framework
shape on it, and a v7 id gives insertion locality on a hot table. Category and PK shape are
independent axes.

### Category 5 — Join table

No independent lifecycle; identity **is** the relationship; no attributes beyond the link; never
referenced by another table.

**PK: composite of the two foreign keys.** Marker: `IJoinTable`, *only if* the relationship is
modelled as a class at all.

The model currently has **none** — every apparent many-to-many carries its own attributes and is
therefore Category 1. The rule exists so the first one is not an ad-hoc exception.

### 1.1 Marker policy — realistic about what we do not own

We cannot make `DataProtectionKey`, `IdentityUser` or Quartz's tables implement our interfaces:
they are framework types.

> **Application-owned entity types are classified by marker interface.
> Framework-owned types are classified by explicit registration in infrastructure
> configuration** — `TechnicalEntityRegistry.Register<DataProtectionKey>()` and equivalents,
> asserted at startup against the EF model.

The architecture test therefore reads: *every entity type in the EF model is classified either
by a marker interface it implements or by an entry in the technical registry; a type classified
by neither, or by two, fails.* No per-table exception list, and no impossible requirement.

### 1.2 UUID v7 is identity, never ordering *(closes L-002)*

- **Never `ORDER BY id`.** Order by `created_at`, a business date (`issued_date`, `sold_date`),
  an explicit sequence (`number_sequence`, `revision_index`, `line_number`) or `sort_order`.
- The timestamp inside a v7 value is a storage-layout detail: generated on the application host,
  carrying no timezone semantics, and not the business event time — `IClock` supplies that. Two
  rows created in one transaction have no meaningful v7 ordering relative to each other.
- Any sort or pagination keyed on `id` is a defect. Enforced by an architecture test.

**Business keys stay separate and human-readable**: `quote.number_text` (`260906-4`),
`product.sku`, `sale.sale_number`, `production_order.order_number`. Never show a UUID to a user
as a document identifier.

### 1.2.1 Customer list total ordering

The authoritative ordering for every paginated Customer list, filtered or unfiltered, is:

```text
name ASC, created_at ASC, creation_sequence ASC
```

`customers.customer.creation_sequence` is a persistence-only `bigint`, required, unique and
immutable after insert. PostgreSQL allocates it from
`customers.customer_creation_sequence_seq` (`bigint`, start 1, increment 1, no cycle), owned by
the column. Allocation uses the column's database default; application code never computes
`MAX(...) + 1`. Concurrent inserts therefore receive distinct values. Sequence and rollback gaps
are expected, are never filled, and no value is reused after soft deletion.

EF maps the field as database-generated on add and prevents application updates after save.
Normal seed, test and import paths omit it and receive the database default; v1 exposes no path
for callers to supply or override a value. Restores preserve both stored values and the sequence
state as part of the PostgreSQL backup.

This field is an explicit creation-order sequence permitted by the rule above, not a primary key,
foreign key, aggregate identity, Customer number or commit chronology. It is absent from request
and response DTOs and from the UI. Its only v1 purpose is to make pagination total and stable when
collation considers names equal and `created_at` also ties. The final unique value resolves every
such tie without weakening the UUID prohibition.

### 1.3 Complete table classification

Every table in [DATA-MODEL](../DATA-MODEL.md), classified. There is no "miscellaneous".
This table is **documentation**; the architecture test derives from the implemented types.

| schema.table | Category | PK | Classified by | Aggregate root |
|---|---|---|---|---|
| `platform.user` (+ Identity tables) | Technical/framework | framework | registry | — |
| `platform.audit_log` | Domain entity (append-only) | uuid | `IDomainEntity` | self |
| `platform.outbox_message` | Technical | uuid | `ITechnicalTable` | self |
| `platform.outbox_message_attempt` | Technical | uuid | `ITechnicalTable` | `outbox_message` |
| `platform.account_setup_token` | Domain entity | uuid | `IDomainEntity` | self |
| `platform.data_protection_keys` | Technical/framework | `int` identity | registry | — |
| `platform.data_protection_key_archive` | Technical | uuid | `ITechnicalTable` | self |
| `platform.qrtz_*` | Technical/framework | framework | registry | — |
| `settings.company_profile` | Domain entity (singleton) | uuid | `IAggregateRoot` | self |
| `settings.app_setting` | Domain entity | uuid | `IAggregateRoot` | self |
| `settings.brand_asset_type` | **Reference** | `code` | `IReferenceData` | — |
| `settings.brand_asset` | Domain entity | uuid | `IAggregateRoot` | self |
| `settings.brand_asset_version` | Domain entity (owned) | uuid | `IOwnedBy<BrandAsset>` | `brand_asset` |
| `settings.branding_assignment` | Domain entity | uuid | `IAggregateRoot` | self |
| `customers.customer` | Domain entity | uuid | `IAggregateRoot` | self |
| `customers.customer_address` | Domain entity (owned) | uuid | `IOwnedBy<Customer>` | `customer` |
| `inventory.supply_category` | **Master data** | uuid | `IMasterData` | self |
| `inventory.supply` | Domain entity | uuid | `IAggregateRoot` | self |
| `inventory.supply_cost_history` | Domain entity (owned) | uuid | `IOwnedBy<Supply>` | `supply` |
| `inventory.supply_lot` | Domain entity (owned) | uuid | `IOwnedBy<Supply>` | `supply` |
| `inventory.filament_material` | **Master data** | uuid | `IMasterData` | self |
| `inventory.filament_brand` | **Master data** | uuid | `IMasterData` | self |
| `inventory.filament` | Domain entity | uuid | `IAggregateRoot` | self |
| `inventory.filament_price_history` | Domain entity (owned) | uuid | `IOwnedBy<Filament>` | `filament` |
| `inventory.filament_lot` | Domain entity (owned) | uuid | `IOwnedBy<Filament>` | `filament` |
| `inventory.stock_movement` | Domain entity (append-only) | uuid | `IAggregateRoot` | self |
| `inventory.stock_count` | Domain entity | uuid | `IAggregateRoot` | self |
| `catalog.product_category` | **Master data** | uuid | `IMasterData` | self |
| `catalog.product` | Domain entity | uuid | `IAggregateRoot` | self |
| `catalog.product_recipe` | Domain entity (owned) | uuid | `IOwnedBy<Product>` | `product` |
| `catalog.product_filament_component` | Domain entity (owned, nested) | uuid | `IOwnedBy<ProductRecipe>` | `product` |
| `catalog.product_supply_component` | Domain entity (owned, nested) | uuid | `IOwnedBy<ProductRecipe>` | `product` |
| `catalog.product_cost_line` | Domain entity (owned, nested) | uuid | `IOwnedBy<ProductRecipe>` | `product` |
| `energy.machine` | **Master data** | uuid | `IMasterData` | self |
| `energy.energy_tariff` | **Master data** | uuid | `IMasterData` | self |
| `energy.energy_tariff_version` | Domain entity (owned) | uuid | `IOwnedBy<EnergyTariff>` | `energy_tariff` |
| `energy.energy_consumption_session` | Domain entity | uuid | `IAggregateRoot` | self |
| `pricing.sales_channel` | **Master data** | uuid | `IMasterData` | self |
| `pricing.fee_rule` | Domain entity | uuid | `IAggregateRoot` | self |
| `pricing.fee_rule_version` | Domain entity (owned) | uuid | `IOwnedBy<FeeRule>` | `fee_rule` |
| `pricing.price_bracket` | Domain entity (owned, nested) | uuid | `IOwnedBy<FeeRuleVersion>` | `fee_rule` |
| `costing.cost_experiment` | Domain entity | uuid | `IAggregateRoot` | self |
| `costing.cost_experiment_component` | Domain entity (owned) | uuid | `IOwnedBy<CostExperiment>` | `cost_experiment` |
| `quoting.quote_number_counter` | **Technical** | composite `(series, counter_date)` | `ITechnicalTable` | — |
| `quoting.quote` | Domain entity | uuid | `IAggregateRoot` | self |
| `quoting.quote_revision` | Domain entity (owned) | uuid | `IOwnedBy<Quote>` | `quote` |
| `quoting.quote_item` | Domain entity (owned, nested) | uuid | `IOwnedBy<QuoteRevision>` | `quote` |
| `quoting.quote_item_cost_snapshot` | Domain entity (owned, nested) | uuid (= `quote_item_id`) | `IOwnedBy<QuoteItem>` | `quote` |
| `quoting.quote_status_history` | Domain entity (owned, append-only) | uuid | `IOwnedBy<QuoteRevision>` | `quote` |
| `quoting.quote_document` | Domain entity (owned) | uuid | `IOwnedBy<QuoteRevision>` | `quote` |
| `production.production_order` | Domain entity | uuid | `IAggregateRoot` | self |
| `production.production_order_item` | Domain entity (owned) | uuid | `IOwnedBy<ProductionOrder>` | `production_order` |
| `production.production_order_item_planned_material` | Domain entity (owned, nested) | uuid | `IOwnedBy<ProductionOrderItem>` | `production_order` |
| `production.production_order_item_actual_material` | Domain entity (owned, nested) | uuid | `IOwnedBy<ProductionOrderItem>` | `production_order` |
| `production.production_order_status_history` | Domain entity (owned, append-only) | uuid | `IOwnedBy<ProductionOrder>` | `production_order` |
| `sales.sale` | Domain entity | uuid | `IAggregateRoot` | self |
| `sales.sale_item` | Domain entity (owned) | uuid | `IOwnedBy<Sale>` | `sale` |
| `finance.expense_category` | **Master data** | uuid | `IMasterData` | self |
| `finance.expense` | Domain entity | uuid | `IAggregateRoot` | self |
| `documents.document_type` | **Reference** | `code` | `IReferenceData` | — |
| `documents.document_template` | Domain entity | uuid | `IAggregateRoot` | self |
| `documents.document_template_version` | Domain entity (owned) | uuid | `IOwnedBy<DocumentTemplate>` | `document_template` |
| `documents.generated_document` | Domain entity | uuid | `IAggregateRoot` | self |
| `ai.ai_settings` | Domain entity (singleton) | uuid | `IAggregateRoot` | self |
| `ai.ai_insight_run` | Domain entity | uuid | `IAggregateRoot` | self |
| `ai.ai_insight_result` | Domain entity (owned) | uuid | `IOwnedBy<AiInsightRun>` | `ai_insight_run` |
| `reporting.v_*` | Views | — | not entities | — |

Reference data is deliberately **rare**: only two tables. Everything an operator can create is
master data with a surrogate key, so renaming "Consumíveis" is a label change, not an identity
change.

---

## 2. Concurrency: an explicit aggregate `version`

**Every aggregate root carries `version bigint not null`, mapped as the EF concurrency token.**
`xmin` is not used anywhere: it changes only when the root's own physical row is updated, so a
child-only edit leaves it untouched and two transactions editing different children of one
aggregate would both commit.

### 2.1 The rule

> **Any mutation anywhere inside an aggregate — root row, child, grandchild; insert, update or
> delete — makes the root's `version` advance by exactly one for the whole Unit of Work.**

### 2.2 Initial value: `1`

A newly created aggregate is inserted with **`version = 1`**. Not 0. One value, every entity, no
exceptions — a mixed convention makes every test and every assertion ambiguous.

The first successful mutating Unit of Work after creation takes it to `2`.

### 2.3 `Added` roots — created at 1 and **registered as handled**

> **Corrected 2026-09-07 (re-gate H-RG2-001).** The previous text said the interceptor "skips
> roots in state `Added`". That was a real bug. After wave 1's `SaveChanges` the root's EF state
> becomes `Unchanged`, so a wave-2 handler modifying the same root would find it *not* in the
> handled set and bump it `1 → 2` — inside the very Unit of Work that created it. EF entity state
> cannot be used to remember that a root was born in this UoW; the memory must be UoW-scoped.

A root entering the Unit of Work in state `Added`:

- is **inserted with `version = 1`**;
- has **no optimistic-concurrency predicate** applied (there is no prior version to match);
- is **immediately registered in `VersionHandledAggregates`**, and stays registered for the rest
  of the Unit of Work;
- **never receives an increment in this Unit of Work**, no matter how many later waves touch it;
- children added alongside it produce no additional bump.

So a command that creates a quote and whose `QuoteCreated` handler stamps a field on that same
quote in wave 2 commits **`version = 1`**, not 2. Creation and its synchronous domain
consequences are one committed Unit of Work and therefore one semantic version state.

The next *independent* request that loads that aggregate at `1` and modifies it commits `2`.

### 2.4 One version transition per aggregate per Unit of Work

This is the frozen semantics.

> **`version` is the committed revision number of the aggregate — not a count of internal
> persistence waves, and not a count of changed children.**

| Case | Loaded | Committed |
|---|---|---|
| New aggregate + 3 children, one wave | — | **1** |
| New aggregate, later wave modifies it again | — | **1** |
| Existing root, 3 children modified in one wave | 4 | **5** |
| Existing root modified in wave 1 and again in wave 2 | 7 | **8** |
| Existing root, independent later request | 1 | **2** |

**Mechanism.** `AmbientOperationContext.VersionHandledAggregates` is a **Unit-of-Work-scoped**
set of aggregate root identities, populated the first time a root is dealt with and never
cleared until the UoW ends. The `AggregateVersionInterceptor` runs before each `SaveChanges`:

```
for each ChangeTracker entry in state Added / Modified / Deleted:
    root := ownershipRegistry.ResolveRoot(entry)          // §2.5
    if root.Id in VersionHandledAggregates:  continue     // already settled this UoW

    if root is being INSERTED in this UoW:                // tracked by the interceptor,
        root.Version := 1                                 // NOT read from EF state
        // no concurrency predicate on insert
    else:
        root.Version := root.Version + 1                  // expected = loaded value
        mark root Modified

    VersionHandledAggregates.Add(root.Id)
```

Two properties make this correct across waves:

1. **"Is this root being inserted?" is decided once, in the wave that first sees it**, and
   recorded in the set — never re-derived from `EntityState` afterwards. This is the H-RG2-001
   fix.
2. Roots already in the set are left alone. Later waves persist their new values against the
   already-settled token, which matches because it is this transaction's own uncommitted row and
   this transaction holds the row lock.

If two sets read more clearly in implementation, `CreatedAggregates` and `BumpedAggregates` are
an acceptable decomposition, provided membership in **either** means "version already settled for
this UoW".

Rejected alternative — **one bump per wave**: it leaks an implementation detail (how many event
waves the platform happened to run) into a value clients hold and compare. Adding a domain event
handler would change the version arithmetic of an unrelated command.

**What is preserved either way** — and is the property that actually matters: *every* child
mutation of an *existing* aggregate is observed by a conditional update against the version that
was loaded, so a concurrent writer conflicts.

### 2.5 Child → root ownership: an explicit registry, no naming conventions

```csharp
public interface IAggregateRoot : IDomainEntity { long Version { get; } }

public interface IOwnedBy<TParent> where TParent : class   // declares the IMMEDIATE parent
{
    Guid ParentId { get; }
}
```

Each owned entity declares its **immediate parent**, not the root:
`ProductFilamentComponent : IOwnedBy<ProductRecipe>` and `ProductRecipe : IOwnedBy<Product>`.
Nesting therefore resolves by **walking the declared chain**, which is why a deep child does not
have to carry a denormalized root id.

`AggregateOwnershipRegistry` is built **once at startup** from those declarations plus EF model
metadata (used only to locate the FK property, never to guess from a name):

- every `IOwnedBy<>` chain is walked to its terminus;
- a chain that does not terminate at an `IAggregateRoot`, that is cyclic, or that resolves to
  two different roots → **startup failure**, not a runtime surprise;
- the resolved root type and the navigation path are cached, so the interceptor is O(depth) with
  no reflection on the hot path.

Resolving the *root instance* from a tracked child walks the cached navigation path through the
change tracker. That works because of §2.6 — children are always loaded through their root, so
the parents are tracked.

No convention-based reflection: nothing infers ownership from a class name, a namespace or a
property name.

### 2.6 Writes go through the root; reads do not

> **Application services may not independently persist aggregate-owned children.**

| Operation | Allowed path |
|---|---|
| **Write** (insert/update/delete a child) | load the **root** via its repository, mutate through root behaviour, save |
| **Read** for display or reporting | project child rows directly (`AsNoTracking`, Dapper in `Reporting`) — **no** root load required |

Repositories and facades exist **only at aggregate-root boundaries**. There is no
`IQuoteItemRepository`. Read models are free to query any table.

This is not stylistic: if a child could be modified without its root tracked, the interceptor
would have nothing to bump and the concurrency guarantee would silently vanish. An architecture
test asserts no repository type is generic over a non-root entity.

### 2.7 The conditional update

Whatever EF emits, the invariant behaviour is:

```sql
UPDATE <root>
SET version = @expectedVersion + 1, …
WHERE id = @id AND version = @expectedVersion;
```

| Rows affected | Meaning |
|---|---|
| `1` | success |
| `0` | **concurrency conflict** → `DbUpdateConcurrencyException` → HTTP 409 `CONCURRENCY_CONFLICT` |

### 2.8 Scope and exemptions

Every aggregate root in §1.3 carries `version`. **Exempt, by nature:**

- **Append-only tables** — `stock_movement`, `audit_log`, `quote_status_history`,
  `production_order_status_history`, `outbox_message_attempt`: only ever inserted.
- **Immutable rows** — `quote_item_cost_snapshot`, `brand_asset_version`,
  `generated_document` (`ISSUED`), `document_template_version` (`PUBLISHED`).
- **Technical tables** — `quote_number_counter` is serialized by its atomic upsert and row lock;
  `outbox_message` by `FOR UPDATE SKIP LOCKED` plus its fencing token
  ([ADR-0012 §15](ADR-0012-domain-events-and-outbox.md)).

Note that an append-only child still bumps its root when the root is a real aggregate:
appending a `quote_status_history` row bumps `quote.version`, because the quote's observable
state changed.

---

## 3. Deletion

| Category | Policy |
|---|---|
| Master data referenced by history (Customer, Product, Supply, Filament, SalesChannel, Machine, ExpenseCategory, DocumentTemplate) | **Soft delete** — `deleted_at timestamptz null`, EF global query filter, partial unique indexes scoped to `deleted_at IS NULL` |
| Transactional records (Quote, QuoteRevision, Sale, ProductionOrder, StockMovement, Expense, AuditLog, GeneratedDocument) | **Never deleted.** Canceled, superseded or archived |
| Child rows of a live aggregate (recipe components, quote items) | Hard delete **only while the parent is still mutable** |

`is_active` and `deleted_at` mean different things and both exist: `is_active = false` is "no
longer offered, keep it visible"; `deleted_at` is "hide it". Conflating them removes the ability
to retire a product without hiding it from the operator.

**Partial unique indexes are mandatory with soft delete.** `UNIQUE (document)` would block
re-registering a customer whose earlier record was soft-deleted; `UNIQUE (document) WHERE
deleted_at IS NULL` is correct. Every unique constraint on a soft-deletable table carries this
predicate.

## 4. Standard columns

| Table kind | Convention |
|---|---|
| **Application-owned domain / master data** (mutable) | `created_at timestamptz not null`, `created_by uuid null`, `updated_at timestamptz null`, `updated_by uuid null`, populated by an interceptor from `IClock` and `AmbientOperationContext` |
| **Append-only business history** (`audit_log`, `stock_movement`, `*_status_history`, `outbox_message_attempt`) | carries its own event instant; **no meaningless `updated_*` columns** — nothing ever updates the row |
| **Framework-owned** (ASP.NET Core Identity, Quartz, `data_protection_keys`) | **retains the framework schema, unmodified** |

> **Framework tables are never altered to satisfy an application convention.** Adding columns
> to a table whose migrations the framework owns risks breaking those migrations for no
> benefit. Prose follows reality, not the reverse: **[DATA-MODEL](../DATA-MODEL.md) is the
> authority for physical shape.**

These are convenience metadata — **not** the audit trail
([ADR-0010](ADR-0010-audit-strategy.md)) and **not** a concurrency token.

## Alternatives considered

- **`bigserial` PKs** — rejected for enumerability and for requiring a round trip before an ID
  exists.
- **UUID v4** — index fragmentation with no compensating benefit.
- **Database-generated UUIDs** — loses pre-`SaveChanges` availability, which the event and outbox
  flows rely on.
- **Composite natural keys** — natural keys change; every FK would carry multiple columns.
- **"Every table has a uuid PK"** — false against the model; a rule the model violates is worse
  than no rule.
- **"Reference data = anything that appears as a literal"** — the re-gate's F finding: far too
  broad, would have swept every seeded master-data row into code-keyed identity, making a
  category rename a breaking change. Replaced by *who may create a row*.
- **Per-table exceptions in the architecture test** — an exception list grows without review.
- **Requiring marker interfaces on framework entities** — impossible; hence the registry (§1.1).
- **`xmin`** — cannot express aggregate-wide concurrency (§2).
- **A token on every mutable row** — protects rows, not invariants: two users editing different
  components of one recipe would both succeed and the second would price against a BOM neither
  saw.
- **Pessimistic locking for aggregates** — adds deadlock surface; reserved for the two technical
  tables where the lock *is* the algorithm.
- **One version bump per wave** — rejected in §2.4.
- **Denormalizing `AggregateRootId` onto every deep child** — would make resolution O(1) but
  duplicates a derivable fact on every row and can drift; the cached registry path is cheap
  enough.

## Consequences

**Positive:** one classification rule with no exception list; `version` means "committed
revision of this aggregate" and nothing else; child mutations are genuinely protected;
ownership is validated at startup rather than discovered in production.

**Negative:**
- `version` must be bumped by the interceptor, so raw SQL that updates an aggregate bypasses
  concurrency control. Mitigated by the interceptor (nobody writes `version` by hand) plus an
  architecture test banning raw-SQL writes outside `Reporting` reads.
- Bumping the root on child changes means concurrent edits to *unrelated* children of one large
  aggregate now conflict. Intended — they share an invariant — but it makes aggregate size a
  real design concern: an oversized aggregate produces false conflicts.
- The ownership registry is startup machinery that must be maintained as entities are added.
  Its failure mode is loud (startup) rather than silent, which is the point.
- UUIDs are 16 bytes and unpleasant in logs; always log the business key alongside.
- Soft delete requires every raw query in `Reporting` to respect the filter — a standing review
  item.

## Compliance checks

See [ROADMAP S1](../ROADMAP.md#s1-acceptance-test-contracts-mandatory) for Given/When/Then. Summary:
Versioning — `AggregateInitialVersionIsOne` (B-1), `AddedAggregateWithChildrenStartsAtOne`
(B-2), `AddedAggregateRemainsVersionOneAcrossWaves` (B-2a), `AddedThenConcurrencyAfterCommit`
(B-2b), `MultipleChildrenSingleVersionBump` (B-3), `ExistingAggregateBumpsOnceAcrossWaves`
(B-4), `ConcurrentDifferentChildrenConflict` (B-5), `NestedChildResolvesCorrectRoot` (B-6),
`OwnershipRegistryRejectsBrokenChain` (B-7), `NoRepositoryOverNonRootEntity` (B-8).

Keys and classification — `EveryApplicationTableHasExactlyOneCategory` (F-1),
`DomainAndMasterPKGuid` (F-2), `ReferencePKCode` (F-3), `TechnicalTableClassification` (F-4),
`FrameworkTechnicalEntityAllowedWithoutMarker` (F-5), `JoinCompositePK` (F-6),
`DataModelFiveCategoriesMatchAdr` (F-7),
`FrameworkTablesDoNotRequireApplicationAuditColumns` (F-8), `NoOrderByIdAnywhere` (F-9).
