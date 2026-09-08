# ADR-0003 — Cost and Price Snapshots

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

**The critical rule of the product:** changing a filament price, a supply cost, an energy
tariff, a marketplace fee, a product or a recipe must **not** change a quote that already
exists. A quote is a statement made on a date. If reopening a three-month-old quote shows
different numbers than the PDF the customer received, the system is worse than a spreadsheet.

At the same time the system must **explain** any past price ("why was this R$ 128,40?") and
must **aggregate** past costs and margins in reports without reading thousands of JSON blobs.

Those two needs pull in opposite directions: explanation wants a rich, nested, evolving
structure; aggregation wants flat, typed, indexable columns.

## Decision

**A hybrid, two-layer snapshot, stored on `quoting.quote_item_cost_snapshot` (1:1 with
`quote_item`).**

### Layer 1 — typed relational columns (the queryable layer)

Every value that drives the arithmetic or appears in a report gets its own `numeric` column:
`filament_cost`, `supplies_cost`, `packaging_cost`, `manual_cost`, `energy_kwh`,
`energy_price_per_kwh`, `energy_cost`, `machine_hours`, `machine_hourly_rate`, `machine_cost`,
`labor_minutes`, `labor_hourly_rate`, `labor_cost`, `direct_cost`, `wastage_rate`,
`wastage_cost`, `unit_total_cost`, `commission_percent`, `fixed_fee`, `fixed_fee_application`,
`fee_clamp_applied`, `price_rounding_policy`, `calculation_engine_version`.
Full list in [DATA-MODEL §9](../DATA-MODEL.md#9-quoting-schema).

### Layer 2 — a `breakdown jsonb` document (the explanation layer)

The complete `CostBreakdown` + `PriceBreakdown` tree: every filament component with its name,
grams and price per kg at that instant; every supply with quantity, unit and unit cost; every
manual line; the energy source; the ordered pricing steps. Rendered for "explain this price"
and for the PDF. **Never queried in aggregate.**

### Reference columns are references, not sources

`fee_rule_version_id`, `energy_tariff_version_id`, `product_recipe_id` are stored for
traceability ("which rule was applied"), but the **values** are already frozen in layers 1 and
2. Reading a snapshot never joins to live master data to obtain a number. Those FKs are
`ON DELETE RESTRICT`, so the referenced version cannot vanish, but even if a row were somehow
lost the snapshot would still reproduce the quote.

### Denormalized display data

`quote_item.product_name_snapshot`, `quote_revision.customer_snapshot` (name, document, e-mail,
phone, address at issue time). A renamed product or an edited customer address must not alter a
document already sent.

### The immutability boundary

Once a `QuoteRevision` exists, **no field affecting money changes**. Only `status`,
`superseded_by_revision_id` and appended history rows change. Editing is
clone + change + new revision ([ADR-0004](ADR-0004-quote-numbering-and-revisioning.md)).

Even within a new revision, **untouched items keep their original snapshot verbatim** — they
are copied, not recomputed. Re-pricing is an explicit user action that visibly marks the
affected items. Without this rule, a user who edits the quantity of item 2 would silently
re-price item 1 at today's filament cost, which is the retroactive change the rule forbids,
merely delayed by one edit.

## Alternatives considered

### A. Pure JSONB snapshot — rejected
One `jsonb` column holding everything. Simple to write, and this was the tempting option.
Rejected because reporting is a first-class requirement: "margin by product by channel by
month", "estimated vs actual", "which product's margin is deteriorating" all need aggregation
over snapshot values. Doing that over JSONB means functional indexes on every path,
`::numeric` casts everywhere, no type safety, and silent breakage the day the JSON shape
changes. The reporting requirements (brief §27) make this a wrong default.

### B. Pure relational snapshot — rejected
Every component in its own child table (`quote_item_filament_snapshot`, etc.). Fully queryable
and type-safe, but it triples the write path and the join count to serve a use case —
"explain this one price to one human" — that is inherently document-shaped and never
aggregated. It also freezes the shape of the explanation: adding a new cost concept later
requires a migration on historical tables.

### C. Recompute from versioned master data — rejected
Store only IDs and timestamps, and rebuild the price by resolving every price/fee/tariff
version as of the quote date. Elegant, and it is why the master data *is* versioned. Rejected
as the primary mechanism because it makes historical correctness depend on the *permanent*
correctness of every resolver and every version window, forever. A single bug in a temporal
query, one gap in a validity range, one migration that merges two rules — and every past
document changes. The snapshot is the guarantee; version history is the corroboration.

### D. Full event sourcing — rejected
See [ADR-0010](ADR-0010-audit-strategy.md).

## Consequences

**Positive**
- Historical integrity is guaranteed by data, not by the continued correctness of code.
- Reports aggregate typed columns directly, with normal indexes.
- "Explain this price" reads one row.
- A future change to the cost formula cannot alter past documents; `calculation_engine_version`
  makes the difference visible.

**Negative**
- Duplication between layer 1 and layer 2 (the same numbers appear twice). They are written
  together, in one transaction, by one engine result — so they cannot drift — but a reviewer
  will notice the redundancy and should understand it is deliberate.
- Snapshots are wide rows. At the expected volume (< 5 000 quotes/year) this is irrelevant.
- Correcting a genuine past mistake requires a new revision, not an edit. This is the intended
  behaviour, and the UI must make it feel natural rather than obstructive.

## Compliance checks

- Integration test: create a quote; change filament price, supply cost, energy tariff, fee rule
  and the product recipe; reload the quote — **every** monetary value is unchanged and the
  breakdown still renders.
- Integration test: edit one item in a quote with three items; the other two snapshots are
  byte-identical in the new revision.
- Architecture test: no query in `Quoting` joins `quote_item_cost_snapshot` to
  `inventory.*` or `pricing.*` to obtain a monetary value.
