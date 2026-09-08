# ADR-0013 — Expense, Inventory and Double Counting

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

A spool of PLA costs R$ 89,90. That money can enter the system through two doors:

1. as an **Expense** — "Filamento, R$ 89,90, 06/09";
2. as **material cost** inside every product printed from that spool.

If both are counted in the same profit figure, the filament is paid for twice and every margin
report is wrong. The same problem applies to a printer purchase (an expense *and* a machine
hourly rate) and to electricity (a utility bill *and* per-item energy cost).

The brief asks explicitly: prevent a filament purchase from being registered twice as a cost,
and document how.

## Decision

### 1. Every expense declares an accounting treatment

`finance.expense.accounting_treatment`:

| Value | Meaning | Enters "Custos do mês"? | Reaches profit via |
|---|---|---|---|
| `OPERATING_EXPENSE` | Consumed in the period (energy bill, marketing, rent, freight) | **yes** | directly |
| `INVENTORY_PURCHASE` | Bought as stock (filament spool, bag of screws, packaging) | **no** | material cost when consumed |
| `ASSET_ACQUISITION` | Durable equipment (a printer) | **no** | machine hourly rate (depreciation) |

Only `OPERATING_EXPENSE` rows enter the operational cost figure. The other two are cash
movements that reach the result through a different, already-modeled path.

### 2. Structural prevention: one lot, at most one expense

```sql
UNIQUE (filament_lot_id) WHERE filament_lot_id IS NOT NULL
UNIQUE (supply_lot_id)   WHERE supply_lot_id   IS NOT NULL
CHECK  (NOT (filament_lot_id IS NOT NULL AND supply_lot_id IS NOT NULL))
CHECK  ((filament_lot_id IS NOT NULL OR supply_lot_id IS NOT NULL)
        → accounting_treatment = 'INVENTORY_PURCHASE')
```

Registering a `FilamentLot` optionally creates its linked expense in the same transaction, with
the treatment forced. Because the link is unique, the same purchase cannot be entered twice
through the two doors — the database refuses it. This is prevention by constraint, not by a
warning the operator will click past.

The UI reinforces it: registering an expense in a material category prompts *"esta compra é
estoque? vincule ao lote"*, and an operator who insists on `OPERATING_EXPENSE` for a material
category sees an explicit warning about double counting.

### 3. Energy: two reports, never one sum

Energy is the sharp edge, because there is no lot to link. The utility bill is a real
`OPERATING_EXPENSE`, and per-item energy cost is a real component of product cost. Both are
correct; adding them is not.

The resolution is **two clearly separated reports that are never summed**:

| Report | Question | Includes |
|---|---|---|
| **Resultado de caixa** (Cash Result) | "What entered and left the bank this month?" | sales revenue − operating expenses − inventory purchases − asset acquisitions |
| **Margem por produto** (Product Margin) | "Does this product make money?" | sale price − product cost (materials, energy, machine, labor, fees) |

The dashboard's "Custos" tile shows the **Cash Result** view: operating expenses only. Product
margin lives on the product/sales reports. Each report states its basis in its header. There is
deliberately **no** single screen that adds an energy bill to per-item energy cost, because
that number would be meaningless.

The same reasoning applies to machine depreciation: it appears as `ASSET_ACQUISITION` cash in
the Cash Result and as an hourly rate in Product Margin, never in both at once.

### 4. Consumption, not purchase, is the cost event

For inventory, the moment cost is recognized in the product result is **consumption** — the
`StockMovement(OUT)` produced when actual material is recorded
([ADR-0006](ADR-0006-estimated-vs-actual-cost.md)) — valued at
`unit_cost_at_consumption`. Purchases move cash and increase stock; they do not create product
cost.

## Alternatives considered

- **No treatment field; every expense counts** — the spreadsheet behaviour, and the bug being
  fixed.
- **Forbid material expenses entirely; derive everything from lots** — clean in theory, and
  rejected: the operator sometimes needs to record a purchase without registering a lot (a
  small parts order, a purchase that will not be stock-tracked). Forbidding it drives data
  outside the system.
- **Full double-entry accounting with inventory and COGS accounts** — the textbook-correct
  answer, and disproportionate. It would demand accounts, journals, periods and closing
  procedures from an operator who wants to know whether a keychain is profitable. The
  treatment flag captures the same distinction at a fraction of the complexity.
- **A single "profit" figure reconciling everything** — rejected: cash result and product margin
  answer different questions and legitimately differ (stock bought and not yet sold). Forcing
  one number would hide that difference rather than explain it.
- **Warning only, no constraint** — rejected: warnings are dismissed. The unique index makes
  the error impossible rather than discouraged.

## Consequences

**Positive:** double counting of material purchases is structurally impossible; the operator
sees two honest reports instead of one misleading one; the treatment flag is a single, teachable
concept.

**Negative:**
- Two profit-ish numbers require explanation, and the UI must label them clearly and
  consistently. A user who compares them without reading the labels will be confused — this is
  a documentation and interface obligation, carried by S12.
- Miscategorizing an expense still produces a wrong report (the constraint prevents *double*
  counting, not *mis*-classification). Mitigated by category defaults
  (`expense_category.default_treatment`) and the UI prompt.
- Inventory value is not tracked as a balance-sheet figure in v1; stock is quantity plus last
  cost, not a valued asset. Recorded as a deliberate limitation.

## Compliance checks

- Test: creating a second expense linked to the same filament lot is rejected by the database.
- Test: an expense linked to a lot cannot have treatment `OPERATING_EXPENSE`.
- Test: the monthly cost figure excludes `INVENTORY_PURCHASE` and `ASSET_ACQUISITION`.
- Test: registering a lot with the auto-expense option creates exactly one expense.
