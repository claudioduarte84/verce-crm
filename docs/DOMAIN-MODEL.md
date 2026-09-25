# DOMAIN MODEL — Verce 3D | Laboratório de Custos

Conceptual model. Physical tables, keys and indexes are in [DATA-MODEL](DATA-MODEL.md);
formulas in [CALCULATION-RULES](CALCULATION-RULES.md); lifecycles in
[STATE-MACHINES](STATE-MACHINES.md).

Notation: **AR** = aggregate root · *E* = entity inside an aggregate · `VO` = value object.

---

## 1. Shared Kernel value objects

| VO | Shape | Invariants |
|---|---|---|
| `Money` | `decimal Amount`, currency implicit BRL (v1) | scale ≤ 6 internally; comparison and arithmetic only with same currency; no implicit conversion from `double` |
| `Percent` | `decimal Fraction` (0.175 = 17.5%) | `0 ≤ Fraction`; contextual upper bounds applied by callers |
| `Grams` | `decimal Value` | `≥ 0`, scale ≤ 3 |
| `Kwh` | `decimal Value` | `≥ 0`, scale ≤ 4 |
| `Quantity` | `decimal Value`, `SupplyUnit Unit` | `> 0` in BOM/lines; unit must match the supply's unit |
| `DurationSeconds` | `int Value` | `≥ 0` |
| `DateRange` | `DateOnly From`, `DateOnly? Until` | half-open `[From, Until)`; `Until > From` when present |
| `QuoteNumber` | `DateOnly Date`, `int Sequence`, `int RevisionIndex` | `Sequence ≥ 1`, `RevisionIndex ≥ 1`; renders `YYMMDD-N[suffix]` |
| `DocumentPath` | storage key + sha256 | immutable once written |

`Money`, `Percent`, `Grams`, `Kwh` all wrap `decimal`. **`double`/`float` are banned in the
domain assemblies** and the ban is enforced by an architecture test.

---

## 2. Customers module

### Customer (AR)
Fields: `Id`, `PersonType {INDIVIDUAL, COMPANY}`, `Name`, `TradeName?`, `Document?`
(CPF/CNPJ, optional, normalized digits only), `Email?`, `Phone?`, `Notes?`, `IsActive`,
`DeletedAt?`, audit columns. Persistence also assigns an internal `CreationSequence: long`; it is
not exposed as Customer identity or product data.

Children: `Addresses` (*E*, 0..N).

Invariants:
- `Name` required, trimmed, 2..200 chars.
- `Document`, when present, must be a structurally valid CPF (INDIVIDUAL) or CNPJ (COMPANY),
  and is **unique among non-deleted customers**. Absence is allowed and common.
- `CreationSequence` is required, unique, database-allocated once and immutable. Gaps are allowed;
  soft deletion never releases or recycles it. Customer lists use
  `Name, CreatedAt, CreationSequence` for total deterministic pagination.
- At most one address flagged `IsPrimary`; at most one flagged `IsDefaultShipping`.
- A customer referenced by any quote or sale cannot be hard-deleted, only deactivated.

### CustomerAddress (*E*)
`Id`, `CustomerId`, `Label` (e.g. "Casa", "Loja"), `ZipCode`, `Street`, `Number`,
`Complement?`, `District`, `City`, `State` (UF), `Country` (default `BR`), `IsPrimary`,
`IsDefaultShipping`, `Notes?`.

Deliberately **not** modeled in v1: fiscal regime, state registration, municipal registration,
suframa. The product is not a fiscal system (PRODUCT-VISION §5).

---

## 3. Inventory module

> **S3 supersedes the original plan below.** The S1-era design in this section modeled Filament
> as its own aggregate with per-lot stock and cost history. The S3 mission brief made a more
> specific, generalized design mandatory instead: quantities/units must work uniformly across
> *every* supply (resin in mL, packaging in `un`, cord in meters — not filament-in-grams only),
> and non-negative stock became a hard invariant rather than a warning. See
> [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) for the full
> reasoning and the rejected alternatives; what follows is the model as actually implemented.

### 3.1 Supply (AR)
`Id`, `Code` (unique, human-usable, normalized uppercase, **immutable after creation**), `Name`,
`Description?`, `CategoryCode` (→ `SupplyCategory`), `BaseUnit: SupplyBaseUnit`
(**immutable after creation**), `Active`, `MinimumStock?` (base unit), `PreferredSupplier?`,
`Notes?`, `FilamentDetails?` (optional value, see §3.3), `CurrentStockBaseUnit` (cached, see §3.5),
`LatestPurchaseUnitCost?` (informational snapshot — see §3.7, not a costing policy),
`HasRecordedMovement` (gates the one-time initial balance), `CreatedAt`/`UpdatedAt`, `Version`.

`SupplyBaseUnit` enum: `Gram`, `Kilogram`, `Unit`, `Milliliter`, `Liter`, `Meter`, `Centimeter`.

Invariants:
- `Code` matches `^[A-Za-z0-9._-]{2,40}$`, normalized uppercase at construction, unique.
- `MinimumStock`, when set, is `>= 0`.
- `LowStock = CurrentStockBaseUnit <= MinimumStock` (only meaningful once `MinimumStock` is set).
- Deactivating never deletes; an inactive supply is excluded from the default list (`status=active`
  when omitted), remains fully readable, can be reactivated, and still permits inventory managers
  to post receipts/reconciliation movements. Deactivation removes an item from the operational
  catalogue; it never freezes or erases physical inventory history.
- **Not** "PLA = one generic cost": every color/brand/diameter variant is its own `Supply` row
  with its own stock and cost — filament granularity comes from having many `Supply` rows in the
  `FILAMENT` category, not from a shared filament entity.

### 3.2 SupplyCategory (Category 3 reference data)
`Code` (PK — `FILAMENT`, `RESIN`, `PACKAGING`, `HARDWARE`, `ELECTRONICS`, `FINISHING`,
`CONSUMABLE`, `OTHER`), `Name`, `IsActive`. Classification only — no calculation logic and no
per-category behaviour; a future category is a seeded data row, never a deploy.

### 3.3 Filament details (optional value on Supply, not a separate aggregate)
`FilamentDetails { MaterialType, Brand, ColorName, ColorCode?, DiameterMm, SpoolNetWeightGrams }`,
present only when `Supply.CategoryCode = FILAMENT` in practice (structurally optional on any
supply). `FilamentMaterialType` enum: `Pla`, `PlaPlus`, `Petg`, `Abs`, `Asa`, `Tpu`, `Nylon`, `Pc`,
`Pva`, `Other`. Persisted as individual nullable scalar columns on `supply` — deliberately **not**
an EF owned type, so it never becomes its own entity in the ADR-0011 PK-category model (see
ADR-0017 §7).

### 3.4 Inventory ledger — InventoryMovement (*E* of Supply, append-only)
`Id`, `SupplyId`, `Type: InventoryMovementType`, `QuantityDeltaBaseUnit` (**signed**, base unit),
`OccurredAt`, `Reason?`, `Reference?`, `Supplier?`, `UnitCostSnapshot?`, `TotalCostSnapshot?`.

`InventoryMovementType`: `InitialBalance` (once, only before any other movement exists),
`PurchaseReceipt`, `ManualIncrease`, `ManualDecrease`, `Correction` (reconciles to a counted
absolute quantity by posting the derived signed delta) — all actively creatable in S3.
`Consumption`, `ReturnIn`, `ReturnOut` exist in the enum but are **not yet creatable by any
endpoint** — reserved for the Production and Sales modules (S8–S11).

Movements are **immutable**: there is no `Update`, `Delete` or `Remove` anywhere on
`InventoryMovement` (enforced by a reflection-based domain test) — a miscount is corrected by
posting a new `Correction` movement, never by editing a prior row.

