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
| Percent (commission, margin, wastage) | `decimal` | `numeric(9,6)` | 6, stored as **fraction** |
| Weight in grams | `decimal` | `numeric(12,3)` | 3 |
| Weight in kilograms | derived, never stored | — | — |
| Energy | `decimal` | `numeric(12,4)` | 4 (kWh) |
| Power | `int` | `integer` | watts |
| Quantity | `decimal` | `numeric(14,4)` | 4 |
| Duration | `int` | `integer` | seconds |
| Labor time | `int` | `integer` | minutes |

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

`CalculationEngineVersion` is a constant (`"1.0.0"` at S4) persisted on every snapshot and
every experiment result. It changes whenever a rule in this document changes semantics.
Old snapshots are **never recomputed**; they are read as-is and rendered with a note when the
engine version differs from current.

---

## 1. Filament cost

### CR-01.1 — Single filament component
```
componentCost = round6( gramsUsed / 1000 * pricePerKg )
```

Worked example (the canonical case):
```
PLA Preto Eclipse · 72 g · R$ 89,90/kg
72 / 1000 = 0,072
0,072 × 89,90 = 6,47280
componentCost = R$ 6,472800
```

### CR-01.2 — Multiple components
```
filamentCost = round6( Σ componentCost[i] )
```
There is **no limit** on the number of filament components.

Example:
```
PLA Preto   72 g @ 89,90/kg → 6,472800
PLA Laranja 18 g @ 94,50/kg → 1,701000
filamentCost = 8,173800
```

### CR-01.3 — Price resolution
`pricePerKg` is `Filament.CurrentPricePerKg` **resolved at the calculation instant** and then
frozen into the snapshot. It is never re-read for an existing quote.

### CR-01.4 — Explainability
Each component must render as
`{filamentName} · {grams} g × R$ {pricePerKg}/kg = R$ {componentCost}`.

---

## 2. Supply cost

### CR-02.1
```
supplyComponentCost = round6( quantity * unitCostAtInstant )
```
Example: Argola 4 un × R$ 0,35 = R$ 1,400000.

### CR-02.2 — Grouping
```
packagingCost = round6( Σ cost where supply.category.kind = PACKAGING )
suppliesCost  = round6( Σ cost where supply.category.kind ≠ PACKAGING )
```
Packaging is separated for reporting only; both enter the direct cost identically.

### CR-02.3 — Unit consistency
`quantity.Unit` must equal `supply.Unit`. A mismatch is rejected at recipe/experiment
construction, not silently converted.

---

## 3. Manual cost lines

### CR-03.1
```
manualCost = round6( Σ line.Amount )
```
Manual lines are already money; they are not multiplied by anything. A manual line meant to
scale with quantity must be expressed as a supply.

---

## 4. Machine cost

### CR-04.1 — Hourly rate
```
machineHourlyRate =
    hourlyRateOverride
    ?? round6( (acquisitionCost / expectedLifetimeHours ?? 0) + (maintenanceCostPerHour ?? 0) )
```
Missing components contribute 0. A machine with no cost data yields rate 0, which is a legal
"I do not track machine cost yet" configuration.

### CR-04.2 — Machine cost per unit
```
printHours   = printDurationSeconds / 3600            (decimal, not rounded)
machineCost  = round6( printHours * machineHourlyRate )
```
Example: 3 h 25 min = 12 300 s → 3,416666… h × R$ 1,20/h = R$ 4,100000.

---

## 5. Energy cost

### CR-05.1 — Estimated kWh (when not measured and not overridden)
```
effectivePowerWatts = machine.AveragePowerWatts ?? machine.NominalPowerWatts
estimatedKwh = round4( effectivePowerWatts / 1000 * printHours * (1 + energyOverheadFactor) )
```
`energyOverheadFactor` (setting `energy.overhead_factor`, default 0) covers bed heating spikes,
enclosure, drying — it is a blunt correction, documented as such.

### CR-05.2 — kWh precedence
```
1. EnergyConsumptionSession for this production item   → source ACTUAL / SMART_PLUG / MANUAL
2. recipe.EstimatedEnergyKwh (explicit override)       → source ESTIMATED_OVERRIDE
3. CR-05.1 machine-power estimate                      → source ESTIMATED
```
The chosen source is always recorded in the breakdown.

### CR-05.3 — Cost
```
energyCost = round6( kwh * tariffPricePerKwh )
```
`tariffPricePerKwh` comes from the `EnergyTariffVersion` valid at the calculation instant, and
its `Id` is stored in the snapshot.

