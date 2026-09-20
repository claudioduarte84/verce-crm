# CALCULATION RULES — Verce 3D | Laboratório de Custos

Every rule has an ID. **Every rule ID must have a named unit test** (`CR_07_MarketplacePrice_…`).
The engines implementing these rules are pure functions: no database, no clock, no config reads.

Related: [ADR-0002 Money, Precision and Rounding](architecture/ADR-0002-money-precision-and-rounding.md) ·
[ADR-0003 Snapshots](architecture/ADR-0003-cost-and-price-snapshots.md) ·
[ADR-0005 Fee Rules](architecture/ADR-0005-marketplace-fee-rules.md)

---

## 0. Precision and rounding contract

### CR-00.1 — Scales

| Concept | C# | PostgreSQL | Scale |
|---|---|---|---|
| Presented money (unit price, line total, document totals) | `decimal` | `numeric(18,2)` | 2 |
| Intermediate money (component cost, cost per gram, unit cost) | `decimal` | `numeric(18,6)` | 6 |
| Price per kilogram | `decimal` | `numeric(18,6)` | 6 |
| Percent (commission, margin) | `decimal` | `numeric(9,6)` | 6, stored as **fraction** |
| S4 Laboratory wastage input | `decimal` | scenario/line overrides not persisted; global Setting is persisted | percentage points 0–100; divide by 100 in formula |
| Weight in grams | `decimal` | `numeric(12,3)` | 3 |
| Weight in kilograms | derived, never stored | — | — |
| Energy | `decimal` | `numeric(12,4)` | 4 (kWh) |
| Power | `int` | `integer` | watts |
| Quantity | `decimal` | `numeric(14,4)` | 4 |
| Duration | `int` | `integer` | seconds |
| S4 Laboratory labor/machine time | `decimal` | not persisted | minutes |

Constants in `Verce.SharedKernel`:
```csharp
public const int MoneyScale      = 2;   // presentation / persistence of final amounts
public const int InternalScale   = 6;   // intermediate money
public const int PercentScale    = 6;
public const int GramsScale      = 3;
public const int KwhScale        = 4;
public const int QuantityScale   = 4;
public const MidpointRounding Mode = MidpointRounding.AwayFromZero;
```

### CR-00.2 — Rounding mode

**`MidpointRounding.AwayFromZero`** everywhere. Half-up matches Brazilian commercial
expectation and matches what an operator computes by hand. Banker's rounding is explicitly
rejected: it is statistically nicer and commercially surprising, and "surprising" is the
failure mode this product exists to eliminate.

### CR-00.3 — Where rounding happens

Rounding is applied **only at the points listed below**, never opportunistically:

1. After each **individual cost component** → `InternalScale` (6).
2. After each **subtotal** inside the cost breakdown → `InternalScale` (6).
3. `UnitTotalCost` → `InternalScale` (6). *Not* rounded to 2 — cost is an internal quantity.
4. `SuggestedUnitPrice` → `MoneyScale` (2), then the configured price rounding policy.
5. `DiscountAmount`, `NetUnitPrice`, `LineTotalAmount`, `CommissionAmount`, `ExpectedProfit`
   → `MoneyScale` (2).
6. `EffectiveMarginPercent` → `PercentScale` (6), computed **from rounded money values** so
   the displayed margin matches the displayed numbers.

Consequence to respect: **the frontend and the PDF never recompute anything.** They render
values produced by the engine. Any client-side arithmetic on money is a defect
(ARCHITECTURE §10). This is the mechanism that guarantees calculation = PDF = report =
database = screen.

### CR-00.4 — Engine version

`CostEngine.Version` is `"1.0.0"` at S4 and is returned on every transient Laboratory result.
S4 has no experiment/snapshot persistence. When a future sprint persists a cost snapshot, it
must copy this version and must never recompute old snapshots.

---

## 1–6. Legacy S3/S5–S10 specification (preserved; not implemented by S4)

The following pre-S4 rule IDs and meanings are retained verbatim in intent. S4 does not implement
or redefine them; its transient laboratory rules begin at CR-13.

### CR-01.1 — Single filament component
`componentCost = round6(gramsUsed / 1000 × pricePerKg)`. Canonical G1 is 72 g at R$ 89,90/kg =
R$ 6,472800.

### CR-01.2 — Multiple components
`filamentCost = round6(Σ componentCost[i])`; there is no component limit. Canonical G2 is
72 g at R$ 89,90/kg plus 18 g at R$ 94,50/kg = R$ 8,173800.

### CR-01.3 — Price resolution
`pricePerKg` is resolved at calculation instant and frozen in the later snapshot; it is not reread
for an existing quote.

