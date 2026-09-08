# ADR-0006 — Estimated vs Actual Cost

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

A quote says 90 g of filament. Production actually used 96 g — a failed first layer, a purge, a
denser infill than planned. Energy estimated at 0,21 kWh may measure 0,26 kWh at the wall.
The gap between planned and real is where the actual margin lives, and it is invisible in a
spreadsheet that overwrites the estimate with the reality (or never records the reality at all).

The brief makes this a **structural** requirement: the domain must support it from the start,
not acquire it in S11.

## Decision

### 1. Two distinct concepts, two distinct storage locations, one derived comparison

| | Estimated cost | Actual cost |
|---|---|---|
| Created | at quote / experiment time | after production |
| Stored in | `quoting.quote_item_cost_snapshot` | `production.production_order_item_actual_material` + `energy.energy_consumption_session` |
| Mutable | **never** | append-only records |
| Prices used | material price **at quote time** | material price **at consumption time** |
| Purpose | pricing, the customer-facing commitment | reality, margin truth |

**Variance is never stored.** It is computed at read time
([CR-09.4](../CALCULATION-RULES.md#cr-094--variance-always-derived-never-stored)). Storing it
would create a third number capable of disagreeing with the two it derives from — the exact
class of inconsistency this system exists to eliminate.

### 2. Actual consumption records

`production_order_item_actual_material`: `material_kind`, `material_id`, `actual_quantity`,
`unit_cost_at_consumption`, `recorded_at`, `recorded_by`, `stock_movement_id`.

Two properties matter:

- **`unit_cost_at_consumption` is captured, not looked up later.** The spool consumed in
  October cost what it cost in October. This is the same freezing principle as
  [ADR-0003](ADR-0003-cost-and-price-snapshots.md), applied to the actual side.
- Recording consumption emits a `StockMovement(OUT)` in the same transaction, so "what did we
  use" and "what is left" can never disagree.

Actual energy arrives as `EnergyConsumptionSession` rows linked to the production item, whatever
their source (`MANUAL`, `SMART_PLUG`).

### 3. Planned material is snapshotted onto the production order

`production_order_item_planned_material` holds the exploded BOM (material, name, color,
quantity) copied from the quote snapshot. It exists for two reasons: the shop-floor document
must be correct even after the recipe is edited, and variance needs a *planned* figure at the
same granularity as the *actual* one. Comparing an actual per-material quantity against a
recipe that has since changed would produce a meaningless variance.

### 4. `Sale.cost_basis` — the honesty flag

`sales.sale.cost_basis ∈ {ESTIMATED, MIXED, ACTUAL}`.

A sale is created with `total_cost_amount` from the quote snapshot and `cost_basis =
ESTIMATED`. As production items report actual consumption, the cost is recomputed and the flag
advances to `MIXED`, then `ACTUAL`. Every profit report that shows a cost must show which basis
it used. A margin computed from estimates and a margin computed from reality are different
claims, and presenting them identically would be misleading.

### 5. What is reconcilable and what is not

| Cost component | Actual available? |
|---|---|
| Filament | **yes** — grams weighed or estimated from the slicer/printer report |
| Supplies | **yes** — counted |
| Energy | **yes** — smart plug or typed kWh |
| Machine time | **yes** — actual print duration |
| Labor | **yes** — typed minutes |
| Manual cost lines | **no** — carried over from the estimate |
| Wastage | **not applicable** — wastage *is* the thing actual consumption measures |

Wastage deserves emphasis: the estimated side applies a percentage because the real loss is
unknown; the actual side has no wastage line because the loss is already inside the measured
consumption. Applying both would double count. `CR-09.3` therefore omits wastage from the
actual total.

### 6. Reporting

Variance is reported per dimension — grams per filament, kWh, hours, money per cost group,
total — and as a relative percentage (`null` when the estimate is zero, never a division by
zero rendered as `∞` or `0%`). Realized margin and margin variance follow in
[CR-09.5](../CALCULATION-RULES.md#cr-095--realized-margin).

## Alternatives considered

- **Overwrite the estimate with the actual** — rejected: destroys the commitment made to the
  customer and makes variance uncomputable. It is what the spreadsheet does, and it is the
  problem.
- **Store variance columns** — rejected: a derived value stored is a value that can drift. It
  would also have to be recomputed whenever a late consumption record arrives.
- **Model actual cost as another snapshot on the quote** — rejected: actual cost belongs to
  production, not to a commercial document. Writing to the quote would violate revision
  immutability.
- **A single `cost` field with a `kind` discriminator** — rejected: estimated and actual coexist
  for the same item and must be comparable simultaneously.
- **Defer the whole concept to S11** — rejected by the brief and by good sense: retrofitting a
  planned-vs-actual split into an existing cost model means rewriting the snapshot, the
  production order and every report. The tables and the boundary are defined in S0; only the UI
  and the reconciliation flow wait for S11.

## Consequences

**Positive:** the product can answer "did this job actually earn what we thought?"; stock
consumption and cost reconciliation share one write path; the estimate stays a faithful record
of the commitment.

**Negative:** the operator must record actual consumption for the feature to have value, and
they will not always do it — hence `MIXED`, hence the basis flag, hence reports that state
their basis instead of pretending. Recording consumption is one more shop-floor step; S11 must
make it fast (pre-filled with the planned quantities, one field to adjust).

## Compliance checks

- Unit test: 90 g planned, 96 g actual → `+6 g`, `+0,066667`.
- Unit test: estimate 0 → relative variance `null`, not an error and not `0`.
- Integration test: recording actual consumption creates exactly one stock movement and moves
  `cost_basis` correctly.
- Integration test: reconciling a sale never modifies the linked quote revision or its snapshot.