Example: 0,21 kWh × R$ 0,92/kWh = R$ 0,193200.

---

## 6. Total cost

### CR-06.1 — Labor
```
laborCost = round6( laborMinutes / 60 * laborHourlyRate )
```

### CR-06.2 — Direct cost
```
directCost = round6(
      filamentCost
    + suppliesCost
    + packagingCost
    + manualCost
    + energyCost
    + machineCost
    + laborCost )
```

### CR-06.3 — Wastage
Wastage models failed prints and material loss. Its base is **material only** — labor, machine
and energy of a failed print are real losses too, but attributing them here would double count
against the machine rate, and the operator reasons about wastage as "I lose filament".

```
wastageBase = filamentCost + suppliesCost + packagingCost
wastageCost = round6( wastageBase * wastageRate )
```

### CR-06.4 — Unit and batch cost
```
unitTotalCost  = round6( directCost + wastageCost )
batchTotalCost = round6( unitTotalCost * quantity )
```
`quantity` is the number of units. Costs are always computed **per unit** first; a per-batch
cost is never the primary figure, because pricing is per unit.

### CR-06.5 — Worked example (the Laboratory case from the brief)
```
PLA Preto   84 g @ R$ 89,90/kg  →  0,084 × 89,90        = 7,551600
PLA Laranja 22 g @ R$ 94,50/kg  →  0,022 × 94,50        = 2,079000
  filamentCost                                           = 9,630600
Cola (manual line)                                       = 0,400000
Parafuso 4 un @ R$ 0,18                                  = 0,720000
  suppliesCost                                           = 0,720000
  manualCost                                             = 0,400000
Energia 0,21 kWh @ R$ 0,92                               = 0,193200
Máquina 3 h 25 min @ R$ 1,20/h → 3,416667 × 1,20         = 4,100000
Mão de obra 0 min                                        = 0,000000
  directCost                                             = 15,043800
Perda 0%                                                 = 0,000000
  unitTotalCost                                          = R$ 15,043800
```

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
suggestedPrice = applyRoundingPolicy( round2( rawPrice ) )
```

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
- `PER_ORDER` → `fixedFeePerUnit = round6(fixedFee / quantity)`, and the item snapshot records
  both the raw fee and the allocation, so a quantity change is visibly a different allocation
  rather than a mysterious price move.

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
                + ( fixedFeeApplication = PER_UNIT ? round2(fixedFee * quantity) : round2(fixedFee) )
```

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

## 10. Filament price derivation

### CR-10.1 — Lot price
```
lotPricePerKg = round6( purchaseAmount / (purchasedWeightGrams / 1000) )
```
Example: R$ 89,90 for 1000 g → R$ 89,900000/kg.

### CR-10.2 — Current price policy (`inventory.filament_price_policy`)
```
LAST_PURCHASE (default) → currentPricePerKg = most recent lot's lotPricePerKg
MANUAL                  → currentPricePerKg is set by the operator; lots do not change it
WEIGHTED_AVERAGE        → reserved, not implemented in v1
```
Whatever the policy, a price change appends `FilamentPriceHistory` and never mutates any
existing snapshot.

---

## 11. Stock

### CR-11.1
```
estimatedStock(material) = Σ movements where Direction=IN  (+quantity)
                         + Σ movements where Direction=OUT (-quantity)
                         + Σ movements where Direction=ADJUSTMENT (±quantity)
```

### CR-11.2 — Count adjustment
```
adjustmentQuantity = countedQuantity - systemQuantityAtCount
```
emitted as one `ADJUSTMENT` movement. History is never rewritten.

---

## 12. Reporting formulas

### CR-12.1 — Conversion rate (formal definition)

A quote that is still open is **not** a failure. Conversion is measured over **decided** quotes.

```
decided(period)  = quotes whose terminal outcome was reached in the period,
                   outcome ∈ { APPROVED, CANCELED, EXPIRED }
approved(period) = decided quotes whose outcome was APPROVED

conversionRate(period) = decided(period) > 0
                       ? round6( approved(period) / decided(period) )
                       : null
```

Counting rules:
- The unit is the **Quote**, not the revision. A quote with five revisions counts once.
- The outcome of a quote is the outcome of its **last non-superseded revision**.
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

G4 check: `15,0438 / (1 - 0,35) = 23,1443…` → `23,14`.