### CR-01.4 — Explainability
Each legacy component renders `{filamentName} · {grams} g × R$ {pricePerKg}/kg = R$ {componentCost}`.

### CR-02.1 — Supply component cost
`supplyComponentCost = round6(quantity × unitCostAtInstant)`; e.g. 4 units at R$ 0,35 = R$ 1,400000.

### CR-02.2 — Supply grouping
`packagingCost = round6(Σ PACKAGING)` and `suppliesCost = round6(Σ non-PACKAGING)`; both enter
direct cost identically.

### CR-02.3 — Unit consistency
The recipe/experiment input unit must equal the supply unit; mismatch is rejected, never converted.

### CR-03.1 — Manual cost lines
`manualCost = round6(Σ line.Amount)`; manual lines are money and are not multiplied by quantity.

### CR-04.1 — Machine hourly rate (future S9)
`machineHourlyRate = hourlyRateOverride ?? round6((acquisitionCost / expectedLifetimeHours ?? 0) + (maintenanceCostPerHour ?? 0))`.
Missing components contribute zero. This remains a future specification; S4's manual scenario
machine rate is not this derivation.

### CR-04.2 — Machine cost per unit (future S9)
`printHours = printDurationSeconds / 3600` (unrounded) and
`machineCost = round6(printHours × machineHourlyRate)`.

### CR-05.1 — Estimated kWh (future S10)
`effectivePowerWatts = machine.AveragePowerWatts ?? machine.NominalPowerWatts` and
`estimatedKwh = round4(effectivePowerWatts / 1000 × printHours × (1 + energyOverheadFactor))`.

### CR-05.2 — kWh precedence (future S10)
`EnergyConsumptionSession` (ACTUAL/SMART_PLUG/MANUAL), then explicit recipe estimate, then
CR-05.1 machine-power estimate. The selected source is recorded in the future breakdown.

### CR-05.3 — Energy cost (future S10)
`energyCost = round6(kwh × tariffPricePerKwh)`, with the tariff version valid at calculation time.

### CR-06.1 — Labor
`laborCost = round6(laborMinutes / 60 × laborHourlyRate)`.

### CR-06.2 — Direct cost
`directCost = round6(filamentCost + suppliesCost + packagingCost + manualCost + energyCost + machineCost + laborCost)`.

### CR-06.3 — Wastage
`wastageBase = filamentCost + suppliesCost + packagingCost` and
`wastageCost = round6(wastageBase × wastageRate)`; labor/machine/energy are excluded.

### CR-06.4 — Unit and batch cost
`unitTotalCost = round6(directCost + wastageCost)` and
`batchTotalCost = round6(unitTotalCost × quantity)`; legacy costs are calculated per unit first.

### CR-06.5 — Legacy laboratory worked example
84 g PLA at R$ 89,90/kg + 22 g at R$ 94,50/kg + manual R$ 0,40 + 4 supplies at R$ 0,18 +
0,21 kWh at R$ 0,92 + 3 h 25 min at R$ 1,20/h produces `directCost = unitTotalCost = R$ 15,043800`
at 0% wastage. This is G3, the defined base for G4/G5/G8/G9.

## 13. S4 Cost Laboratory (implemented)

### CR-13.1 — Generic Supply lines

Every material line references one `Supply`; filament and packaging are not separate costing
types. Duplicate Supply lines are legal and remain distinct in input and output.

### CR-13.2 — Entered and base quantities

Entered quantity is positive and represented with at most eight decimal places. The API calls
S3's closed `SupplyUnitConversion.NormalizePositive`; normalized base quantity uses four decimal
places. Incompatible units and positive quantities that normalize to zero are rejected.

### CR-13.3 — Weighted average acquisition

Only positive, cost-bearing `PurchaseReceipt` movements are eligible:

```text
weightedUnitCost = round6(
    Σ(quantityDeltaBaseUnit × unitCostSnapshot)
    / Σ(quantityDeltaBaseUnit))
```

Example: 1,000 g costing R$ 100 plus 500 g costing R$ 75 gives
`round6(175 / 1500) = R$ 0.116667/g`. Manual adjustments do not affect the basis.

### CR-13.4 — Missing basis and manual simulation

Without an eligible receipt, the line fails with `COST_BASIS_UNAVAILABLE`. An optional manual
cost per base unit takes precedence and is returned as `MANUAL_OVERRIDE`; it never updates the
Supply or ledger. Otherwise the source/policy is `WEIGHTED_AVERAGE_ACQUISITION`.

### CR-13.5 — Wastage precedence and bounds

Wastage is percentage points in the inclusive range 0–100. Precedence is:

```text
line override > scenario default > costing.default_wastage_rate
```

### CR-13.6 — Effective quantity and material costs