### 3.5 Stock is a cached projection, and is non-negative by construction
`CurrentStockBaseUnit` is written only by the same aggregate operation that appends a movement, in
the same transaction — never independently. It is always equal to the signed sum of that supply's
`InventoryMovement` rows. A decrease that would take it below zero is rejected
(`INSUFFICIENT_STOCK`, HTTP 409) before any write is attempted, and two concurrent decreases
against the same supply cannot both succeed — see
[ADR-0017 §3](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md#3-non-negative-stock-via-the-aggregates-own-version--no-new-locking-primitive-closes-h-007-a)
for the mechanism (the aggregate's own optimistic-concurrency `Version`, per
[ADR-0011 §2](architecture/ADR-0011-identifiers-and-concurrency.md#2-concurrency-an-explicit-aggregate-version)
— no new locking primitive). This reverses the S1-era "warn, never block" stock policy for
inventory movements specifically.

### 3.6 Unit normalization
A movement may be entered in any unit **compatible** with the supply's own `BaseUnit`
(kg↔g, L↔mL, m↔cm; `Unit` has no compatible sibling) — the server converts to `BaseUnit` before
posting, and rejects an incompatible pair (`UNIT_CONVERSION_NOT_SUPPORTED`). See
[ADR-0017 §4](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md#4-unit-normalization-a-closed-explicit-conversion-table--not-a-general-unit-of-measure-framework) —
this is a closed, explicit conversion table, not a general unit-of-measure framework.

### 3.7 Purchase receipt cost and the S4 acquisition estimate
`PurchaseReceipt` captures `UnitCostSnapshot`/`TotalCostSnapshot` (per base unit, converted
alongside the quantity) as immutable acquisition history. S4 derives
`WEIGHTED_AVERAGE_ACQUISITION` from every positive cost-bearing purchase receipt. This is an
estimated acquisition basis, not remaining-stock valuation, FIFO/LIFO, accounting cost or actual
consumption. `Supply.LatestPurchaseUnitCost` remains informational and is not used by Costing.

> **Forward-reference note.** Sections below this point other than revised §6 (Costing) (Catalog,
> Production, Finance, §15 Domain events) were drafted before S3 and still reference `FilamentId`/`FilamentLot`/
> `SupplyLot`/`StockMovement`/`MaterialKind`, none of which exist anymore. Every such reference is
> **stale** and must be reconciled by the sprint that actually builds that module, against `Supply`
> (optionally carrying `FilamentDetails`) and `InventoryMovement` (§3 above) — see
> [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md). Left as a note
> rather than redesigned now, which is out of S3's scope.

---

## 4. Catalog module

> **S5 note ([ADR-0019](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md)).** The
> shape below is what S5 actually built: a single **current-state** recipe per product, reusing
> S4's Supply/`CostEngine` model rather than the pre-S4 Filament-component split. A forking,
> multi-revision recipe history is deferred until a real consumer (a QuoteRevision snapshot in
> S6, a ProductionOrder pointer in S9) defines what "current" must mean once a quote exists.

### Product (AR)
`Id` (UUID v7), `Code` (unique, normalized upper-case), `Name`, `Description?`, `Active`,
`Version` (optimistic concurrency, ADR-0011 §2), one owned `ProductRecipe` (1:1).

### ProductRecipe (*E*, owned by `Product`)
`Id`, `ProductId`, `RevisionNumber` (fixed at `1` in S5 — a forward-compatible placeholder, not a
working revision chain), `OutputQuantity: int ≥ 1` (batch size the recipe describes), `Notes?`,
plus process parameters passed straight into S4's `CostEngine` (never reimplemented):

- `WastagePercentOverride: decimal?` — **percentage points**, not a fraction (ADR-0018's
  exception, inherited here because this value flows directly into `CostCalculationInput`); null
  means "use the scenario/global default the same way the Cost Laboratory does."
- `LaborMinutes: decimal?`, `LaborHourlyRateOverride: decimal?` — null rate means "use
  `costing.default_labor_hourly_rate`"; null minutes means "this recipe has no labor component."
- `MachineMinutes: decimal?`, `MachineHourlyRate: decimal?` — machine cost requires an explicit
  rate once minutes are set (`RECIPE_MACHINE_RATE_REQUIRED` otherwise); there is no default
  machine rate setting, matching S4.

Owned lines (both *E*, both 0..N, **no maximum count**, replaced wholesale on every recipe edit
via `ReplaceMaterialLines`/`ReplaceAdditionalCostLines` rather than diffed):

| Line | Fields |
|---|---|
| `ProductRecipeMaterialLine` | `SupplyId` (no FK — Catalog never references Inventory, see ADR-0019 §2), `EnteredQuantity`, `EnteredUnit`, `NormalizedQuantityBaseUnit` (resolved by the API composition root via `SupplyUnitConversion`), `WastagePercentOverride?`, `ManualUnitCostOverride?`, `SortOrder` |
| `ProductRecipeAdditionalCostLine` | `Description`, `Amount: decimal ≥ 0`, `SortOrder` |

There is no `ProductFilamentComponent`/`ProductSupplyComponent` split (S4 already unified
"filament" into `Supply` + `SupplyCategory`, ADR-0017) and no separate `ProductCostLine.CostKind`
— `ProductRecipeAdditionalCostLine` is the one generic additional-cost shape S4's `CostEngine`
already accepts. Packaging remains a material line whose `Supply.CategoryCode = "PACKAGING"`,
not a distinct concept.

Invariants:
- Exactly one `ProductRecipe` per `Product` (1:1, created empty at product construction).
- `EnteredQuantity > 0`, its normalized base-unit quantity must clear the base-unit precision
  floor, `Amount ≥ 0`, `WastagePercentOverride ∈ [0, 100]` when set.
- A recipe may have zero material lines, zero labor and zero machine time — `ProductCostCalculator`
  rejects only a recipe with **no cost component at all** (`RECIPE_EMPTY`).
- **Duplicate `SupplyId` lines within one recipe are allowed and remain independent** — this
  matches S4's `CostEngine`, which never deduplicates material lines.
- Referenced Supplies are validated to exist at the moment a recipe is saved (`SUPPLY_NOT_FOUND`
  otherwise). An inactive Supply may remain in an **existing** recipe line (reading, editing an
  unrelated field, or recalculating cost never cares that a referenced Supply later went
  inactive), but a write may never **increase** how many lines reference an inactive Supply —
  per-Supply cardinality (`submittedCount ≤ existingCount`), not a per-line identity or set-only
  check, because duplicate lines against one Supply are legal (`SUPPLY_INACTIVE` otherwise; see
  [ADR-0019 §5.4/§5.6](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md#54-a-recipe-write-may-never-increase-how-many-lines-reference-an-inactive-supply)).

Editing a recipe never forks a new revision in S5 — it replaces the current one's parameters and
lines in place, bumping the owning `Product.Version` by exactly one (ADR-0027, one Unit of Work).
`RevisionNumber` exists in the schema precisely so that a future fork is an additive migration,
not a breaking rename, once S6/S9 define what triggers one.

### ProductCostCalculator (API-layer service, not a domain type)
Translates a persisted `Product`/`ProductRecipe` into S4's `CostCalculationInput` — resolving
each material line's cost source (manual override, else the Supply's weighted-average
acquisition basis via `ICostingInventoryReader`), the effective wastage rate, and the effective
labor/machine rates from Settings — then calls the **unmodified**
`Verce.Modules.Costing.CostEngine.Calculate` and returns its `CostCalculationResult` unchanged.
See [ADR-0019 §2](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md).

---

## 5. Energy module — future S10

Before S10 this module is a stub: CostEngine has no energy component and no default tariff is
seeded. Energy input/cost is unavailable/absent, never represented as zero.

### Machine (AR)
`Id`, `Name`, `Model?`, `SerialNumber?`, `NominalPowerWatts: int`,
`AveragePowerWatts: int?` (measured; preferred over nominal when present),
`AcquisitionCost: Money?`, `AcquisitionDate?`, `ExpectedLifetimeHours: int?`,
`MaintenanceCostPerHour: Money?`, `HourlyRateOverride: Money?`, `IsActive`, `DeletedAt?`.

Derived `MachineHourlyRate` — see [CALCULATION-RULES §4](CALCULATION-RULES.md#4-machine-cost).
`HourlyRateOverride`, when set, wins over the derived value.

### EnergyTariff (AR) + EnergyTariffVersion (*E*)
`EnergyTariff`: `Id`, `Name` (e.g. "CEEE Residencial B1"), `Utility?`, `IsDefault`, `IsActive`.

`EnergyTariffVersion`: `Id`, `EnergyTariffId`, `PricePerKwh: Money`, `IncludesTaxes: bool`,
`ValidFrom: DateOnly`, `ValidUntil: DateOnly?`, `Notes?`.

Invariants: versions of the same tariff must not overlap (enforced by a database exclusion
constraint); exactly one version resolvable at any instant; `PricePerKwh > 0`.

### EnergyConsumptionSession (AR)
`Id`, `MachineId`, `Source {ESTIMATED, MANUAL, SMART_PLUG}`, `StartedAt`, `EndedAt?`,
`Kwh: Kwh`, `AveragePowerWatts?`, `ProductionOrderItemId?`, `ExternalDeviceId?`,
`ExternalPayload: jsonb?`, `Notes?`.

Sessions with `ProductionOrderItemId` set are the **actual** energy for that item.
Sessions without it are free-standing measurements.

### IEnergyProvider (port)
```csharp
public interface IEnergyProvider
{
    EnergySourceKind Kind { get; }
    Task<EnergyMeasurement> MeasureAsync(EnergyMeasurementRequest request, CancellationToken ct);
}

public sealed record EnergyMeasurement(
    Kwh Kwh, EnergySourceKind Source, decimal Confidence, string? RawPayload);
```

Implementations from S10: `EstimatedEnergyProvider` (power × time), `ManualEnergyProvider`
(operator types the kWh), `SmartPlugEnergyProvider` (S10+, device not chosen — **no
vendor-specific code may be written before the device is selected**). Registration is by
`Kind`; the resolver picks the provider configured per machine, falling back to estimated.

---

## 6. Costing module

### CostEngine (domain service, pure)
Input `CostInput` → output `CostBreakdown`. See [CALCULATION-RULES](CALCULATION-RULES.md).

S4's `CostCalculationInput` is fully resolved before entering the engine: generic Supply material
lines preserve entered quantity/unit and normalized base quantity, resolved cost per base unit,
wastage percentage, current stock and cost source; labor has minutes/rate/source; machine has
minutes/rate; additional direct costs have description/amount; output quantity is an integer ≥ 1.

`CostBreakdown` is a `VO` tree:
```
CostCalculationResult
├── Materials[]           (identity, entered + normalized + effective quantity,
│                          source/policy/rate, before/wastage/after costs, stock warning)
├── AdditionalDirectCosts[] (description, amount)
├── Machine               (minutes, hourlyRate, cost)
├── Labor                 (minutes, hourlyRate, source, cost)
├── Totals                (material before waste, waste, materials, labor, machine,
│                          additional, total estimated, output quantity, estimated unit cost)
└── EngineVersion
```

### Laboratory (stateless S4 application flow)

The S4 Laboratory is not an aggregate. It has no ID, save, clone, history, conversion or database
row. `POST /api/costing/calculate` returns a transient result and never creates a quote, sale,
product, recipe, production record, audit row or stock movement. A line-level manual unit-cost
override is a simulation input and does not update Supply or purchase history. Browser state may
reset on reload. See ADR-0018.

---

## 7. Pricing module

> **S8A reality ([ADR-0019](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md),
> [ADR-0022](architecture/ADR-0022-pricing-override-discount-bracket-precedence.md)).** S5 shipped
> one FeeRule per channel with flat versions. S8A added optional PriceBracket children and the
> CommercialPricingEngine, while deliberately retaining one rule per channel and no
> product/category scope or priority.

### SalesChannel (AR)
`Id` (UUID v7), `Code` (unique, normalized upper-case), `Name`, `Kind {Direct, Marketplace,
Other}`, `DefaultMarginPercent: decimal?` (ADR-0002 **fraction**, e.g. `0.18` = 18% — Pricing
does *not* inherit ADR-0018's percentage-point exception, which is scoped to
`costing.default_wastage_rate` alone), `Notes?`, `Active`, `Version`.

"Venda Direta" (`Code = "DIRECT"`, `Kind = Direct`) is seeded at first boot as a normal channel
whose `FeeRuleVersion` is 0% commission + R$ 0,00 fixed fee, valid from `2020-01-01` with no end
date. **DIRECT identity is a reserved, bidirectional one-to-one pair**: `Code == "DIRECT" ⇔
Kind == Direct` — no other channel may ever be `Kind = Direct`, and the canonical row may never
change away from it (`SalesChannel.UpdateDetails`, [ADR-0019 §5.5/§5.7](architecture/ADR-0019-s5-product-recipe-and-pricing-engine.md#57-direct-identity-is-a-reserved-bidirectional-codekind-pair)).
**There is no special case in the engine** — direct sale is the degenerate marketplace.
This is deliberate: one formula, one code path, one set of tests
([ADR-0005](architecture/ADR-0005-marketplace-fee-rules.md)).

### FeeRule (AR) + FeeRuleVersion (*E*)

`FeeRule`: `Id`, `SalesChannelId` (unique — exactly one rule per channel), `Name`, `Active`,
`Version`. There is no `AppliesTo`, `TargetId` or `Priority`; S8A needed price tiers, not a second
rule-resolution hierarchy.

`FeeRuleVersion`: `Id`, `FeeRuleId`, `ValidFrom: DateOnly`, `ValidUntil: DateOnly?` (half-open
`[from, until)`), `CommissionPercent: decimal ∈ [0, 1)` (ADR-0002 fraction),
`FixedFee: decimal ≥ 0`, `FixedFeeApplication {PerUnit, PerOrder}` (see
[CR-07.3](CALCULATION-RULES.md#cr-073--fixed-fee-application)), `MinimumFee: decimal?`,
`MaximumFee: decimal?`, `Notes?`, zero or more immutable `PriceBracket` children. There is no
provider shipping component in Pricing; Commerce shipping policy remains separate.

`PriceBracket`: `Id`, `FeeRuleVersionId`, `MinPrice`, `MaxPrice?`, `CommissionPercent`,
`FixedFee`, `MinimumFee?`, `MaximumFee?`, `SortOrder`. Ranges are half-open and non-overlapping;
the final range may be open-ended.

Invariants:
- Versions of the same rule must not overlap in time — enforced by a PostgreSQL `EXCLUDE USING
  gist` constraint over `(fee_rule_id, daterange(valid_from, valid_until, '[)'))`, requiring the
  `btree_gist` extension (raw SQL in the migration; deliberately unmodeled in
  `FeeRuleVersionConfiguration` so `has-pending-model-changes` never flags it as drift).
- `CommissionPercent ∈ [0, 1)`, `FixedFee ≥ 0`, `MinimumFee ≤ MaximumFee` when both present.
- An inverted window (`ValidUntil ≤ ValidFrom`) is rejected at construction
  (`FEE_RULE_VERSION_INVALID_WINDOW`).
- Brackets of one version do not overlap; `MaxPrice > MinPrice` when bounded. DIRECT versions and
  brackets must remain zero-fee.

Resolution: given `(salesChannelId, instant)`, `FeeRule.ResolveVersionAt(instant)` picks the
single version whose `[ValidFrom, ValidUntil)` covers `instant` — no product scope or priority.
Missing resolution fails with `FEE_RULE_NOT_FOUND`, never a silent zero-fee default. The
CommercialPricingEngine then resolves optional brackets under
[CR-07.5](CALCULATION-RULES.md#cr-075--bracket-resolution-fee-depends-on-price-price-depends-on-fee).

### PricingEngine (domain service, pure)
Input (`PricingCalculationInput`): `unitTotalCost`, `commissionPercent`, `fixedFee`,
`desiredMargin`, `roundingPolicy {CENT, TEN_CENTS, WHOLE, NINETY_NINE, NONE}`,
`marginWarningDenominator`, optional `minimumFee`/`maximumFee`. Output
(`PricingCalculationResult`): `denominator`, `rawPrice`, `suggestedPrice`, `commissionAmount`
(clamped by min/max fee, reporting which clamp fired), and `warnings` (e.g.
`PRICING_EXTREME_MARGIN` when the denominator falls below the warning threshold but is still
valid). Rejects an invalid commission, margin or non-positive denominator before computing
anything — never returns a number derived from one. The rounding policy is applied directly to
`rawPrice`, not to a pre-rounded value (ADR-0019 §4). `Product`-aware pricing
(`POST /api/pricing/products/{id}/price`) resolves `unitTotalCost` from
`ProductCostCalculator`'s `EstimatedUnitCost` and the commission/fixed fee/min/max from
`FeeRule.ResolveVersionAt(today)` before calling this same pure engine.

### CommercialPricingEngine (domain service, pure; S8A)

Wraps the S5 PricingEngine with optional PriceBracket consistency/containment resolution, manual
price override, discount, post-discount commission basis, fee clamps and explanation metadata.
Its normative precedence is ADR-0022 and CR-07.5/CR-07.6/CR-08. Channel comparison in S8B reuses
this authority; Commerce does not copy its formulas.

---

## 8. Quoting module

### Quote (AR — the container)
`Id`, `Number: QuoteNumber` (date + sequence, **without** revision suffix),
`CustomerId?`, `CurrentRevisionId`, `CreatedAt`, `CreatedBy`.

The `Quote` holds identity and the pointer to the current revision. It holds **no** prices and
**no status** — status lives on the revision ([STATE-MACHINES §1](STATE-MACHINES.md#1-quote-revision-status)).

Its **commercial outcome** (`WON` / `LOST` / `OPEN`) is likewise not stored: it is derived from the
append-only status history and is what conversion reporting reads
([DATA-DICTIONARY §4.1](DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão),
[ADR-0020 §B.2](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)). A win is
permanent and dated at the *first* approval — `hasEverWon` is absorbing and so is `WON` once
reached — but that is not true of the *current* classification of a quote that has never been
won: `OPEN`/`LOST` may legitimately move back and forth as an unwon quote's current revision
expires and is later revived by a new one.

Reporting reads that history through a second, distinct rule: for conversion KPIs a quote
contributes **at most one decision per reporting period**, with a win in the period taking
precedence over any pre-win loss in it. The status history itself stays append-only and complete —
the deduplication lives only in how the metric is read (DATA-DICTIONARY §4.1).

### QuoteRevision (*E*, immutable once issued)
`Id`, `QuoteId`, `RevisionIndex` (1 = original, displayed without suffix),
`RevisionSuffix` (computed: 1→"", 2→"B", 26→"Z", 27→"AA"), `Status: QuoteStatus`,
`SalesChannelId`, `IssuedAt`, `ValidUntil: DateOnly`, `ValidityDays`,
`CustomerSnapshot: jsonb` (name, document, e-mail, phone, addresses at issue time),
`SubtotalAmount`, `DiscountAmount`, `TotalAmount`, `TotalCostAmount`, `ExpectedProfitAmount`,
`EffectiveMarginPercent`, `Notes?`, `InternalNotes?`, `SupersededByRevisionId?`,
`SourceRevisionId?`, `CalculationEngineVersion`.

**Proposal content fields** (all optional, added by the branding addendum so the default
proposal template has somewhere to read from):

| Field | Type | Source |
|---|---|---|
| `Title` | text? | the project/proposal title |
| `Scope` | text? | free description |
| `TechnicalHighlights` | `jsonb` — array of `{label, value}` | typed per quote, may be pre-filled from the product |
| `TechnicalNotes` | text? | |
| `OutOfScope` | text? | |
| `PaymentTerms` | text? | defaults from `documents.default_payment_terms` |
| `DeliveryTerms` | text? | defaults from `documents.default_delivery_terms` |
| `Warranty` | text? | defaults from `documents.default_warranty` |

Two rules govern these:

1. **They are optional and free-form.** `TechnicalHighlights` is a generic label/value
   collection — `Material / PETG`, `Tolerância / ±0,2 mm`, `Cor / Acabamento`. There is
   deliberately **no `material` column, no `tolerance` column and no `prototype` flag** on any
   quote: those are proposal *content* that varies by product, not domain attributes every
   quote must carry.
2. **Defaults are copied onto the revision at issue, never read live.** Changing the default
   payment terms must not alter a proposal already sent — the same rule that governs prices
   ([ADR-0003](architecture/ADR-0003-cost-and-price-snapshots.md)).

Item-level technical highlights are a natural extension and are deliberately deferred; v1 holds
them at revision level, matching the reference proposal.

Invariants (the heart of the product):
- **A revision is immutable after creation.** No field that affects money may change. Only
  `Status`, `SupersededByRevisionId` and status-history rows change afterwards.
- Only the revision equal to `Quote.CurrentRevisionId` may be approved, sent or canceled.
- Creating a revision copies the previous revision's items, then applies the requested change.
- `RevisionIndex` is unique per quote; the suffix algorithm is bijective base-26
  ([ADR-0004](architecture/ADR-0004-quote-numbering-and-revisioning.md)).
- `ValidUntil = IssuedAt(org date) + ValidityDays`; `ValidityDays` defaults from settings
  (15) and is stored per revision so a settings change never moves an existing deadline.

### QuoteItem (*E*)
`Id`, `QuoteRevisionId`, `LineNumber`, `ProductId?`, `ProductRecipeId?`,
`ProductNameSnapshot`, `Description?`, `Quantity`, `UnitCostAmount`, `SuggestedUnitPrice`,
`UnitPrice` (final, possibly overridden), `DiscountKind {NONE, PERCENT, AMOUNT}`,
`DiscountValue`, `DiscountAmount`, `LineTotalAmount`, `DesiredMarginPercent`,
`SalesChannelId` (must equal the revision channel in S8A; mixed-channel revisions fail
`QUOTE_MIXED_CHANNEL_NOT_SUPPORTED`),
`ExpectedProfitAmount`, `EffectiveMarginPercent`, `SortOrder`.

An item may be **ad-hoc** (no `ProductId`): a free description plus a manual cost. The
laboratory and quick quotes need this.

When the applicable `FeeRuleVersion` uses `FixedFeeApplication = PerOrder`, each item also carries
**its allocated share of the single order-level fee** — the value CR-07.3 has always required the
snapshot to hold alongside the raw fee. The shares are allocated in proportion to line estimated
cost and sum to exactly one fee per revision
([CR-07.7](CALCULATION-RULES.md#cr-077--per_order-fee-allocation-across-lines),
[ADR-0020 §C](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)); the share
feeds both the item's suggested price and its `lineFeeAmount`, so the order fee is recovered once
across the revision rather than once per line.

### QuoteItemCostSnapshot (*E*, 1:1 with QuoteItem)
Typed columns for every cost and pricing driver + `Breakdown: jsonb`.
See [ADR-0003](architecture/ADR-0003-cost-and-price-snapshots.md) and
[DATA-MODEL §7](DATA-MODEL.md#9-quoting-schema).

### QuoteStatusHistory (*E*, append-only)
`Id`, `QuoteRevisionId`, `FromStatus?`, `ToStatus`, `ChangedAt`, `ChangedBy?`,
`Trigger {USER, SYSTEM_JOB, EVENT}`, `Reason?`, `Notes?`.
This is business history, distinct from the generic audit log.

### QuoteDocument (*E*)
Link between a revision and a `GeneratedDocument`, with `IsCurrent` per document type, so a
revision can be re-rendered (e.g. after a template fix) while keeping every previously issued
file.

### Numbering
`QuoteNumberCounter` (AR, one row per business date): `CounterDate: DateOnly`, `LastSequence`.
Allocation is a single atomic upsert-and-return inside the quote's transaction.
See [ADR-0004](architecture/ADR-0004-quote-numbering-and-revisioning.md).

---

## 9. Sales module

### Sale (AR)
`Id`, `SaleNumber`, `CustomerId?`, `CustomerNameSnapshot`, `SalesChannelId`, `QuoteRevisionId?`,
`ConversionRequestId?`, `Source {QUOTE_CONVERSION, MANUAL_ENTRY, MARKETPLACE_ORDER}`,
`MarketplaceAccountId?`, `ExternalOrderId?`, `FeeSource {LOCAL_RULE, PROVIDER_REPORTED}`, `SoldAt`,
`Status {CONFIRMED, CANCELED}`, `GrossAmount`, `DiscountAmount`, `NetAmount`,
`ChannelFeeAmount`, `ShippingAmount`, `TotalCostAmount`, `GrossProfitAmount`,
`EffectiveMarginPercent`, `ExternalOrderCode?`, `Notes?`.

### SaleItem (*E*)
`Id`, `SaleId`, `LineNumber`, `ProductId?`, `ProductNameSnapshot`, `Quantity`, `UnitPrice`,
`DiscountAmount`, `LineTotalAmount`, `UnitCostAmount`, `LineCostAmount`,
`ChannelFeeAmount`, `GrossProfitAmount`, `QuoteItemId?`.

### SaleStatusHistory (*E*)
Append-only entity owned by Sale: `SaleId`, `FromStatus?`, `ToStatus`, `Reason?`, `ChangedAt`,
`ChangedBy?`. Creation appends the initial `CONFIRMED` entry. Cancellation appends
`CONFIRMED -> CANCELED` with a nonblank reason, actor and timestamp; history is never rewritten.

### Quote versus Sale — the boundary

| | Quote | Sale |
|---|---|---|
| Nature | **Commercial proposal** | **Financial fact** |
| Answers | "What would this cost the customer?" | "What did we actually earn?" |
| Created | Before the deal | When the deal closes |
| Mutable | No (revisions) | Status only |
| Cost figure | Estimated, snapshotted | Best-known cost: actual when reconciled, else the snapshot |
| Reports | Conversion, pipeline | Revenue, profit, margin |

A Sale is created only by an explicit operator command from an `APPROVED` revision; approval
creates ProductionOrder, not Sale. Conversion copies priced lines/totals and never reruns pricing;
at most one non-canceled Sale references a revision. A standalone `MANUAL_ENTRY` may occur on
DIRECT or a marketplace channel and freezes an available authorized cost basis.

Duplication is bounded and intentional: a quote is a statement made then; a sale is money
recognized now. Aggregating revenue from `quote_item` would be wrong (quotes that never
closed) and aggregating proposals from `sale_item` would be wrong (deals never proposed).
**Reports read revenue and profit from `Sale` only. Reports read conversion from `Quote` only.**

Sales cost reconciliation: `Sale.TotalCostAmount` is initialized from the quote snapshot and
recomputed when the linked production order reports actual consumption
([ADR-0006](architecture/ADR-0006-estimated-vs-actual-cost.md)).

---

## 10. Production module

### ProductionOrder (AR)
`Id`, `OrderNumber` (same `YYMMDD-N` generator, separate counter series),
`QuoteRevisionId` (**unique** — the idempotency key), `CustomerId?`,
`Status: ProductionOrderStatus`, `DueDate?`, `TotalQuantity`,
`ShippingAddressSnapshot: jsonb`, `CustomerNameSnapshot`, `Notes?`, `StartedAt?`,
`CompletedAt?`, `ShippedAt?`, `DeliveredAt?`, `CancellationReason?`, `SupersededByOrderId?`.

### ProductionOrderItem (*E*)
`Id`, `ProductionOrderId`, `QuoteItemId?`, `ProductId?`, `ProductRecipeId?`,
`ProductNameSnapshot`, `Quantity`, `QuantityProduced`, `Status`, `Notes?`.

Plus, for the shop floor and for reconciliation:

- `ProductionOrderItemPlannedMaterial` (*E*) — the exploded, snapshotted BOM: filament
  components with grams and color, supplies with quantities. Copied from the quote snapshot so
  the shop-floor document is correct even if the recipe changes tomorrow.
- `ProductionOrderItemActualMaterial` (*E*) — S9 physical fact: `MaterialKind`, `MaterialId`,
  `ActualQuantity`, `RecordedAt`, `RecordedBy`, correction acknowledgement/reason and the atomic
  `InventoryMovement(Consumption)` identity. S11 later values this fact; it does not post stock.
- Actual energy comes from `EnergyConsumptionSession.ProductionOrderItemId`.

**Snapshot policy here:** copy what the shop floor must read without joins to mutable master
data (names, colors, quantities, address). Do **not** copy prices — cost figures live in the
quote snapshot and the actual-consumption rows.

**Idempotency** *(corrected 2026-09-07, gate blocker B-001)*: `QuoteRevisionId` is unique, and
the `QuoteApproved` handler uses
`INSERT … ON CONFLICT (quote_revision_id) DO NOTHING` followed by a `SELECT`, so replaying the
event, retrying a request or double-clicking approve produces exactly one order.

> The handler must **never** "catch the unique violation and continue". In PostgreSQL a statement
> error aborts the whole transaction (`25P02`); every later command fails until `ROLLBACK`, so
> catching the .NET exception would destroy the transaction it appears to protect. Only
> `ON CONFLICT` (used here) or an explicit `SAVEPOINT`/`ROLLBACK TO SAVEPOINT` is correct.
> See [ADR-0012 §8](architecture/ADR-0012-domain-events-and-outbox.md#11-the-quote-approval-invariant).

The handler runs **synchronously inside the approval transaction**, so if the order cannot be
created the approval rolls back. Creating it through the outbox would be wrong: the outbox
guarantees eventual delivery, not atomicity, and an approved quote with no production order —
even briefly — violates the invariant.

---

## 11. Finance module

### Expense (AR)
`Id`, `ExpenseCategoryId`, `Description`, `Amount: Money`, `IncurredOn: DateOnly`,
`PaidOn: DateOnly?`, `PaymentMethod?`, `SupplierName?`, `DocumentNumber?`,
`AccountingTreatment {OPERATING_EXPENSE, INVENTORY_PURCHASE, ASSET_ACQUISITION}`,
`InventoryMovementId?` (plain UUID to PurchaseReceipt), `SalesChannelId?`, `MachineId?`, `Notes?`,
`AttachmentPath?`.

### ExpenseCategory (AR)
`Id`, `Name`, `DefaultTreatment`, `IsActive`.
Seeded: Filamento, Insumos, Energia, Embalagem, Marketing, Frete, Manutenção, Equipamento,
Impostos/Taxas, Outros.

### The double-counting rule (mandatory)

A spool of filament costs money once. It must not appear twice in the same profit figure —
once as an expense "Filamento R$ 89,90" and once as material cost inside sold products.

Rule:

1. Expenses whose treatment is `INVENTORY_PURCHASE` are **excluded from the operational
   result**. They reach the result through consumption (material cost of items sold).
2. A PurchaseReceipt InventoryMovement may create one linked `Expense` (treatment forced to
   `INVENTORY_PURCHASE`). The plain UUID link is unique, so the same purchase cannot be entered
   twice through the two doors.
3. Expenses whose treatment is `ASSET_ACQUISITION` (a printer) are excluded from the
   operational result too; they reach it through `MachineHourlyRate` depreciation.
4. Only `OPERATING_EXPENSE` rows enter "Custos/Despesas do mês" in the dashboard.
5. From S10, energy is the sharp edge: if the electricity bill is registered as an
   `OPERATING_EXPENSE` **and** energy cost is inside product cost, the profit report double counts. The resolution
   is stated in [ADR-0013](architecture/ADR-0013-expense-inventory-double-counting.md): the
   utility bill is registered as `OPERATING_EXPENSE` and the **Cash Result** report shows it,
   while the **Product Margin** report uses the per-item energy cost. The two reports are
   labeled distinctly and never summed. The dashboard shows the Cash Result.

---

## 12. Documents module

### DocumentType (lookup)
`QUOTE`, `PRODUCTION_ORDER`, `SHIPPING_LABEL`. Each declares a **binding catalogue** and an
`IRenderContextResolver` implemented by the owning module.

### DocumentTemplate (AR)
`Id`, `DocumentTypeCode`, `Name`, `IsDefault`, `IsActive`, `DeletedAt?`.

### DocumentTemplateVersion (*E*)
`Id`, `DocumentTemplateId`, `VersionNumber`, `Status {DRAFT, PUBLISHED, ARCHIVED}`,
`SchemaVersion`, `Definition: jsonb` (the block tree + `theme` tokens), `PageSetup: jsonb`
(size, orientation, margins, header/footer regions with `repeatOn` — a shipping label uses the
same field with custom millimetres), `PublishedAt?`, `PublishedBy?`.

The block tree supports **controlled bindings**, **declarative `visibleWhen` conditionals** and
**repeatable collections**. `{{token}}` syntax exists only in the Studio picker; resolution is
by typed path against a `RenderContext`, never by string replacement. Publishing validates every
path against the type's catalogue. Full grammar in
[ADR-0007 §3](architecture/ADR-0007-document-template-engine.md).

Block families: `Logo`, `Text`, `Header`, `DynamicField`, `RichText`, `TechnicalHighlight`,
`ItemsTable`, `Totals`, `Terms`, `Notes`, `Image`, `QrCode`, `Separator`, `PageNumber`,
`Spacer`, `ConditionalSection`.

`TechnicalHighlight` renders a generic `{label, value}` collection (`Material / PETG`,
`Tolerância / ±0,2 mm`). It is presentation only: **no domain entity is created for material,
tolerance, colour or finish, and no such field is mandatory on a quote.**

### GeneratedDocument (AR — historical evidence)
`Id`, `DocumentTypeCode`, `SourceType`, `SourceId`, `Purpose {PREVIEW, ISSUED}`,
`DocumentTemplateVersionId`, `PdfPath`, `PdfSha256`, `PdfSizeBytes`,
`RenderedHtmlPath`, `RenderedHtmlSha256`, `RenderDataSnapshot: jsonb`,
`BrandAssetVersionIds: uuid[]`, `GeneratedAt`, `GeneratedBy`, `IssuedAt?`, `SentAt?`,
`RenderDurationMs`, `ChromiumVersion`, `RenderEngineVersion`, `IsCurrent`.

`RenderDataSnapshot` is the **fully resolved render context** — company data, customer data,
quote fields, items, totals, terms, highlights and resolved brand asset versions — exactly as
handed to the renderer.

Rules ([ADR-0016](architecture/ADR-0016-document-render-snapshots.md)):

- Only `PUBLISHED` template versions may render. A published version is immutable; changes
  create a new version.
- A document with `Purpose = ISSUED` is **permanent and immutable**. No code path updates or
  deletes it.
- Re-rendering a revision **inserts a new row**; it never overwrites an existing document.
- `PREVIEW` documents are disposable and prunable after `documents.preview_retention_days` (30).
- Changing the logo, the company phone, the template or the theme affects **future** documents
  only.

---

## 13. AI module

`AiSettings` (AR, single row): `Id`, `Provider` (`OPENAI`), `ApiKeyEncrypted: bytea`,
`ApiKeyLastFour`, `ApiKeyUpdatedAt`, `DefaultModel` (`gpt-5.4`), `FallbackModel`
(`gpt-5.4-mini`), `DefaultPrompt`, `MaxOutputTokens`, `Temperature`,
`IncludeCustomerNames: bool` (**default false**), `MonthlyBudgetAmount: Money?`, `IsEnabled`.

`AiInsightRun` (AR): `Id`, `TriggeredBy`, `TriggerKind {MANUAL, SCHEDULED}`, `Model`,
`PromptTemplateUsed`, `PeriodFrom`, `PeriodTo`, `DatasetFingerprint` (sha256 of the payload),
`DatasetSummary: jsonb` (**what was sent** — the payload itself, retained for auditability),
`Status {PENDING, RUNNING, SUCCEEDED, FAILED}`, `StartedAt`, `CompletedAt?`,
`PromptTokens?`, `CompletionTokens?`, `EstimatedCost?`, `ErrorMessage?`.

`AiInsightResult` (*E*): `Id`, `AiInsightRunId`, `Kind {FACT, HYPOTHESIS, RECOMMENDATION}`,
`Priority`, `Title`, `Body`, `RelatedEntityType?`, `RelatedEntityId?`, `Metrics: jsonb?`.

The API key is never returned by any endpoint — only `ApiKeyLastFour`. See
[ADR-0008](architecture/ADR-0008-ai-integration-and-secret-handling.md) and
[SECURITY](SECURITY.md).

---

## 14. Settings module

Owns organization identity: company profile, brand assets, application branding, app settings
and the theme registry. Brand assets live here rather than in a `Branding` module of their own —
they are one cohesive concern with company identity, and Documents already depends on Settings
([ADR-0015](architecture/ADR-0015-brand-assets-and-application-branding.md)).

### CompanyProfile (AR, single row)
`LegalName`, `TradeName`, `Document`, `Email`, `Phone`, `Website`, `Instagram`, `WhatsApp`,
`ZipCode`, `Street`, `Number`, `Complement?`, `District`, `City`, `State`, `Country`,
`Timezone` (default `America/Sao_Paulo`), `Currency` (default `BRL`).

Supplies the `company.*` bindings to every document. Fiscal modeling stays deliberately shallow:
`Document` is one free field, not a fiscal regime model.

*(Replaces the `OrganizationProfile` name used before the branding addendum.)*

### BrandAssetType (lookup, not a C# enum)
`PRIMARY_LOGO`, `COMPACT_LOGO`, `NEGATIVE_LOGO`, `SYMBOL`, `FAVICON`, `DOCUMENT_LOGO`, `OTHER`,
plus S8B target `PRODUCT_IMAGE`. Adding a type is data; S8B adds the row, not a C# enum member.

### BrandAsset (AR)
`Id`, `BrandAssetTypeCode`, `Name`, `IsActive`, `CurrentVersionId`, `DeletedAt?`.

### BrandAssetVersion (*E*, immutable, never deleted)
`Id`, `BrandAssetId`, `VersionNumber`, `FilePath` (content-addressed by sha256), `Sha256`,
`ContentType`, `FileSizeBytes`, `WidthPx`, `HeightPx`, `OriginalFileName` (metadata only —
never used in a path), `UploadedAt`, `UploadedBy`, `IsCurrent`.

**Replacing a logo creates a new version.** Documents reference the **version**, never the
asset, which is what keeps an already-issued proposal rendering the logo it was issued with.

v1 accepts `PNG`, `JPEG`, `WEBP`. **SVG is excluded from v1** — it is XML that can carry script
and external references. The validation pipeline is per-content-type (`IUploadValidator`), so
adding SVG later means adding a sanitizing validator, reviewed on its own merits.

### BrandingAssignment (AR)
`Role {SYSTEM_LOGO, SYSTEM_LOGO_COMPACT, FAVICON, DOCUMENT_DEFAULT_LOGO}` (**unique**),
`BrandAssetId`.

The application asks for *the system logo*, never for a named asset. Documents that inherit
resolve through `DOCUMENT_DEFAULT_LOGO`. Product name (`VERCE 3D`) and subtitle
(`Laboratório de Custos`) are app settings, so the frontend hard-codes no branding at all.

### AppSetting (AR)
Typed key/value store, `Key`, `Value`, `ValueType`, `Scope`, `UpdatedAt`, `UpdatedBy`. Audited.
Initial keys:

| Key | Default | Used by |
|---|---|---|
| `quote.default_validity_days` | `15` | Quoting |
| `quote.allow_direct_approval` | `true` | Quoting |
| `pricing.default_margin_percent` | `0.35` | Pricing |
| `pricing.price_rounding_policy` | `CENT` | Pricing |
| `pricing.margin_warning_denominator` | `0.10` | Pricing |
| `costing.default_labor_hourly_rate` | `0.00` | Costing |
| `costing.default_wastage_rate` | `0.00` percentage points (0–100) | Costing |
| `energy.default_tariff_id` | introduced and seeded in S10 | Energy |
| `energy.overhead_factor` | introduced in S10 | Energy |
| `ui.default_theme` | `verce-default` | Frontend |
| `branding.product_name` | `VERCE 3D` | Frontend, documents |
| `branding.product_subtitle` | `Laboratório de Custos` | Frontend |
| `documents.default_payment_terms` | (empty) | Quoting — copied onto each revision |
| `documents.default_delivery_terms` | (empty) | Quoting — copied onto each revision |
| `documents.default_warranty` | (empty) | Quoting — copied onto each revision |
| `documents.preview_retention_days` | `30` | Documents |
| `uploads.max_image_bytes` | `5242880` | Settings |
| `commerce.listing_observation_retention_days` | `180` (introduced in S8B) | Commerce |

`timezone` and `currency` are **not** app settings — they are fields on `CompanyProfile`,
because they are organization facts rather than tunable behaviour.

---

## 14A. Commerce module — S8B target (fifteenth business module)

> **Architecture freeze:** [ADR-0023](architecture/ADR-0023-s8b-commerce-foundation.md). S8B
> owns provider-neutral commercial intent and observed listing facts. It does not call provider
> APIs, ingest marketplace orders or create a parallel financial Sale.

### ChannelOffer (AR)

`Id`, `ProductId` (logical Catalog reference), `SalesChannelId` (logical Pricing reference),
`Status {ACTIVE, INACTIVE}`, `IntendedUnitPrice`,
`PriceSource {PRICING_ENGINE, MANUAL, IMPORTED_OBSERVED}`,
`EstimatedSellerPaidShippingAmount?`,
`ShippingEstimateSource {MANUAL, IMPORTED_OBSERVED, PROVIDER_SYNC}?` (S8B produces only the first
two), `ActivatedAt?`, `DeactivatedAt?`,
audit timestamps and `Version`.

Business identity is the ordinary unique pair `(ProductId, SalesChannelId)`. Deactivation retains
the row and does not free the pair. Activation requires the Product and SalesChannel to be active.
Commercial activation is derived, never stored on Product:

```text
product.Active && exists ACTIVE ChannelOffer
```

`IntendedUnitPrice` is VERCE intent and is never overwritten by a listing observation. DIRECT is
a normal ChannelOffer over the canonical Pricing channel; it has no MarketplaceAccount or
MarketplaceListing.

### MarketplaceProvider (reference data)

Textual codes `MERCADO_LIVRE`, `SHOPEE`, `TIKTOK_SHOP`. DIRECT is not a provider. Provider
capability state is separate from account grant state and uses the closed vocabulary
`LISTINGS_READ`, `LISTINGS_WRITE`, `ORDERS_READ`, `FEES_QUOTE`, `ANALYTICS_READ`, `ADS_READ`,
`SHIPPING_READ`, `INVENTORY_SYNC`.

### MarketplaceAccount (AR)

`Id`, `ProviderCode`, `SalesChannelId`, `DisplayName`, `ExternalAccountId`, `Active`,
`CredentialReference?`, `ConnectionState {NOT_CONFIGURED, DISCONNECTED, CONNECTED, ERROR}`,
`SyncState {NEVER_SYNCED, SYNCED, ERROR}`, last-success/attempt/failure metadata, audit timestamps
and `Version`.

Business identity is `(ProviderCode, ExternalAccountId)`; multiple accounts for one provider are
valid. Credentials are external protected configuration referenced by identifier, never raw token
columns. At creation, `SalesChannelId` must resolve to an active Pricing channel with
`Kind = Marketplace` and code other than `DIRECT`. It is immutable thereafter; deactivation
blocks provider work but preserves the channel mapping, listings and ChannelOffer intent.

Effective capability is provider `SUPPORTED` intersected with account `GRANTED` on an active
account. Structural provider state is `UNKNOWN|SUPPORTED|UNSUPPORTED`; account grant is
`UNKNOWN|GRANTED|DENIED`. Transient failure is connection/sync/runtime health, not capability or
grant mutation, and may block execution without rewriting those structural facts.

### S8C.1 connection extension (corrected architecture candidate)

[ADR-0024](architecture/ADR-0024-s8c1-marketplace-connector-authorization-foundation.md)
supersedes the S8B connection interpretation on implementation. MarketplaceAccount retains
durable ProviderCode + ExternalAccountId and immutable SalesChannelId; CredentialReference
becomes a server-generated opaque pointer to the current credential generation, never writable
in public DTOs. Normal refresh/reconnect retain the reference and advance its store-owned version;
after confirmed store/key loss or an unconfirmed higher K1 version from a FAIL_CLOSED operation,
explicit bound recovery retires K1 and confirms a fresh K2/version 1. An old K1 file or key
backup cannot become current again merely by reappearing.
Its owned MarketplaceAccountConnection is the sole authorization/runtime authority. The old
ConnectionState, LastError and generic LastFailureAt are removed by the single migration;
SyncState and resource-sync timestamps remain.

Authorization is exactly NOT_CONNECTED|CONNECTED|REAUTHORIZATION_REQUIRED|REVOKED.
No ERROR or AUTHORIZATION_PENDING; pending work is derived from a separate authorization
session. Start reconnect preserves usable credentials; once its exchange may have been sent the
durable operation row suspends use until its result is confirmed. Disconnect sets REVOKED, runtime UNKNOWN,
denies secret reads immediately and performs idempotent local deletion while retaining history.

Runtime is UNKNOWN|AVAILABLE|UNAVAILABLE; no DEGRADED. UNKNOWN permits the first controlled
attempt; successful probe/operation makes AVAILABLE, terminal operational failure makes
UNAVAILABLE. Owner probe is the recovery path and honors persisted Retry-After. Runtime and
timestamps persist across restart; UNKNOWN is not synthesized by an unconfigured freshness timer.
Ordinary business execution requires SUPPORTED + GRANTED + MarketplaceAccount.Active + an existing
active Marketplace SalesChannel + usable authorization + runtime != UNAVAILABLE. A provider-wide
callback operation pending after send blocks credential execution until its identity/impact is
resolved. Connect/inspect/probe do not require unrelated business grants; probe still respects
the active Marketplace channel and persisted Retry-After.

MarketplaceAuthorizationSession stores provider/channel, initiating actor, optional intended
reconnect account, requested label, hashed random state/browser binding, status/timestamps,
protected transient handle and Version. It has a 15-minute lifetime; one atomic PENDING ->
CLAIMED transition owns the exchange forever. Browser/actor/provider/channel are validated.
New identity is provider-derived; reconnect must match its bound account exactly. An unbound new
session discovering an existing identity returns a safe reconnect-required conflict. Under
UNKNOWN/MAY_SUPERSEDE credential impact, the existing account becomes REAUTHORIZATION_REQUIRED,
even if inactive; the new candidate is discarded after terminal arbitration. Two new sessions
for one identity can leave the unique winner requiring explicit reconnect after the later
exchange. A reconnect X returning Y never rebinds history; X and any existing Y fail closed when
impact is unknown. The fake can invalidate the prior credential for the same identity, without
asserting real-provider behavior.

`MarketplaceAccountOperation` is the single durable PostgreSQL arbiter for CONNECT_NEW,
RECONNECT, REFRESH and DISCONNECT. It is committed before an external code/refresh exchange.
Its terminal decision is PENDING -> CONFIRMED or FAIL_CLOSED, never both; operation-row locks
serialize final confirmation, compensation, startup and Quartz. Store receipts prove only
material persistence. CONNECT_NEW/RECONNECT require session, actor, identity, channel, account,
grants and audit in the same CONFIRMED transaction; receipt-only restart recovery cannot complete
them. REFRESH may confirm an exact same-operation receipt after its account/reference/version
guards. The connection points only to a currently CONFIRMED operation/reference/version; a
candidate never becomes executable. If DB commit response is lost, a resolver waits on the
operation row for commit or rollback before acting. Quartz startup/15-minute cleanup resolves
abandoned claims and orphans through the same arbiter, with due-time/keyset fairness.

All pre-S8C.1 accounts remain with configured identity but NOT_CONNECTED/UNKNOWN and cleared
legacy credential pointers; successful bound reconnect establishes IdentityVerifiedAt. Generic
account POST is retired (410), PUT allows DisplayName + Version only, and manual grant mutation
is retired (410). No client controls ExternalAccountId or CredentialReference. Grant metadata
is LEGACY_MANUAL|AUTH_INSPECTION|PROBE_INSPECTION, bounded safe reason and VerifiedAt.
Outages do not change support/grants, and OAuth scopes alone do not prove business eligibility.
For a legacy mistyped ExternalAccountId, reject the mismatched reconnect, preserve/deactivate the
old account and its history, and authorize the correct identity separately when allowed. History
reassociation needs a later explicit administrative workflow, never automatic mutation.

### MarketplaceListing (AR)

`Id`, `MarketplaceAccountId`, `ExternalListingId`, `ExternalSku?`, `ProductId?`,
`ChannelOfferId?`, `TitleSnapshot?`, `ObservedPrice?`, `ListingUrl?`,
`ObservedStatus {DRAFT, ACTIVE, PAUSED, INACTIVE, ERROR}`, `ProviderNativeStatus?`,
`LinkageState {UNLINKED, NEEDS_REVIEW, LINKED}`, `SyncState {NEVER_SYNCED, SYNCED, ERROR}`,
`ProviderObservedAt?`, `LastSyncAttemptAt?`, `LastSuccessfulSyncAt?`, bounded/redacted
`SyncError?`, audit timestamps and `Version`.

Business identity is the ordinary unique pair `(MarketplaceAccountId, ExternalListingId)`.
Product is nullable so `Não vinculadas` is first-class. LINKED requires ProductId; UNLINKED and
NEEDS_REVIEW require both ProductId and ChannelOfferId null. A linked Product may have null
ChannelOfferId until commercial intent is explicitly connected/created. Non-null ChannelOfferId
requires LINKED and must identify that Product × the account's immutable SalesChannel. Same-schema
FKs prove existence; Commerce validation proves Product/channel equality. Observed state, linkage
and sync are independent: `ACTIVE + LINKED + ERROR` is valid. Unlinking never deletes the listing
or its external identity.

### MarketplaceListingObservation (*E*, append-only)

Safe normalized snapshot of material listing changes: listing identity, title/external SKU,
observed price/status, bounded native status, provenance `MANUAL|IMPORTED|PROVIDER_SYNC`,
provider-observed time, ingestion time, idempotent `ObservationKey` and a safe-fact fingerprint.
It contains no raw provider body. Consecutive identical freshness polls do not append duplicates,
while a material `A -> B -> A` sequence remains visible. Retention is
`commerce.listing_observation_retention_days` (180), always preserving the latest observation,
current listing and audit/link history.

### ProductCommercialProfile (AR), ProductCommercialImage (*E*) and CommercialTag (AR)

`ProductCommercialProfile` has ordinary unique logical `ProductId`, timestamps and `Version`; it
owns the Product's presentation without duplicating name, SKU, active state or recipe.
`ProductCommercialImage` maps that profile to a logical Settings `BrandAssetId`, role
`PRIMARY|GALLERY`, sort order and optional alt text. At most one PRIMARY exists per profile. The
asset must be type `PRODUCT_IMAGE` and uses Settings' validated PNG/JPEG/WebP,
content-addressed/versioned pipeline; Commerce stores no path or bytes.

`CommercialTag` has `Id`, normalized unique `Code`, `Name`, `Active` and `Version`. Composite join
tables assign tags to ProductCommercialProfile (the unique Product presentation) and same-module
MarketplaceListings. Natal, Páscoa and similar concepts are data rows, never hardcoded booleans.
Campaign budgets/windows are out of scope.

### Read compositions and application ports

Commercial Catalog is Product-centric and composes Product, image/tags, offers, current estimated
cost, channel economics and canonical Sale metrics. Sale metrics return nullable units/revenue
plus `SalesMetricCoverage {COMPLETE, PARTIAL, UNKNOWN}` for the requested interval/source set.
Manual marketplace Sales are factual but cannot establish COMPLETE coverage before effective
ORDERS_READ, successful sync and full interval/source coverage; PARTIAL returns known facts and
UNKNOWN returns null rather than authoritative zero. DIRECT rows are authoritative for
transactions recorded in VERCE, not for unrecorded real-world activity. Published Items is
listing-centric, one row per MarketplaceListing. Both are SQL-paginated/filterable/sortable read
models, not duplicated master tables.

Commerce application services depend on provider-neutral ports implemented in the composition
root for Product facts/cost, Pricing fee resolution and Sales aggregates. `IChannelFeeProvider`
returns normalized fee facts and provenance `LIVE_API|CACHE|MANUAL|FALLBACK`; S8B implements only
the local FeeRule adapter (`MANUAL`, or explicit configured `FALLBACK`). Provider HTTP adapters
and a provider-fee cache are deferred until S8C.0 discovery.

S8C.1 freezes only typed registry, authorization and account-inspection ports; resource ports
remain conceptual. Infrastructure owns protected storage and adapter DTOs. Fake code lives in
test-support/harness only. IChannelFeeProvider, ChannelFeeQuote and LocalFeeRuleProvider remain
unchanged in S8C.1; S8C.4 must design account-aware live quote input/output, local IDs, fallback
and truthful provenance before any provider implementation. No fabricated FeeRule IDs.

Mutable roots use expected `Version` and the existing audit infrastructure. Permissions are
`commerce:read` (Owner/Operator/Viewer), `commerce:manage` (Owner/Operator) and
`commerce:accounts:manage` (Owner).

---

## 15. Domain events

**Synchronous** (`IDomainEvent`) — dispatched between save waves inside the business
transaction; the effect must be atomic with the cause. Handlers use the ambient `DbContext`,
never their own, and may not perform I/O
([ADR-0012 Part I](architecture/ADR-0012-domain-events-and-outbox.md)):

| Event | Raised by | Handled by | Effect |
|---|---|---|---|
| `QuoteCreated` | Quoting | Quoting | status history row |
| `QuoteRevised` | Quoting | Quoting, **Production** | supersede previous revision, status history; set `HasPendingRevision` on the prior approved revision's non-terminal order ([STATE-MACHINES §3.3](STATE-MACHINES.md#33-has_pending_revision-is-advisory--it-never-blocks)) |
| `QuoteSent` | Quoting | Quoting | status history |
| `QuoteApproved` | Quoting | **Production** | create `ProductionOrder` (idempotent); cancel a superseded `QUEUED` order ([STATE-MACHINES §3.0](STATE-MACHINES.md#30-the-complete-matrix)) |
| `QuoteExpired` | Quoting (job) | Quoting, **Production** | status history; re-evaluate `HasPendingRevision` |
| `QuoteCanceled` | Quoting | Quoting, Production | status history; re-evaluate `HasPendingRevision` — it can never cancel an order, since only an `APPROVED` (terminal) revision has one ([ADR-0020 §A.6](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)) |
| `SupplyCostChanged` | Inventory | Inventory | append cost history |
| `FilamentPriceChanged` | Inventory | Inventory | append price history |
| `FilamentLotRegistered` | Inventory | Inventory, Finance | stock IN; optional expense |
| `ProductionOrderCreated` | Production | Production | initial status history |
| `ProductionStarted` / `ProductionCompleted` | Production | Production | timestamps, status history |
| `ActualMaterialRecorded` | Production | Inventory | `InventoryMovement(Consumption)` |
| `SaleCreated` | Sales | — | (reporting reads directly) |

**Integration events** (`IIntegrationEvent`) — written to the outbox in the same transaction,
processed after commit; must not be able to fail the business transaction. A concrete event
implements **either** `IDomainEvent` **or** `IIntegrationEvent`, never both:

`GenerateQuotePdfRequested` → render quote PDF · `ProductionOrderCreated` → render shop-floor
document · `AiInsightRequested` → call OpenAI · `EnergySessionRequested` → poll smart plug.

*(The PDF event was previously written here as `QuoteApproved`, which is the **synchronous**
event in the table above — the same name could not implement both interfaces, as the paragraph
itself requires. [ADR-0012 §1](architecture/ADR-0012-domain-events-and-outbox.md) already names
the integration event `GenerateQuotePdfRequested`; corrected to match.)*

Each carries an `idempotency_key`, because delivery is at-least-once. Document renders key on
`render_request_id`, so a retry cannot produce a second `ISSUED` document
([ADR-0012 §13](architecture/ADR-0012-domain-events-and-outbox.md#22-consumer-idempotency)).

Naming: past tense, aggregate-prefixed. Events carry IDs and the minimum payload, never whole
aggregates.

---

## 16. Invariant summary (the ones that must never be violated)

1. A quote revision's monetary fields never change after creation.
2. A quote number + revision index pair is unique and never reused.
3. Only the current revision of a quote can transition status (except automatic expiration and
   supersession).
4. `commission + margin ≥ 1` is never priced.
5. Every price stored has a retrievable breakdown that reproduces it.
6. A supply/filament price change always appends history; the old value stays resolvable.
7. Fee rule versions never overlap in time for the same rule.
8. Energy tariff versions never overlap in time for the same tariff.
9. One production order per approved quote revision, at most.
10. A PurchaseReceipt `InventoryMovement` produces at most one expense row.
11. Inventory movements are append-only.
12. The AI API key is never in a response, a log, or a plaintext column.
13. A `GeneratedDocument` with `Purpose = ISSUED` is never updated or deleted; re-rendering
    inserts a new row.
14. A brand asset version is immutable and never deleted; replacing an asset creates a version.
15. A document references a brand asset **version**, never an asset, so replacing a logo cannot
    alter an issued document.
16. No template can bind `internal_notes`, a unit cost or a margin field — those paths are
    absent from the `QUOTE` binding catalogue.
17. At most one ChannelOffer exists for one Product × SalesChannel, regardless of status.
18. Commercial activation is derived from active Product + active ChannelOffer; Product carries
    no duplicated commercial-active flag.
19. MarketplaceListing external identity is never reused after deactivation, unlink or error.
20. A listing observation never silently overwrites ChannelOffer intent.
21. Fuzzy or ambiguous listing identity never auto-links a Product or activates an offer.
22. DIRECT never has a fake provider, MarketplaceAccount or MarketplaceListing.
23. Provider capability and account grant remain separate; effective capability is their
    intersection for an active account.
24. Commerce stores no raw credential and no arbitrary provider payload.
25. Missing energy, shipping, stock, WIP or actual-cost facts are unavailable/partial, never
    fabricated as zero.
