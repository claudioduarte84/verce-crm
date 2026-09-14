# ADR-0017 — Inventory Ledger, Non-Negative Stock Concurrency, and Unit Normalization

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-14
- **Sprint:** S3

## Context

S3 introduces Supply and Inventory. Three decisions were either explicitly owed or forced by the
sprint brief:

1. [ARCHITECTURE-DEBT §H-007 A](../ARCHITECTURE-DEBT.md) — the stock movement sign convention and
   how two concurrent stock changes racing against the same material are resolved, with a
   **decision deadline of S3** — closed by this ADR.
2. The S3 mission made non-negative stock a **hard, blocking invariant** of the ledger, reversing
   [DATA-DICTIONARY §5](../DATA-DICTIONARY.md#stock-enforcement)'s v1 "warn, never block" policy.
   That policy is superseded for inventory movements specifically (it still applies to quotes and
   production orders, which S3 does not touch).
3. S3 requires supplies denominated in units other than filament grams (resin in mL, packaging in
   `un`, cord in meters) — a purchase or adjustment entered in a different but compatible unit
   must normalize into the supply's own unit before it can be posted to the ledger.

A fourth, related deviation is recorded here because it changes [DOMAIN-MODEL §3](../DOMAIN-MODEL.md#3-inventory-module)
materially: the S1-era plan modeled **Filament as its own aggregate** (with `FilamentLot`,
`SupplyLot`, per-material cost history and a `MaterialKind` discriminator shared with stock
movements). The S3 mission brief is explicit and more specific than that earlier plan — filament
is **optional metadata on a `Supply` row**, not a separate aggregate — and per `CLAUDE.md` §5,
"if the domain spec and the code disagree, the spec in `docs/` wins until an ADR changes it." This
ADR is that change.

## Decision

### 1. Inventory is an append-only ledger of signed deltas, never a mutable stock number

`InventoryMovement` is a child of `Supply` (`IOwnedBy<Supply>`), created only through domain
methods on `Supply` itself (`RecordInitialBalance`, `RecordPurchaseReceipt`, `RecordManualIncrease`,
`RecordManualDecrease`, `RecordCorrection`). There is no `Update` or `Delete` on `InventoryMovement`
— reflection is asserted in `SupplyDomainTests` to keep it that way. A correction to a miscounted
stock is a **new** movement (`Correction`), never an edit of history. This mirrors ADR-0016's
"an issued document is evidence" pattern applied to a ledger instead of a document.

`QuantityDeltaBaseUnit` is stored **signed** (positive for `PurchaseReceipt`, `ManualIncrease`,
`InitialBalance`; negative for `ManualDecrease`; `Correction` derives its own signed delta from the
counted absolute quantity minus current stock). `Supply` derives every delta and the internal
`InventoryMovement` constructor independently validates the permitted sign for its type (and
rejects zero), so type and sign cannot disagree. This lets stock be reconstructed as a plain signed
sum, with no `CASE` expression keyed on a separate direction column.

`Consumption`, `ReturnIn` and `ReturnOut` exist in `InventoryMovementType` today but are **not**
creatable by any S3 endpoint — they are reserved for the Production and Sales modules (S8–S11),
which is why the enum already has room for them without a schema change later.

### 2. Current stock is a cached, derived projection — never independently mutable

`Supply.CurrentStockBaseUnit` exists as a real column for cheap reads (list filters, low-stock
queries), but the **only** code path that writes it is the same `Post()` helper that appends a
movement, in the same aggregate mutation, in the same transaction. There is no endpoint, no
domain method, and no migration script that sets `CurrentStockBaseUnit` directly. If the two ever
disagree, the signed sum of `inventory_movement` rows for that supply is correct by construction.

### 3. Non-negative stock via the aggregate's own `Version` — no new locking primitive (closes H-007 A)

A decreasing movement (`ManualDecrease`, or a `Correction` whose derived delta is negative) is
rejected with `InsufficientStockException` → HTTP 409 `INSUFFICIENT_STOCK` if it would drive
`CurrentStockBaseUnit` below zero. That check runs in memory, against the `Supply` instance loaded
for the current request, before any write is attempted — so a request that is individually
invalid never reaches a race at all.

For the race that remains — two concurrent requests that each individually look valid against the
stock they observed — `Supply` is the aggregate root and `CurrentStockBaseUnit` is one of its own
scalar properties, so mutating it is indistinguishable, from
[ADR-0011 §2](ADR-0011-identifiers-and-concurrency.md#2-concurrency-an-explicit-aggregate-version)'s
point of view, from any other change to the root: `AggregateVersionInterceptor` bumps
`Supply.Version` exactly once per Unit of Work, and EF's optimistic-concurrency check on `Version`
means only one of the two concurrent `SaveChanges` calls commits. The other throws
`DbUpdateConcurrencyException`, mapped to HTTP 409 `CONCURRENCY_CONFLICT`.

Combined: `stock = 100`, two concurrent `-80` requests against the same loaded version resolve to
exactly one `200 OK` (final stock `20`) and one `409` — proven by
`SupplyHttpIntegrationTests.Two_simultaneous_decreases_never_both_succeed_and_stock_never_goes_negative`,
which issues both requests concurrently against a real ASP.NET Core host and PostgreSQL, and was
run repeatedly to confirm it is not flaky.

**No new locking primitive was introduced.** Alternatives considered and rejected:

- **`SELECT ... FOR UPDATE` on the `supply` row** — would work, but duplicates a consistency
  mechanism the aggregate root already owns via ADR-0011, and the two would need to be kept in
  agreement by convention rather than by construction.
- **A separate `StockCount`/ledger-lock service** — rejected: it would make inventory a second
  aggregate boundary for the same invariant `Supply` already owns, which is exactly what
  `CLAUDE.md` §6 ("aggregates are the transactional boundary") rules out.
- **A database `CHECK (current_stock_base_unit >= 0)` constraint** — there is deliberately no
  mutable stock column exposed to any writer for this to guard; the invariant is enforced inside
  the aggregate, strictly before a write is attempted, which produces a domain-meaningful
  `INSUFFICIENT_STOCK` error instead of a bare constraint violation surfaced as a generic 500.

### 4. Unit normalization: a closed, explicit conversion table — not a general unit-of-measure framework

`SupplyBaseUnit` is `Gram | Kilogram | Unit | Milliliter | Liter | Meter | Centimeter`.
`SupplyUnitConversion` holds only the deterministic pairs the mission calls out — kg↔g, L↔mL,
m↔cm — and throws `UNIT_CONVERSION_NOT_SUPPORTED` for anything else, including any conversion
into or out of `Unit` (a count has no compatible sibling unit). Every movement-recording endpoint
accepts a `quantity` + `enteredUnit` and stores that immutable fact (`numeric(18,8)` plus closed
unit catalogue) alongside its normalized delta. Conversion rounds to the authoritative
`numeric(14,4)` base precision before posting; a positive entry that cannot remain positive at that
precision is rejected as `QUANTITY_BELOW_BASE_PRECISION`. The ledger for one supply is therefore
always denominated in exactly one base unit without losing what the operator originally entered.

Rejected: a general, extensible unit-conversion graph/unit-of-measure framework. Nothing in the
product needs unit families beyond these three deterministic pairs, and building the generality
now would invite unit categories (area, volume-by-shape, …) the product has no use for yet — the
same reasoning `CLAUDE.md` applies to premature abstraction generally.

### 5. Purchase receipt cost is a snapshot, not a costing policy

`PurchaseReceipt` persists `UnitCostSnapshot`/`TotalCostSnapshot` (converted alongside the
quantity, so the unit cost is always expressed per base unit) purely as history: "what did the
last purchase cost." `Supply.LatestPurchaseUnitCost` is the same number, cached for cheap display.
UnitCost means cost per entered unit. Supplying one cost deterministically derives the other;
supplying both requires equality after the canonical money calculation and is otherwise rejected
as `PURCHASE_COST_MISMATCH`.
Neither is S4's actual costing policy — S3 makes no choice between FIFO, LIFO or moving-average
cost, and none of that machinery exists yet. [DATA-DICTIONARY §5](../DATA-DICTIONARY.md)'s
"Filament price policy" section (`LAST_PURCHASE` / `MANUAL` / `WEIGHTED_AVERAGE`) is explicitly
**not** implemented by this ADR and remains open for S4 to decide, now scoped to all of Inventory
rather than filament alone.

### 6. Filament is optional `Supply` metadata, not a separate aggregate (supersedes the S1-era plan)

`Supply.FilamentDetails` (`MaterialType`, `Brand`, `ColorName`, `ColorCode?`, `DiameterMm`,
`SpoolNetWeightGrams`) is present only when a supply is filament, mapped as individual nullable
scalar columns on `supply` (not an EF owned type — see §7) rather than a `Filament` aggregate with
its own `FilamentLot`/price history. Consequences of this choice:

- **No `MaterialKind` discriminator.** A supply's `SupplyCategory` (e.g. `FILAMENT`) already
  classifies it; movements and reporting group by `supply_id`/`category_code`, not by a kind
  shared across two different tables.
- **No per-lot stock.** Stock is per `Supply` row (e.g. "PLA Preto Voolt3D 1.75mm"), not per
  physical spool. A specific color/brand/diameter combination that used to require a `Filament` +
  `FilamentLot` pair is now one `Supply` row plus its `InventoryMovement` history — matching the
  mission's explicit "do not model PLA as one generic cost."
- **No separate filament price history / weighted-average machinery.** §5 already covers what S3
  keeps (a last-purchase snapshot) and what it defers (an actual costing policy).

This directly resolves `DOMAIN-MODEL §3.1`'s original justification for a separate aggregate
("cost unit is R$/kg while consumption is in grams" — solved by unit normalization, §4 above;
"purchase model is spool/lot based" — not carried forward; "reporting needs filament consumption
as a first-class dimension" — still available by filtering `Supply.CategoryCode = 'FILAMENT'`,
without a second aggregate).

### 7. Filament details are scalar columns, not an EF owned type

An EF `OwnsOne` for `FilamentDetails` would register as its own entity type in the model, which
would need its own ADR-0011 PK category and would fail
`PkCategoryTests.Every_entity_in_the_model_is_classified_by_exactly_one_category` (an owned type is
neither a root nor cleanly one of the five categories for this shape). Instead, `FilamentDetails`
is a plain C# record computed from private nullable scalar fields on `Supply`
(`_filamentMaterialType`, `_filamentBrand`, …), each mapped via EF's field-only
`b.Property<T>("_fieldName")`, with the public `FilamentDetails` property itself
`b.Ignore()`d. `PkCategoryTests` asserts `FilamentDetails` is never registered as a separate EF
entity type, pinning this decision down as a regression guard.

## Alternatives considered

- **Advisory lock per supply for decreases** — rejected; see §3.
- **Unsigned quantity + separate direction column** — rejected; see §1 (invites the two columns
  disagreeing, which the signed-delta-derived-from-type design makes structurally impossible).
- **General unit-of-measure conversion graph** — rejected; see §4.
- **Keep Filament as a separate aggregate, just add unit normalization to it** — considered, but
  the mission brief is explicit that quantities/units must generalize across *all* supplies
  (resin, packaging, hardware, …), not only filament, which removes the original justification for
  segregating filament into its own model.

## Consequences

**Positive:**
- One aggregate, one concurrency mechanism, one place non-negative stock is enforced.
- A supply's ledger is trivially auditable: `SUM(quantity_delta_base_unit) = current_stock_base_unit`.
- Adding a new supply category (e.g. a future adhesives category) needs a `SupplyCategory` row,
  never a schema change.
- Filament-specific reporting is a filter, not a join across two aggregates.

**Negative:**
- `Supply` carries six nullable filament-only columns that are always null for non-filament rows —
  accepted as the simpler alternative to a second aggregate, matching the mission's explicit
  instruction.
- S4 still owes an actual costing policy decision (FIFO/LIFO/weighted-average); until then,
  `LatestPurchaseUnitCost` must not be read as more than "last purchase," and no code should treat
  it as a costing policy.
- `DOMAIN-MODEL §3`, `DATA-MODEL §4` and `DATA-DICTIONARY §1/§2/§5` from the S1-era plan are
  superseded by this ADR and have been rewritten in the same delivery, per `CLAUDE.md`'s
  documentation discipline.

## Compliance checks

- Test: a decrease larger than current stock is rejected (`INSUFFICIENT_STOCK`) and stock is
  unchanged (`SupplyDomainTests`, `SupplyHttpIntegrationTests`).
- Test: two concurrent decreases against the same stock resolve to exactly one success and one
  `409`, final stock never negative (`SupplyHttpIntegrationTests`, the mandatory concurrency test).
- Test: `InventoryMovement` exposes no `Update`/`Delete`/`Remove` method (reflection check,
  `SupplyDomainTests`).
- Test: an incompatible unit conversion (e.g. grams into a `Unit`-based supply) is rejected
  (`SupplyDomainTests`).
- Test: `FilamentDetails` is never registered as a separate EF entity type
  (`PkCategoryTests`).
- Test: `Supply.CurrentStockBaseUnit` after N movements equals the signed sum of those movements'
  `QuantityDeltaBaseUnit` (covered across `SupplyDomainTests`' purchase/increase/decrease/
  correction cases).