```text
effectiveQuantity = round4(normalizedRequired × (1 + wastagePercent / 100))
costBeforeWastage = round6(normalizedRequired × unitCostBase)
costAfterWastage  = round6(effectiveQuantity × unitCostBase)
wastageCost       = round6(costAfterWastage - costBeforeWastage)
```

Example: 100 g at R$ 0.10/g with 5% waste gives 105 g, R$ 10 before waste,
R$ 0.50 waste and R$ 10.50 after waste.

### CR-13.7 — Labor

`laborCost = round6(laborMinutes / 60 × laborHourlyRate)`. A manual hourly rate wins over
`costing.default_labor_hourly_rate`; the result identifies `MANUAL_OVERRIDE` or
`DEFAULT_SETTING`. Example: 30 minutes at R$ 40/hour = R$ 20.

### CR-13.8 — Machine

`machineCost = round6(machineMinutes / 60 × machineHourlyRate)`. Both values are manual S4
scenario inputs. The hourly rate may encompass the operator's chosen operational allowance;
S4 has no Printer, depreciation or energy formula. Example: 120 minutes at R$ 3/hour = R$ 6.

### CR-13.9 — Additional direct costs

`additionalDirectCosts = round6(Σ amount)`. Each non-negative amount has a required description.

### CR-13.10 — Totals and output quantity

```text
materialsTotal = round6(Σ material.costAfterWastage)
totalEstimated = round6(materialsTotal + laborCost + machineCost + additionalDirectCosts)
estimatedUnit  = round6(totalEstimated / outputQuantity)
```

`outputQuantity` is an integer from 1 through 1,000,000. Example: a R$ 125 batch with output 10
has estimated unit cost R$ 12.500000. At least one material, positive-time labor/machine or
positive additional cost is required; otherwise `COST_CALCULATION_EMPTY` is returned.

### CR-13.11 — Stock is advisory

If effective material quantity exceeds current stock, the line contains
`REQUESTED_QUANTITY_EXCEEDS_CURRENT_STOCK`; calculation succeeds. A calculation never changes
Supply stock/version, creates an InventoryMovement, writes a setting or emits an audit row.

### CR-13.12 — Backend is authoritative

Every line returns Supply identity, entered and normalized quantities/units, wastage/effective
quantity, cost source/policy/rate, before/waste/after costs and stock warning. Totals are returned
by the backend. The frontend formats those values as BRL with two decimal places and performs no
authoritative monetary calculation.

---

## 7. Pricing

There is **one formula**. Direct sale is the case where commission = 0 and fixed fee = 0
(DOMAIN-MODEL §7). No branch, no special case.

### CR-07.1 — Validity guard (executed before any division)
```
denominator = 1 - (commissionPercent + desiredMargin)

if commissionPercent < 0 or commissionPercent >= 1  → error PRICING_INVALID_COMMISSION
if desiredMargin     < 0 or desiredMargin     >= 1  → error PRICING_INVALID_MARGIN
if denominator <= 0                                 → error PRICING_INVALID_DENOMINATOR
if denominator <  marginWarningDenominator          → warning PRICING_EXTREME_MARGIN (default 0.10)
```
`PRICING_INVALID_DENOMINATOR` is a hard failure. The engine returns a failed `Result`; it never
returns a number. The API responds 422 with the offending commission and margin so the UI can
say, in pt-BR, "comissão 18% + margem 85% = 103%: matematicamente impossível".

The warning does not block: a denominator of 0.05 (a 20× markup) is legal but almost always a
typo, so the UI must surface it.

### CR-07.2 — Suggested price
```
rawPrice       = (unitTotalCost + fixedFeePerUnit) / denominator
suggestedPrice = applyRoundingPolicy( rawPrice )
```

> **S5 clarification ([ADR-0019 §4](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md)).**
> The rounding policy is applied to the raw, unrounded `rawPrice` — not to a value already
> rounded to two decimals — matching CR-07.4's own worked table (`NONE` on `40,5187234…` yields
> `40,51`, the truncated raw value, never `40,52`). `Verce.Modules.Pricing.PricingEngine` is the
> implementation of record; if this prose and the worked table in CR-07.4 ever disagree again,
> the worked table wins.

**Direct sale** (commission = 0, fixedFee = 0) collapses to the brief's formula:
```
salePrice = unitTotalCost / (1 - desiredMargin)
```

**Marketplace** is the brief's formula verbatim:
```
salePrice = (unitTotalCost + fixedFee) / (1 - (commissionPercent + desiredMargin))
```

