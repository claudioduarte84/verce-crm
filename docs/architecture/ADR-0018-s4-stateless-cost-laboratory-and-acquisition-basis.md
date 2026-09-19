# ADR-0018 — S4 Stateless Cost Laboratory and Acquisition Basis

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-14
- **Sprint:** S4

> **Supersession.** This ADR supersedes ADR-0002 §3 only for the persisted
> `costing.default_wastage_rate` and the related Cost Laboratory wastage input. All other
> percentage settings retain ADR-0002's fraction representation.

## Context

S3 records immutable purchase-receipt quantity and cost snapshots, but deliberately did not
choose the costing policy. S4 needs a useful cost laboratory before Product, Recipe, Machine,
Production and sales-price concepts exist. Persisting experiments or inventing those future
aggregates now would make S4 own concepts whose invariants are not yet known.

## Decision

S4 exposes a stateless `POST /api/costing/calculate`. The API resolves settings and inventory
facts, normalizes entered quantities through S3's closed `SupplyUnitConversion`, and passes a
fully resolved input to the pure `CostEngine`. The engine uses decimal arithmetic only, rounds
component money and totals to six internal decimal places with half-up rounding, and returns an
explainable breakdown. It never queries a database, reads configuration or mutates inventory.

The material cost policy is `WEIGHTED_AVERAGE_ACQUISITION`:

```text
sum(PurchaseReceipt.QuantityDeltaBaseUnit × PurchaseReceipt.UnitCostSnapshot)
────────────────────────────────────────────────────────────────────────────
sum(PurchaseReceipt.QuantityDeltaBaseUnit)
```

Only immutable, positive, cost-bearing `PurchaseReceipt` movements participate. Manual
adjustments and current stock do not change this estimated acquisition basis. If no eligible
receipt exists, calculation fails with `COST_BASIS_UNAVAILABLE` unless the line supplies a
manual cost per base unit; that line is then labelled `MANUAL_OVERRIDE`.

Wastage is entered as percentage points from 0 through 100. A line override wins over the
scenario default, which wins over the persistent global setting `costing.default_wastage_rate`.
The scenario and line values are transient, but that global default is persisted. Effective material quantity is
`normalizedRequired × (1 + wastagePercent / 100)`. Labor similarly uses a manual hourly-rate
override before `costing.default_labor_hourly_rate`. Machine cost is minutes divided by 60 times
the manually entered hourly rate. Additional direct costs are explicit non-negative lines.

For this one Setting S3's stored fraction convention (`0.05 = 5%`) is replaced by percentage
points (`5 = 5%`). The S4 data migration multiplies only this key by 100 on upgrade and divides
by 100 on Down, canonicalizing the result with PostgreSQL's `trim_scale` so no mathematically
unnecessary trailing zeros survive (`0.05 → "5"`, not `"5.00"`); malformed legacy numeric text
fails deterministically and the row is left untouched. The key name is retained for backward
compatibility. Percentage points match operator-facing Cost Lab input and avoid a surprising `5`
meaning 500% in a bounded 0–100 configuration catalogue.

**The migration also increments `AppSetting.Version`** on both Up and Down, in the same
statement that changes `Value`. This is deliberate, not incidental: the migration changes the
*semantic meaning* of the stored text, not merely its formatting, so a client or editor that
read the row's Version before deployment must never be able to write its pre-migration value
back afterward under the old Version — ordinary optimistic concurrency (ADR-0011 §2) rejects
that write as a conflict instead of silently resurrecting the fractional interpretation. The
version never decrements, including on Down, so it stays monotonic through a rollback.

The picker returns active supplies only. Historical basis remains readable through the explicit
cost-basis endpoint, and calculation with an inactive supply requires explicit opt-in. Exceeding
current stock produces `REQUESTED_QUANTITY_EXCEEDS_CURRENT_STOCK` but does not block calculation
or post a movement.

Owner, Operator and Viewer may read and calculate because the operation is analytical and has no
side effects. Anonymous callers are denied. There are no S4 calculation IDs, scenario records,
audit rows or Costing tables. It includes the targeted Settings data-compatibility migration
described above; no Costing aggregate or schema is introduced.

## Alternatives considered

- **Last purchase:** rejected because one unusually small or expensive receipt would replace the
  entire basis and ignore the other acquisition evidence already recorded by S3.
- **Moving average of remaining stock:** rejected because S4 has no authoritative consumption
  allocation and must not pretend an acquisition estimate is inventory accounting.
- **FIFO/LIFO:** rejected because lots and consumption allocation do not exist.
- **Persisted CostExperiment:** rejected for S4; save/clone/history can be introduced with a
  separately designed aggregate when a product/recipe lifecycle requires it.
- **Client-side formulas:** rejected because browser arithmetic would become a second authority.
- **Printer, energy or production models:** rejected as future-sprint scope.

## Consequences

The laboratory is immediately useful and deterministic without a Costing schema change. A manual override
supports what-if analysis without contaminating master data. The weighted average is explicitly
an estimated acquisition basis, not FIFO/LIFO, remaining-stock valuation, accounting cost or
actual production consumption. Refreshing the page may discard the scenario, by design.

Stock and acquisition-basis reads are analytical read-model queries; a calculation does not promise
one database snapshot across several Supplies. A concurrent receipt can slightly change one basis,
which is acceptable for this estimated simulation. The current weighted-basis query scans eligible
purchase history for selected Supplies; that is acceptable at S4 scale and is not a trigger for a
projection or cache.

## Compliance checks

- Pure engine unit tests cover material, wastage, labor, machine, additional costs, batches,
  precision, warnings and invalid input.
- PostgreSQL integration tests prove weighted acquisition basis, ignored manual adjustments,
  inactive historical reads, authorization, settings precedence and zero inventory side effects.
- Architecture tests prohibit Inventory/Settings/EF references and persistent entities in the
  Costing assembly.
- Frontend tests assert generated DTO usage and server-returned breakdown workflows.
