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

### 3.7 Purchase receipt cost is informational only
`PurchaseReceipt` captures `UnitCostSnapshot`/`TotalCostSnapshot` (per base unit, converted
alongside the quantity) purely as history — "what did the last purchase cost." This is **not**
S4's actual costing policy (FIFO/LIFO/moving-average): that choice remains open, now scoped to all
of Inventory rather than filament alone (see [DATA-DICTIONARY §5](DATA-DICTIONARY.md#purchase-cost-policy-deferred-to-s4)).

> **Forward-reference note.** Sections below this point (Catalog, Costing, Production, Finance,
> §15 Domain events) were drafted before S3 and still reference `FilamentId`/`FilamentLot`/
> `SupplyLot`/`StockMovement`/`MaterialKind`, none of which exist anymore. Every such reference is
> **stale** and must be reconciled by the sprint that actually builds that module, against `Supply`
> (optionally carrying `FilamentDetails`) and `InventoryMovement` (§3 above) — see
> [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md). Left as a note
> rather than redesigned now, which is out of S3's scope.

---

## 4. Catalog module

### Product (AR)
`Id`, `Sku?` (unique when present), `Name`, `Description?`, `CategoryId?`, `ImagePath?`,
`DefaultSalesChannelId?`, `DefaultMarginPercent: Percent?`, `IsActive`, `DeletedAt?`,
`CurrentRecipeId?`.

### ProductRecipe (*E*, versioned)
`Id`, `ProductId`, `RevisionNumber` (1..N), `IsCurrent`, `EffectiveFrom`, `Notes?`, plus
process parameters:

- `PrintDurationSeconds: DurationSeconds`
- `MachineId?` (Energy module reference by ID)
- `EstimatedEnergyKwh: Kwh?` — when set, overrides the machine-power estimate
- `LaborMinutes: int`
- `LaborHourlyRate: Money?` — null means "use the organization default"
- `WastageRate: Percent` (default 0)
- `PostProcessingNotes?`

Components (all *E*, all 0..N, **no maximum count**):

| Component | Fields |
|---|---|
| `ProductFilamentComponent` | `FilamentId`, `GramsUsed: Grams`, `AllowSubstitution: bool`, `Note?`, `SortOrder` |
| `ProductSupplyComponent` | `SupplyId`, `Quantity`, `Note?`, `SortOrder` |
| `ProductCostLine` | `Description`, `Amount: Money`, `CostKind {MANUAL, OUTSOURCED, OTHER}`, `SortOrder` |

Packaging is **not** a separate concept: it is a `ProductSupplyComponent` pointing at a supply
whose category kind is `PACKAGING`. The cost breakdown groups it separately by that flag, so
the report dimension exists without a redundant model.

Invariants:
- A recipe is **immutable once it has been used** by a quote snapshot or a production order.
  Editing a used recipe creates `RevisionNumber + 1` and flips `IsCurrent`.
- Exactly one `IsCurrent` recipe per product.
- `GramsUsed > 0`, `Quantity > 0`, `Amount ≥ 0`.
- A recipe may have zero components (a pure-labor or pure-manual-cost product is legal).
- Referenced filaments/supplies must be active at the moment the recipe revision is created.

Why versioned even though quotes snapshot everything: production orders and the shop-floor
document need to point at *the exact recipe used*, and the operator needs to compare recipe
revisions over time. Versioning is cheap; recovering it later is not.

---

## 5. Energy module

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

Implementations: `EstimatedEnergyProvider` (power × time, S4), `ManualEnergyProvider`
(operator types the kWh, S10), `SmartPlugEnergyProvider` (S10+, device not chosen — **no
vendor-specific code may be written before the device is selected**). Registration is by
`Kind`; the resolver picks the provider configured per machine, falling back to estimated.

---

## 6. Costing module

### CostEngine (domain service, pure)
Input `CostInput` → output `CostBreakdown`. See [CALCULATION-RULES](CALCULATION-RULES.md).

`CostInput` is a fully-resolved record: filament components with `GramsUsed` **and**
`PricePerKgAtInstant`, supply components with `Quantity` **and** `UnitCostAtInstant`, manual
lines, machine hourly rate, tariff price per kWh, labor rate, wastage rate, print duration,
energy kWh (or the parameters to estimate it), and `Quantity` of units.

`CostBreakdown` is a `VO` tree:
```
CostBreakdown
├── FilamentComponents[]  (filamentId, name, grams, pricePerKg, cost)
├── SupplyComponents[]    (supplyId, name, quantity, unit, unitCost, cost, isPackaging)
├── ManualLines[]         (description, amount)
├── Energy                (kwh, pricePerKwh, tariffVersionId, source, cost)
├── Machine               (hours, hourlyRate, cost)
├── Labor                 (minutes, hourlyRate, cost)
├── Wastage               (base, rate, cost)
├── Totals                (filamentCost, supplyCost, packagingCost, manualCost,
│                          directCost, wastageCost, unitTotalCost, batchTotalCost)
└── EngineVersion
```

### CostExperiment (AR) — the Laboratory
`Id`, `Name`, `Description?`, `Status {DRAFT, SAVED, CONVERTED, ARCHIVED}`, `CreatedBy`,
process parameters (same shape as a recipe: duration, machine, labor, wastage, energy),
`ResultSnapshot: jsonb` (a `CostBreakdown`), `ResultTotalCost: Money`,
`SourceExperimentId?` (set when cloned), `ConvertedToProductId?`, `ConvertedAt?`.

Children: `CostExperimentComponent` (*E*) with
`Kind {FILAMENT, SUPPLY, MANUAL}` + the corresponding fields — filament components carry
`FilamentId` + `GramsUsed`, supply components carry `SupplyId` + `Quantity`, manual lines
carry `Description` + `Amount`. Components may also be **ad-hoc**: a filament component may
instead carry a free `PricePerKg` with no `FilamentId`, so an operator can simulate a material
not yet registered. That is the point of a laboratory.

Behaviours: `Recalculate()`, `Clone()`, `ConvertToProduct()` (creates a `Product` +
`ProductRecipe` revision 1 from the experiment; ad-hoc components must be resolved to real
master data first, or conversion is rejected with a listed reason).

An experiment has **no commercial effect**: it creates no quote, no sale, no stock movement.

---

## 7. Pricing module

### SalesChannel (AR)
`Id`, `Name`, `Kind {DIRECT, MARKETPLACE, OTHER}`, `Code?`, `IsActive`, `DeletedAt?`,
`DefaultMarginPercent: Percent?`, `Notes?`.

"Venda Direta" is a normal channel whose fee rule is 0% + R$ 0,00. **There is no special case
in the engine** — direct sale is the degenerate marketplace. This is deliberate: one formula,
one code path, one set of tests ([ADR-0005](architecture/ADR-0005-marketplace-fee-rules.md)).

### FeeRule (AR) + FeeRuleVersion (*E*) + PriceBracket (*E*)

`FeeRule`: `Id`, `SalesChannelId`, `Name`, `AppliesTo {ALL_PRODUCTS, PRODUCT_CATEGORY, PRODUCT}`,
`TargetId?`, `Priority: int`, `IsActive`.

`FeeRuleVersion`: `Id`, `FeeRuleId`, `ValidFrom: DateOnly`, `ValidUntil: DateOnly?`,
`CommissionPercent: Percent`, `FixedFee: Money`,
`FixedFeeApplication {PER_UNIT, PER_ORDER}` (default `PER_UNIT`, see
[CR-07.3](CALCULATION-RULES.md#cr-073--fixed-fee-application)),
`MinimumFee: Money?`, `MaximumFee: Money?`, `ShippingComponent: Money?` (reserved, S8+), `Notes?`.

`PriceBracket`: `Id`, `FeeRuleVersionId`, `MinPrice: Money`, `MaxPrice: Money?`,
`CommissionPercent`, `FixedFee`, `MinimumFee?`, `MaximumFee?`, `SortOrder`.
Zero brackets = the version's own flat values apply at every price.

Invariants:
- Versions of the same rule must not overlap in time (database exclusion constraint).
- Brackets within a version must not overlap and must be contiguous from the lowest `MinPrice`.
- `CommissionPercent ∈ [0, 1)`.
- `MinimumFee ≤ MaximumFee` when both present.

Resolution: given `(salesChannelId, productId, instant, price)` the `FeeRuleResolver` picks the
highest-`Priority` active rule matching the product scope, then its version valid at `instant`,
then its bracket for `price`. The bracket/price circularity is resolved by the algorithm in
[CALCULATION-RULES §7.3](CALCULATION-RULES.md#cr-075--bracket-resolution-fee-depends-on-price-price-depends-on-fee).

### PricingEngine (domain service, pure)
Input: `unitTotalCost`, `commissionPercent`, `fixedFee`, `desiredMargin`, `roundingPolicy`,
optional `finalPriceOverride`. Output: `PriceBreakdown` with cost, fixed fee, commission
amount, desired margin, suggested price, final price, expected profit, effective margin, and
the ordered list of steps that produced it. Rejects invalid denominators — never returns a
number computed from a non-positive denominator.

---

## 8. Quoting module

### Quote (AR — the container)
`Id`, `Number: QuoteNumber` (date + sequence, **without** revision suffix),
`CustomerId?`, `CurrentRevisionId`, `CreatedAt`, `CreatedBy`.

The `Quote` holds identity and the pointer to the current revision. It holds **no** prices.

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
`SalesChannelId` (item-level channel override; defaults to the revision channel),
`ExpectedProfitAmount`, `EffectiveMarginPercent`, `SortOrder`.

An item may be **ad-hoc** (no `ProductId`): a free description plus a manual cost. The
laboratory and quick quotes need this.

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
`Id`, `SaleNumber`, `CustomerId?`, `SalesChannelId`, `QuoteRevisionId?`, `SoldAt`,
`Status {CONFIRMED, CANCELED}`, `GrossAmount`, `DiscountAmount`, `NetAmount`,
`ChannelFeeAmount`, `ShippingAmount`, `TotalCostAmount`, `GrossProfitAmount`,
`EffectiveMarginPercent`, `ExternalOrderCode?`, `Notes?`.

### SaleItem (*E*)
`Id`, `SaleId`, `LineNumber`, `ProductId?`, `ProductNameSnapshot`, `Quantity`, `UnitPrice`,
`DiscountAmount`, `LineTotalAmount`, `UnitCostAmount`, `LineCostAmount`,
`ChannelFeeAmount`, `GrossProfitAmount`, `QuoteItemId?`.

### Quote versus Sale — the boundary

| | Quote | Sale |
|---|---|---|
| Nature | **Commercial proposal** | **Financial fact** |
| Answers | "What would this cost the customer?" | "What did we actually earn?" |
| Created | Before the deal | When the deal closes |
| Mutable | No (revisions) | Status only |
| Cost figure | Estimated, snapshotted | Best-known cost: actual when reconciled, else the snapshot |
| Reports | Conversion, pipeline | Revenue, profit, margin |

A sale created from an approved quote revision **copies the priced lines** (they are already
frozen) and **references** the revision. It does not re-run pricing. A sale may also be
created standalone (a walk-in sale with no quote) — the same table, `QuoteRevisionId` null.

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
- `ProductionOrderItemActualMaterial` (*E*) — what was actually consumed:
  `MaterialKind`, `MaterialId`, `ActualQuantity`, `UnitCostAtConsumption`, `RecordedAt`,
  `RecordedBy`. Recording emits `StockMovement(OUT)`.
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
`FilamentLotId?`, `SupplyLotId?`, `MachineId?`, `Notes?`, `AttachmentPath?`.

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
2. Registering a `FilamentLot` or `SupplyLot` may create the linked `Expense` automatically
   (treatment forced to `INVENTORY_PURCHASE`). A lot has **at most one** expense
   (`unique(filament_lot_id)`, `unique(supply_lot_id)` on `expense`), so the same purchase can
   never be entered twice through the two doors.
3. Expenses whose treatment is `ASSET_ACQUISITION` (a printer) are excluded from the
   operational result too; they reach it through `MachineHourlyRate` depreciation.
4. Only `OPERATING_EXPENSE` rows enter "Custos/Despesas do mês" in the dashboard.
5. Energy is the sharp edge: if the electricity bill is registered as an `OPERATING_EXPENSE`
   **and** energy cost is inside product cost, the profit report double counts. The resolution
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
`PRIMARY_LOGO`, `COMPACT_LOGO`, `NEGATIVE_LOGO`, `SYMBOL`, `FAVICON`, `DOCUMENT_LOGO`, `OTHER`.
Adding a type is data.

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
| `costing.default_wastage_rate` | `0.00` | Costing |
| `energy.default_tariff_id` | seeded | Energy |
| `energy.overhead_factor` | `0.00` | Energy |
| `inventory.purchase_cost_policy` | *(not yet seeded — open decision, deferred to S4)* | Inventory/Costing |
| `ui.default_theme` | `verce-default` | Frontend |
| `branding.product_name` | `VERCE 3D` | Frontend, documents |
| `branding.product_subtitle` | `Laboratório de Custos` | Frontend |
| `documents.default_payment_terms` | (empty) | Quoting — copied onto each revision |
| `documents.default_delivery_terms` | (empty) | Quoting — copied onto each revision |
| `documents.default_warranty` | (empty) | Quoting — copied onto each revision |
| `documents.preview_retention_days` | `30` | Documents |
| `uploads.max_image_bytes` | `5242880` | Settings |

`timezone` and `currency` are **not** app settings — they are fields on `CompanyProfile`,
because they are organization facts rather than tunable behaviour.

---

## 15. Domain events

**Synchronous** (`IDomainEvent`) — dispatched between save waves inside the business
transaction; the effect must be atomic with the cause. Handlers use the ambient `DbContext`,
never their own, and may not perform I/O
([ADR-0012 Part I](architecture/ADR-0012-domain-events-and-outbox.md)):

| Event | Raised by | Handled by | Effect |
|---|---|---|---|
| `QuoteCreated` | Quoting | Quoting | status history row |
| `QuoteRevised` | Quoting | Quoting | supersede previous revision, status history |
| `QuoteSent` | Quoting | Quoting | status history |
| `QuoteApproved` | Quoting | **Production** | create `ProductionOrder` (idempotent) |
| `QuoteExpired` | Quoting (job) | Quoting | status history |
| `QuoteCanceled` | Quoting | Quoting, Production | status history; block queued order |
| `SupplyCostChanged` | Inventory | Inventory | append cost history |
| `FilamentPriceChanged` | Inventory | Inventory | append price history |
| `FilamentLotRegistered` | Inventory | Inventory, Finance | stock IN; optional expense |
| `ProductionOrderCreated` | Production | Production | initial status history |
| `ProductionStarted` / `ProductionCompleted` | Production | Production | timestamps, status history |
| `ActualMaterialRecorded` | Production | Inventory | stock OUT movement |
| `SaleCreated` | Sales | — | (reporting reads directly) |

**Integration events** (`IIntegrationEvent`) — written to the outbox in the same transaction,
processed after commit; must not be able to fail the business transaction. A concrete event
implements **either** `IDomainEvent` **or** `IIntegrationEvent`, never both:

`QuoteApproved` → render quote PDF · `ProductionOrderCreated` → render shop-floor document ·
`AiInsightRequested` → call OpenAI · `EnergySessionRequested` → poll smart plug.

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
10. A lot purchase produces at most one expense row.
11. Stock movements are append-only.
12. The AI API key is never in a response, a log, or a plaintext column.
13. A `GeneratedDocument` with `Purpose = ISSUED` is never updated or deleted; re-rendering
    inserts a new row.
14. A brand asset version is immutable and never deleted; replacing an asset creates a version.
15. A document references a brand asset **version**, never an asset, so replacing a logo cannot
    alter an issued document.
16. No template can bind `internal_notes`, a unit cost or a margin field — those paths are
    absent from the `QUOTE` binding catalogue.