Worked example:
```
unitTotalCost   = 15,0438
fixedFee        = 4,00
commission      = 0,18
desiredMargin   = 0,35
denominator     = 1 - 0,53 = 0,47
rawPrice        = (15,0438 + 4,00) / 0,47 = 40,5187234…
suggestedPrice  = R$ 40,52
```

### CR-07.3 — Fixed fee application
`FeeRuleVersion.FixedFeeApplication ∈ {PER_UNIT, PER_ORDER}`, default `PER_UNIT`.

- `PER_UNIT` → `fixedFeePerUnit = fixedFee`.
- `PER_ORDER` → `fixedFeePerUnit = round6(allocatedOrderFee / quantity)`, where
  `allocatedOrderFee` is **this line's share** of the one order-level fee, allocated across the
  lines by [CR-07.7](#cr-077--per_order-fee-allocation-across-lines). The item snapshot records
  both the raw fee and the allocation, so a quantity change is visibly a different allocation
  rather than a mysterious price move.

> **Corrected for the multi-line case (H-004 remainder,
> [ADR-0020 §C](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)).** This
> rule previously divided the **whole** fee by each line's own quantity. That is correct for a
> single-line quote — all S5 could produce — but charges the order fee once per *line* as soon as
> there are two, which contradicts `PER_ORDER`'s own definition ("charged once per order").
> A single-line quote is unaffected: its allocated share **is** the whole fee.

### CR-07.4 — Price rounding policy (setting `pricing.price_rounding_policy`)

| Policy | Effect on 40,5187 |
|---|---|
| `CENT` (default) | `40,52` |
| `TEN_CENTS` | `40,60` (ceiling to 0,10) |
| `WHOLE` | `41,00` (ceiling to 1,00) |
| `NINETY_NINE` | `40,99` (ceiling to the next `,99`) |
| `NONE` | `40,5187` truncated to 2 → `40,51` |

All policies except `NONE` and `CENT` round **up**, never down: rounding a price down silently
erodes the margin the operator asked for.

> **NINETY_NINE precisely (Terra B-01 correction, [ADR-0019 §5.1](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md#51-ninety_nine-is-a-true-ceiling-not-floor099)).**
> `NINETY_NINE` means **the smallest commercial price of the form `N,99` that is greater than or
> equal to the raw price** — a true ceiling. It does **not** mean "replace the decimal digits with
> `,99`": for a raw price of `40,995`, replacing the digits gives `40,99`, which is BELOW the raw
> price and therefore wrong; the correct result is `41,99`. Worked boundary values:
> `40,98 → 40,99`, `40,99 → 40,99` (exact `,99` stays put), `40,991 → 41,99`,
> `40,995 → 41,99`, `41,00 → 41,99`, `41,99 → 41,99`, `41,991 → 42,99`. For every valid raw price,
> `suggestedPrice ≥ rawPrice` holds under this policy, same as every other rounding-up policy.

### CR-07.5 — Bracket resolution (fee depends on price, price depends on fee)

When a `FeeRuleVersion` has price brackets, the fee cannot be known before the price and the
price cannot be computed before the fee. Resolution is a **consistency search**, not iteration
to a fixed point:

```
1. Sort brackets ascending by MinPrice.
2. For each bracket b: compute price_b using b.CommissionPercent and b.FixedFee (CR-07.2).
3. A bracket is CONSISTENT when  b.MinPrice <= price_b < (b.MaxPrice ?? +∞).
4. If exactly one bracket is consistent          → use it.
5. If several are consistent                     → use the one yielding the LOWEST price
                                                    (deterministic, customer-favourable).
6. If none is consistent (a fee discontinuity)   → for each boundary MinPrice_b, evaluate the
                                                    achieved margin at price = MinPrice_b with
                                                    bracket b's fees; take the lowest boundary
                                                    whose achieved margin >= desiredMargin and
                                                    pin the price to it.
7. If step 6 finds nothing                       → error PRICING_BRACKET_DISCONTINUITY.
                                                    The operator must set the price manually.
```
Step 6 and 7 exist so the system can never return a price that silently misses the requested
margin. Brackets are not used before S8; the algorithm is specified now so the fee model does
not have to be redesigned later.

### CR-07.6 — Minimum and maximum fee
```
commissionAmount = round2( finalUnitPrice * commissionPercent )
if minimumFee is set: commissionAmount = max(commissionAmount, minimumFee)
if maximumFee is set: commissionAmount = min(commissionAmount, maximumFee)
```
Clamping is applied **after** the price is computed. When a clamp is active, the effective
margin (CR-08.4) will differ from the desired margin; that difference must be surfaced in the
breakdown as `feeClampApplied: MIN|MAX`, never hidden.

### CR-07.7 — `PER_ORDER` fee allocation across lines

*(H-004 remainder; decided in [ADR-0020 §C](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md).
Not exercised before S6, which is the first sprint with quote lines.)*

A `PER_ORDER` fixed fee is charged **exactly once per quote revision** — never once per line,
never once per unit. It is *our* marketplace cost, recovered through the quoted prices exactly as
commission already is; it is never a separate customer-facing charge, so revision totals
(CR-08.6) remain pure sums of line values.

**Scope.** Allocation runs over the lines of the revision resolving to the same applicable
`FeeRuleVersion` (the "fee group"). In S6 that is all lines, since mixed-channel revisions are
H-003's decision (S8). A `FeeRuleVersion` carries exactly one fixed fee, so there is exactly one
order-level fee per group.

**Basis — the line's estimated cost**, known before pricing and therefore non-circular:

```
basis_i      = round6( unitTotalCost_i × quantity_i )
totalBasis   = Σ basis_i                                   (over the fee group)
exactShare_i = orderFee × basis_i / totalBasis             (full decimal precision, unrounded)
```

Cost proportion raises every line's **allocation-driven pricing basis** by the *same percentage*
(`basis_i · (1 + orderFee/totalBasis)`), so no line's cost signal is distorted relative to
another's — this is exact regardless of margin, rounding or overrides, because it happens before
any of those apply. It is **not** a claim that every line's *final* price moves by that same
percentage: `price_i = (basis_i · (1 + orderFee/totalBasis)) / (1 − commission − margin_i)` only
collapses to one shared multiplier when `margin_i` is also uniform across the group — and
`DesiredMarginPercent` is set per line, so it need not be. See
[ADR-0020 §C.3](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md#c3-allocation-basis-line-estimated-cost)
for the exact statement. Quantity or equal-per-line bases would inflate cheap lines; a price-based
basis would be circular (the fee would depend on the price that depends on the fee).

**Algorithm — floor plus largest remainder**, because allocation *partitions* an exact amount and
the parts must sum to the whole:

```
1. alloc_i   = floor2( exactShare_i )                      (truncate toward zero, 2 decimals)
2. residual  = round( (orderFee − Σ alloc_i) × 100 )       (integer, 0 ≤ residual < lineCount)
3. rank by remainder_i = exactShare_i − alloc_i DESCENDING,
   tie-break quote_item.line_number ASCENDING
4. add R$ 0,01 to each of the first `residual` lines in that ranking
```

`line_number` is unique per revision and stable; **database row order is never used**, and
`sort_order` (which the operator can change) is never used.

**Invariant CR-07.7a (must be a test): `Σ alloc_i = orderFee` exactly, always.**

| Degenerate input | Behaviour |
|---|---|
| `orderFee = 0` | every `alloc_i = 0`; `residual = 0` |
| `orderFee < 0` | cannot occur — `FeeRuleVersion` validates `fixedFee ≥ 0` (ADR-0005) |
| `totalBasis = 0` (every line zero-cost) | **equal split per line** — the same algorithm with `basis_i = 1`; no division by zero, no rejection |
| one line with `basis_i = 0`, `totalBasis > 0` | that line gets `alloc_i = 0`; nothing special |
| empty fee group | nothing to allocate; an empty quote is already refused (`QUOTE_HAS_NO_ITEMS`) |

**Recalculation is total, and it happens while constructing the next revision — never on a
persisted one.** There is no mutable "draft revision" in this domain
([ADR-0020 §A.7](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md#a7-revision-construction-is-clone-then-recalculate-never-an-in-place-edit-blocking-02)):
a commercial change (quantity, line added, line removed, cost changed) is applied to an in-memory
revision candidate cloned from the current persisted revision, which recomputes the whole fee
group's allocation from the candidate's current line set **before** that candidate is persisted as
the next revision. Every line sharing the fee group is affected by construction — not only the
line the operator touched — because the group's `totalBasis` changed
([ADR-0020 §C.6](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md#c6-per_order-dependency-closure-and-full-recomputation)
names this the dependency closure). Allocations are never patched incrementally, which would
accumulate residual-cent drift and break CR-07.7a. Once a revision is persisted and reaches a
terminal status it is immutable, so its allocations are frozen history; a further commercial
change constructs yet another new revision, which recomputes its own allocation from scratch.

**Rounding.** Partitioning is not rounding: no value here is rounded to a scale, each part is
*selected* so the parts total the original. This is therefore not an exception to
[ADR-0002 §4](architecture/ADR-0002-money-precision-and-rounding.md)'s half-away-from-zero rule,
which continues to govern every ordinary rounding in the pricing of an allocated line.

Worked vectors (S6's canonical corpus) are in
[ADR-0020 §C.8](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md#c8-canonical-allocation-vectors):
single line · two equal lines · three equal lines (residual cent) · unequal basis · quantity
change · line removal · zero basis.

---

## 8. Discount, line totals and profit

### CR-08.1 — Final unit price
```
unitPrice = manualPriceOverride ?? suggestedPrice
```
An override is always allowed and always recorded (`PriceOverridden = true` in the snapshot),
so "why is this cheaper than suggested?" is answerable.

### CR-08.2 — Discount
```
DiscountKind = NONE     → discountAmount = 0
DiscountKind = PERCENT  → discountAmount = round2( unitPrice * discountValue )
DiscountKind = AMOUNT   → discountAmount = round2( discountValue )

netUnitPrice = unitPrice - discountAmount        (must be > 0, else DISCOUNT_EXCEEDS_PRICE)
```
Discount is per item. A global quote-level discount is **deliberately not implemented in v1**:
it would have to be allocated back to items to keep per-item margin correct, and that
allocation is a second, avoidable rounding source. If it is added later it must be modeled as
an allocation across items, not as a subtraction from the total.

### CR-08.3 — Line totals
```
lineTotalAmount = round2( netUnitPrice * quantity )
lineCostAmount  = round2( unitTotalCost * quantity )
lineFeeAmount   = round2( commissionAmount * quantity )
                + ( fixedFeeApplication = PER_UNIT ? round2(fixedFee * quantity)
                                                   : allocatedOrderFee )        -- CR-07.7
```

The `PER_ORDER` branch uses **this line's allocated share**, not the whole fee. Adding
`round2(fixedFee)` to every line — as this rule previously did — charges the order fee once per
line, so a three-line quote with a R$ 10,00 order fee would report R$ 30,00 of fee. Because
CR-07.7 guarantees `Σ allocatedOrderFee = orderFee` exactly, `Σ lineFeeAmount` now contains the
order fee exactly once ([ADR-0020 §C.1](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)).

### CR-08.4 — Profit and effective margin
```
expectedProfit  = round2( lineTotalAmount - lineFeeAmount - lineCostAmount )
effectiveMargin = lineTotalAmount > 0
                  ? round6( expectedProfit / lineTotalAmount )
                  : 0
```

**Invariant CR-08.5 (must be a test):** with `roundingPolicy = NONE`, no discount, no override,
no fee clamp, `effectiveMargin` equals `desiredMargin` within 0,0001. Any larger deviation
means the cost or fee model leaked something the pricing formula did not account for.

### CR-08.6 — Revision totals
```
subtotalAmount = round2( Σ round2(unitPrice * quantity) )
discountAmount = round2( Σ discountAmount * quantity )
totalAmount    = round2( Σ lineTotalAmount )
totalCostAmount= round2( Σ lineCostAmount )
expectedProfit = round2( Σ expectedProfit )
effectiveMargin= totalAmount > 0 ? round6( expectedProfit / totalAmount ) : 0
```
Totals are sums of already-rounded line values — never a re-computation from unrounded inputs.
This is what keeps the PDF footer equal to the sum of the printed lines.

---

## 9. Estimated versus actual

### CR-09.1 — Actual material cost
```
actualMaterialCost = round6( Σ actualQuantity[i] * unitCostAtConsumption[i] )
```
`unitCostAtConsumption` is the material price at the moment of consumption, not at quote time.

### CR-09.2 — Actual energy cost
```
actualEnergyCost = round6( Σ session.Kwh * tariffPriceAtSession )
```

### CR-09.3 — Actual total
```
actualTotalCost = round6( actualMaterialCost + actualEnergyCost
                        + machineCost(actual hours) + laborCost(actual minutes)
                        + manualCost )
```
Manual lines have no actual counterpart and carry over from the estimate.

### CR-09.4 — Variance (always derived, never stored)
```
absoluteVariance = actual - estimated
relativeVariance = estimated != 0 ? round6( absoluteVariance / estimated ) : null
```
Reported per dimension: grams per filament, kWh, hours, money per cost group, and total.

Example from the brief: estimated 90 g, actual 96 g → `+6 g`, `+6,67%`.

### CR-09.5 — Realized margin
```
realizedProfit = round2( saleNetRevenue - actualTotalCost )
realizedMargin = saleNetRevenue > 0 ? round6( realizedProfit / saleNetRevenue ) : 0
marginVariance = realizedMargin - effectiveMarginAtQuote
```

---

## 10. Purchase cost derivation and S4 acquisition policy

> **Supersedes the original per-lot design below.** S3 delivered `Supply`/`InventoryMovement`
> instead of a `Filament`/`FilamentLot` pair — see
> [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) §5–6. There is no
> `FilamentPriceHistory` and no configurable price policy yet; what S3 actually computes is:

### CR-10.1 — Purchase receipt unit cost (implemented)
```
only UnitCost: totalCost = roundMoney(UnitCost × enteredQuantity)
only TotalCost: unitCostSnapshot(baseUnit) = roundInternal(TotalCost / normalizedBaseQuantity)
both supplied: roundMoney(UnitCost × enteredQuantity) must equal roundMoney(TotalCost)
```
Example: 1 kg purchased for R$ 89,90 against a gram-denominated supply → quantity converts to
1000 g, `unitCostSnapshot = 89.90 / 1000 = R$ 0,089900/g`. Both supplied forms are rejected as
`PURCHASE_COST_MISMATCH` if their canonical money values disagree. Quantity is first normalized
to `numeric(14,4)` in the supply base unit and must remain positive; an unrepresentable positive
input is rejected as `QUANTITY_BELOW_BASE_PRECISION` before any cost division. `Supply.LatestPurchaseUnitCost` caches
the most recent `unitCostSnapshot`; it is **not** a weighted average and is never recomputed from
older movements.

### CR-10.2 — S4 cost policy
```
WEIGHTED_AVERAGE_ACQUISITION → CR-13.3, estimated from immutable purchase receipts
MANUAL_OVERRIDE              → per-line what-if value, no persistence or ledger mutation
```
This does not claim moving-average remaining-stock valuation, FIFO/LIFO, accounting cost or
actual consumption. See ADR-0018.

---

## 11. Stock *(implemented S3)*

### CR-11.1 — Current stock (implemented)
```
currentStockBaseUnit(supply) = Σ inventory_movement.quantity_delta_base_unit
                                where supply_id = supply.id
```
`quantity_delta_base_unit` is signed at the point each movement is posted (positive for
`InitialBalance`/`PurchaseReceipt`/`ManualIncrease`, negative for `ManualDecrease`, derived for
`Correction` — see CR-11.2), so this is a plain sum, never a `CASE` on a separate direction column.
`Supply.CurrentStockBaseUnit` caches this sum, written only alongside the movement insert that
produced it (same transaction) — never independently.

**Non-negative invariant (implemented):** a movement whose delta would drive
`currentStockBaseUnit` below zero is rejected (`INSUFFICIENT_STOCK`), including under two
concurrent requests against the same supply — enforced via the aggregate's own
optimistic-concurrency `Version`, not a new locking primitive
([ADR-0017 §3](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md)).

### CR-11.2 — Count-based correction (implemented)
```
signedDelta = countedQuantity(baseUnit) - currentStockBaseUnit(supply)
```
Posted as one `Correction` movement carrying `signedDelta`. A `signedDelta` of exactly zero is
rejected (`CORRECTION_QUANTITY_UNCHANGED`) — a correction that changes nothing is not a valid
movement. History is never rewritten; a later miscount is fixed by another `Correction`, not by
editing this one.

---

## 12. Reporting formulas

### CR-12.1 — Conversion rate (formal definition)

*(Corrected for H-009 A — [ADR-0020 §B.2](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md),
same-period cardinality frozen 2026-09-20. This rule previously said "the outcome of a quote is the
outcome of its **last non-superseded revision**", which is precisely the retroactive,
current-revision-keyed definition H-009 A was raised against: it silently removed a won quote from
a closed period the moment anyone revised it. The normative definition now lives in
[DATA-DICTIONARY §4.1](DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão); this rule restates
it so the two can never drift.)*

A quote that is still open is **not** a failure. Conversion is measured over **decided** quotes,
where a decision is read from the append-only status history, not from the current revision.

```
periodOutcome(quote, period) =                  -- at most ONE outcome per quote per period
    WON    if firstApprovalAt(quote) falls in the period
    LOST   else if ≥ 1 eligible pre-win CANCELED/EXPIRED transition falls in the period
    —      otherwise
                                                -- precedence: WON > LOST > no contribution

won(period)      = COUNT(DISTINCT QuoteId) with periodOutcome = WON
lost(period)     = COUNT(DISTINCT QuoteId) with periodOutcome = LOST
decided(period)  = won(period) + lost(period)

conversionRate(period) = decided(period) > 0
                       ? round6( won(period) / decided(period) )
                       : null
```

Counting rules:
- The unit is one **quote-period decision**. A quote with five revisions still contributes at most
  one outcome to a period, and enters `won()` at most once **ever** (keyed on `firstApprovalAt`).
- A quote's outcome is **never** read from its current revision's status. A win is dated at the
  first `APPROVED` transition and is permanent; a loss is an eligible terminal transition that
  occurred while the quote had never been approved.
- Expire-then-cancel inside one period is **one** loss, not two; expire-then-approve inside one
  period is **one** win and **zero** losses.
- `SUPERSEDED` is never an outcome — it is an internal transition.
- Quotes still in `GENERATED`, `SENT` or `NEGOTIATING` are excluded from both numerator and
  denominator; they appear separately as "em aberto".
- `null` (no decided quotes) is rendered as "—", never as 0%.

A secondary **cohort conversion** is available for trend analysis:
`cohortConversion(month) = approved quotes created in month / all quotes created in month`,
which under-reports recent months by construction and must be labeled as such in the UI.

### CR-12.2 — Dashboard aggregates
```
salesInMonth   = Σ Sale.NetAmount        where SoldAt in month and Status = CONFIRMED
profitInMonth  = Σ Sale.GrossProfitAmount where …
costsInMonth   = Σ Expense.Amount        where IncurredOn in month
                                          and AccountingTreatment = OPERATING_EXPENSE
openQuotes     = count(Quote) whose current revision Status ∈ {GENERATED, SENT, NEGOTIATING}
ordersInProduction = count(ProductionOrder) where Status ∈ {QUEUED, IN_PRODUCTION}
```
`costsInMonth` excludes `INVENTORY_PURCHASE` and `ASSET_ACQUISITION`
([ADR-0013](architecture/ADR-0013-expense-inventory-double-counting.md)).

### CR-12.3 — Product profitability
```
productProfit(product, period) = Σ SaleItem.GrossProfitAmount
productMargin(product, period) = Σ GrossProfitAmount / Σ LineTotalAmount
```
Ranked by profit, not by margin: a 90% margin on R$ 3,00 is not a better product than a 30%
margin on R$ 300,00. The UI shows both columns and sorts by profit by default.

---

## 13. Test corpus (golden cases, must exist from S4)

| # | Case | Expected |
|---|---|---|
| G1 | 72 g PLA @ 89,90 | 6,472800 |
| G2 | 72 g @ 89,90 + 18 g @ 94,50 | 8,173800 |
| G3 | Laboratory example CR-06.5 | unitTotalCost 15,043800 |
| G4 | G3, direct sale, margin 35% | 23,14 |
| G5 | G3, commission 18%, fixed 4,00, margin 35% | 40,52 |
| G6 | commission 0,60 + margin 0,45 | `PRICING_INVALID_DENOMINATOR` |
| G7 | commission 0,18 + margin 0,80 | warning `PRICING_EXTREME_MARGIN`, price returned |
| G8 | G5 with quantity 7 | lineTotal = round2(40,52 × 7) = 283,64 |
| G9 | G5 with 10% item discount | net 36,47, margin recomputed and lower |
| G10 | Estimated 90 g vs actual 96 g | +6 g, +0,066667 |
| G11 | 3 decided quotes, 1 approved | 0,333333 |
| G12 | 0 decided quotes | `null` |
| G13 | Weighted acquisition: 100 g @ 0,116667/base + 5% waste | material total 12,250035 |
| G14 | 100 g @ 0,10/base + 5% S4 percentage-point waste | material total 10,500000 |
| G15 | 30 S4 labor minutes @ 0 manual/hour | `MANUAL_OVERRIDE`, 0,000000 |
| G16 | 120 S4 machine minutes @ 3/hour | machine 6,000000 |
| G17 | S4 batch total 39,95, output 2 | estimated unit 19,975000 |
| G18 | S4 two-material labor/machine/additional scenario | total estimated 39,950000 |
| G19 | S4 manual material cost override | source `MANUAL_OVERRIDE` |

G4 check: `15,0438 / (1 - 0,35) = 23,1443…` → `23,14`.

## 14. S4 rule coverage matrix

| Rule | Named test |
|---|---|
| CR-13.1 / CR-13.2 | `CR_13_1_G13_weighted_acquisition_input_is_costed_without_changing_its_basis` |
| CR-13.3 / CR-13.4 | `CR_13_7_G19_manual_material_override_is_explicit` |
| CR-13.5 / CR-13.6 | `CR_13_2_G14_material_wastage_uses_percentage_points`; `CR_13_2_explicit_zero_wastage_does_not_fall_back_to_default` |
| CR-13.7 | `CR_13_3_G15_labor_uses_manual_zero_rate_as_a_real_override` |
| CR-13.8 | `CR_13_4_G16_machine_cost_uses_decimal_minutes_over_sixty` |
| CR-13.9 | `CR_13_8_negative_additional_amount_is_rejected` |
| CR-13.10 | `CR_13_5_G17_batch_division_retains_six_decimal_places`; `CR_13_6_G18_multi_material_full_costing_is_reconciled` |
| CR-13.11 / CR-13.12 | `Effective_quantity_above_stock_warns_but_still_calculates` |
