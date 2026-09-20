# ADR-0019 — S5 Product Recipe Persistence and Pricing Engine Scope

- **Status:** Accepted (pending external review) · **revised 2026-09-19 (twice)**
- **Date:** 2026-09-19
- **Sprint:** S5

> **Correction 2 (2026-09-19, Terra S5 focused re-review — `VERCE3D-S5-B04R-N04-FINAL-CORRECTION-001`).**
> Terra's focused re-review of Correction 1 (below) found the B-04 fix itself was incomplete, plus
> one more non-blocking gap. See §5.6-§5.7.
> 6. **B-04's "already referenced" check was `HashSet<SupplyId>.Contains`, not cardinality.**
>    Duplicate lines against the same Supply are deliberately legal (§1), so a `Contains` check
>    lets a submission with MORE lines against an inactive Supply than existed slip through —
>    every individual line "existed before" as far as `Contains` can tell. Fixed: for an inactive
>    Supply, `submittedCount` may never exceed `existingCount` (per-Supply cardinality, not set
>    membership).
> 7. **A non-canonical channel could be created or converted to `Kind = Direct`.** §5.5 only froze
>    the canonical "DIRECT" row's own `Kind`; nothing stopped a second channel (e.g. "SHOP") from
>    also being `Kind = Direct`, producing an undefined second "degenerate zero-fee case." Fixed:
>    the Code/Kind pairing is now bidirectional — `Code == "DIRECT" ⇔ Kind == Direct` — enforced in
>    `SalesChannel.UpdateDetails`, reached by both construction and update.

> **Correction (2026-09-19, Terra S5 independent review — `VERCE3D-S5-CORRECTIONS-MACRO-001`).**
> Terra's independent review found four blocking defects and two non-blocking gaps in this
> sprint's implementation of the decisions below. None of them changes §1-§4's decisions; all are
> corrections to how faithfully the code implemented them, recorded here per this ADR index's own
> rule 1 (a factually-wrong ADR, or one whose implementation contradicted it, is corrected in
> place with a dated note). See §5 for the full detail.
> 1. **NINETY_NINE was not actually a ceiling.** `Math.Floor(rawPrice) + 0.99m` alone reduces a
>    price below its raw value whenever the fractional part exceeds `.99` (e.g. `40.995 → 40.99`,
>    a REDUCTION) — contradicting §4's own stated rule. Fixed to a true ceiling.
> 2. **FeeRuleVersion applicability used `clock.UtcNow.Date`, not the organization business
>    date.** Wrong near UTC midnight for America/Sao_Paulo (ADR-0004's own established source of
>    "today"). Fixed to `IClock.OrganizationToday()`.
> 3. **The Product Pricing response was the ad-hoc calculator's own generic DTO.** It could not
>    serve as an S6 snapshot source without a second query to re-derive which FeeRuleVersion or
>    rounding policy produced it. A dedicated `ProductPriceResponse` now carries every identity
>    (Product, SalesChannel, FeeRule, FeeRuleVersion) and driver the calculation actually used.
> 4. **A Recipe write accepted a newly-introduced inactive Supply.** §1's own generic-material-line
>    design never stated an activity rule; the gap let a write silently reference a Supply the
>    active-only UI picker could never have offered. Fixed: an inactive Supply may remain in an
>    *existing* recipe line, never be newly introduced by a write.
> 5. **The seeded Direct channel had no protection against gaining real commercial terms.** A
>    configuration user could add a non-zero `FeeRuleVersion` to "Venda Direta," silently turning
>    the zero-fee degenerate case ADR-0005 §2 describes into a fee-bearing one. Fixed: a
>    `SalesChannelKind.Direct` channel's `FeeRule.AddVersion` now rejects non-zero terms, and the
>    seeded channel's `Kind` is frozen.

> **Supersession.** This ADR narrows ADR-0005 for S5: `FeeRule.AppliesTo`/`TargetId`/`Priority`
> and `PriceBracket` are **deferred to S8**, exactly as CALCULATION-RULES §CR-07.5 already states
> ("bracket resolution — not used before S8"). It also narrows DOMAIN-MODEL.md §4's pre-S4
> `Product`/`ProductRecipe` shape, which predates S4's Supply-based inventory model and referred
> to a `ProductFilamentComponent`/`ProductSupplyComponent` split and a multi-revision recipe
> history that no S5 consumer (Quote/Production, both S6+) exists yet to require.

## Context

S5 must add a persistent Product catalog, a Product Recipe (BOM), current-cost calculation and
Direct/marketplace pricing — usable by the future S6 Quote Engine without implementing S6 itself.
Three scope questions had to be resolved before writing a single line of persistence code:

1. Does the recipe need multi-revision history now, or is "one current recipe per product"
   enough until a real consumer (a Quote snapshot, a Production order) needs to freeze one?
2. Does Pricing need scoped/prioritized fee rules and price brackets now, or is "one fee rule per
   channel, flat commission + fixed fee" enough until a real marketplace requires brackets?
3. Exactly how does the rounding policy apply — to the pre-rounded suggested price, or to the raw
   unrounded price? CALCULATION-RULES' own prose (CR-07.2) and worked table (CR-07.4) read as
   contradictory on this point.

## Decision

### 1. Product Recipe is "current state," not a version chain — yet

`Product` is an `AggregateRoot` with exactly one `ProductRecipe` (1:1, `HasOne().WithOne()`).
`ProductRecipe` carries `RevisionNumber`, fixed at `1` for every S5 recipe — a forward-compatible
placeholder field, not a working revision chain. There is no `IsCurrent` flag, no history table,
and editing a recipe replaces its material/additional-cost lines in place
(`ReplaceMaterialLines`/`ReplaceAdditionalCostLines`) rather than forking a new row.

This is deliberate, not an oversight: nothing in S5's scope reads a *historical* recipe revision.
The only two future consumers that will (a QuoteRevision snapshotting the recipe it priced, and a
ProductionOrder pointing at "the exact recipe used") are S6 and S9 respectively, and their
invariants — what triggers a fork, what "current" means once a quote exists, how a fork
interacts with an in-flight quote — are not yet known. Building the fork mechanism now, against
guessed invariants, is the same mistake ADR-0018 already named for a persisted `CostExperiment`:
"save/clone/history can be introduced with a separately designed aggregate when a product/recipe
lifecycle requires it." `RevisionNumber` is kept in the schema precisely so that day's migration
is additive (a fork bumps the number and rows a new `ProductRecipe`), never a breaking rename.

Recipe material lines are **generic** `ProductRecipeMaterialLine` rows referencing `SupplyId`
(no FK — Catalog does not reference Inventory's assembly; see §2 below), not the pre-S4
`ProductFilamentComponent`/`ProductSupplyComponent` split from DOMAIN-MODEL.md §4. S4 replaced
"Filament" as a first-class concept with `Supply` + `SupplyCategory` (ADR-0017); a recipe line is
one generic shape whether the underlying supply is a filament, a resin or packaging. Duplicate
`SupplyId` lines in one recipe are deliberately allowed, matching the existing S4 `CostEngine`
behavior of treating repeated material lines as independent.

### 2. Catalog never references Inventory or Costing; the API composition root does

`ProductRecipeMaterialLine` stores `SupplyId: Guid` and the entered unit as a plain string, with
no foreign key and no dependency on `Verce.Modules.Inventory`. Unit normalization
(`SupplyUnitConversion.NormalizePositive`) and Supply-existence validation happen in
`Verce.Api.Catalog.ProductEndpoints`, the composition root, exactly as ADR-0001 §3.1 requires —
never inside the pure `Product`/`ProductRecipe` aggregate. `ProductCostCalculator` (also in the
API layer) is the sole translator from a persisted `ProductRecipe` into S4's
`CostCalculationInput`, and calls the unmodified `Verce.Modules.Costing.CostEngine.Calculate` —
S5 does not reimplement wastage, weighted-average acquisition cost, labor, machine or additional
direct cost math. `ProductCostCalculator` throws `RECIPE_EMPTY` for a recipe with no materials,
labor or machine time, and `RECIPE_MACHINE_RATE_REQUIRED` when machine minutes are set without an
hourly rate — both are Catalog-layer request-shape errors, not new CostEngine rules.

### 3. FeeRule is flat for S5; scope, priority and brackets are S8

`FeeRule` has exactly one row per `SalesChannel` (a unique index on `sales_channel_id`), and
carries only `Name`/`Active` plus its `FeeRuleVersion` children — no `AppliesTo`, `TargetId` or
`Priority`. `FeeRuleVersion` keeps `ValidFrom`/`ValidUntil` (half-open, exclusion-constrained per
ADR-0005), `CommissionPercent`, `FixedFee`, `FixedFeeApplication`, `MinimumFee`/`MaximumFee` and
`Notes` — no `PriceBracket` and no reserved `ShippingComponent`. This is not a new decision so
much as making S5's implementation match what CALCULATION-RULES already specified: CR-07.5 names
bracket resolution explicitly "not used before S8," and per-product/per-category fee scoping has
no consumer before Quote line items exist. `SalesChannel` itself keeps `DefaultMarginPercent` as
an ADR-0002 **fraction** (`0.18` = 18%) — Pricing does **not** inherit ADR-0018's percentage-point
exception, which is scoped to `costing.default_wastage_rate` alone.

The seeded "Venda Direta" channel (`Code = "DIRECT"`, `Kind = Direct`) gets a mandatory zero-fee
`FeeRuleVersion` (`ValidFrom = 2020-01-01`, `ValidUntil = null`, commission `0`, fixed fee `0`)
at first boot, gated the same way `InventorySeedService`/`SettingsSeedService` gate — only when
`Settings:SeedOnStartup` is enabled and no migration is pending. Direct sale resolves through the
exact same `FeeRule.ResolveVersionAt` path as any marketplace; there is still no
`if (channel == "Direct")` anywhere (ADR-0005's core rule, unchanged).

### 4. Rounding policy applies to the raw price, not the pre-rounded one

CR-07.2's prose describes rounding to two decimals and then applying the policy; CR-07.4's own
worked table shows the `NONE` policy truncating `40,5187234...` to `40,51` — the *raw*, unrounded
value — not the pre-rounded `40,52`. `PricingEngine.ApplyRoundingPolicy` resolves this by applying
every policy (`CENT`, `TEN_CENTS`, `WHOLE`, `NINETY_NINE`, `NONE`) directly to `rawPrice`, which
reproduces all five of CR-07.4's documented values exactly (see
`Verce.Pricing.Tests.PricingEngineTests.P8_rounding_policy_matches_CALCULATION_RULES_CR_07_4_worked_table`).
The worked table is treated as the more concrete, testable evidence of intent; CR-07.2's prose
should be read as informally describing the same outcome, not a second, competing algorithm.

## Alternatives considered

- **Versioned `ProductRecipe` with `IsCurrent`, per DOMAIN-MODEL.md's pre-S4 shape:** rejected for
  S5 — no consumer needs it yet, and guessing the fork trigger now risks a shape that S6/S9 must
  then unwind. `RevisionNumber` is kept exactly so this is additive later.
- **`FeeRule.AppliesTo`/`Priority`/`PriceBracket` implemented now, unused:** rejected — untested,
  unused branching is a liability, not a hedge; CR-07.5 already reserves this for S8.
- **Percentage-point Pricing fractions (mirroring ADR-0018):** rejected — ADR-0018's exception is
  narrow and named for one Costing setting; extending it to Pricing would silently make
  `SalesChannel.DefaultMarginPercent` and `FeeRuleVersion.CommissionPercent` inconsistent with
  every other ADR-0002 percentage in the system.
- **Rounding policy applied to the pre-rounded 2dp price:** rejected — contradicts CR-07.4's own
  worked numbers for `NONE`, `TEN_CENTS` and `WHOLE`.

## Consequences

A Product's cost is always "current cost," and a Pricing calculation is always "current price" —
there is no S5 concept of "the price this quote was actually issued at." That immutability
belongs to S6's `QuoteRevision` snapshot (ADR-0003/ADR-0004), which will read `Product`/`Recipe`/
`PricingEngine` output at issue time and freeze it into its own row; S5 deliberately builds
nothing that pretends to be that snapshot. A future S8 sprint that adds `PriceBracket` or
per-category `FeeRule` scoping is a pure additive migration, not a breaking rename, because the
flat shape chosen here is a strict subset of DOMAIN-MODEL.md's original design. Marketplace
onboarding (a new channel, a new fee rule, a new time-boxed version) requires zero code changes,
matching ADR-0005's original intent.

## Compliance checks

- `Verce.Catalog.Tests`/`Verce.Pricing.Tests` cover construction validation, recipe line
  replacement (including duplicate-Supply lines), fee-rule version window validation and
  resolution-at-an-instant, and the full P1–P10 pricing golden corpus including the CR-07.4
  rounding-policy table.
- `Verce.Architecture.Tests` (`CatalogArchitectureTests`, `PricingArchitectureTests`) prohibit
  Catalog/Pricing from referencing Inventory or Costing module assemblies, prohibit Pricing from
  referencing ASP.NET Core, and assert `PricingEngine.Calculate` takes only its resolved input
  record.
- `Verce.IntegrationTests` (`Catalog/ProductHttpIntegrationTests`, `Pricing/PricingHttpIntegrationTests`)
  prove the persisted recipe drives `CostEngine` output byte-for-byte identically to a manual S4
  Cost Laboratory calculation with the same inputs, that a calculation leaves Supply stock/version
  untouched, and that fee-rule version overlap is rejected by the database exclusion constraint.

## §5 — Terra S5 corrections (2026-09-19)

### 5.1 NINETY_NINE is a true ceiling, not floor+0.99

CR-07.4 always described `NINETY_NINE` as rounding "up, never down" to the next commercial
`N.99`. The code did not implement that: `Math.Floor(rawPrice) + 0.99m` is only a ceiling when
`rawPrice`'s fractional part is `≤ .99` — which is every fractional value, EXCEPT there is no such
case in ordinary decimal arithmetic... except there is: a raw price like `40.995` has fractional
part `.995 > .99`, so `floor(40.995) + 0.99 = 40.99`, which is **less than** `40.995`. The fix
computes the same candidate and, if it still falls short of the raw price, uses the *next*
integer's `.99` instead:

```text
candidate = floor(rawPrice) + 0.99
result    = candidate < rawPrice ? candidate + 1 : candidate
```

This reproduces every value in CR-07.4's own table unchanged and additionally guarantees
`result >= rawPrice` for every input — proven by
`PricingEngineTests.P8b_NINETY_NINE_never_rounds_below_the_raw_price` (11 boundary cases) and
`P8c_NINETY_NINE_ceiling_invariant_holds_across_several_integer_ranges`.

### 5.2 FeeRuleVersion resolution uses the organization business date

`POST /api/pricing/products/{id}/price` resolved "today" as `DateOnly.FromDateTime(clock.UtcNow.Date)`
— the UTC calendar date. ADR-0004 already established `IClock.OrganizationToday()`
(America/Sao_Paulo, no DST since 2019) as the one legitimate source of "today" anywhere validity
windows or business dates matter; this endpoint simply hadn't been written against it. Near UTC
midnight, `UtcNow.Date` and `OrganizationToday()` disagree by exactly one day for roughly three
hours (BRT is UTC−3), which could resolve the wrong `FeeRuleVersion` at a fee-rule boundary. Fixed
to call `OrganizationToday()` once per request and reuse that single value everywhere the request
needs "today" (never re-queried, so the response in §5.3 is guaranteed self-consistent). Proven at
a real UTC/BRT calendar boundary — not just a unit test of the clock helper — by
`PricingOrganizationDateHttpIntegrationTests`, which fixes the clock to `2026-06-15T01:30:00Z`
(UTC date June 15, organization date June 14) and shows the endpoint resolves the version valid on
June 14.

### 5.3 Product Pricing returns a self-describing snapshot contract

`POST /api/pricing/products/{id}/price` returned `PricingCalculateResponse` — the SAME shape the
ad-hoc `/api/pricing/calculate` calculator returns, which carries no identity at all (not even
which Product or channel produced it). A future S6 `QuoteRevision` needs to freeze exactly which
`FeeRuleVersion` and rounding policy priced a line, and re-deriving that from a second query after
the fact risks describing a *different*, possibly-since-changed resolution than the one that
actually ran. The endpoint now returns a dedicated `ProductPriceResponse`
(`src/Verce.Api/Pricing/PricingEndpoints.cs`) built from the exact same resolved `SalesChannel`/
`FeeRule`/`FeeRuleVersion`/`PricingCalculationResult` values used to compute the price — never a
second, independently-resolved "current" lookup:

`ProductId/Code/Name`, `UnitTotalCost`, `SalesChannelId/Code/Kind/Name`, `DesiredMargin`,
`FeeRuleId`, `FeeRuleVersionId`, `CommissionPercent`, `FixedFee`, `OrganizationDate`,
`Denominator`, `RawPrice`, `RoundingPolicy`, `SuggestedPrice`, `CommissionAmount`,
`FeeClampApplied`, `Warnings`.

`PricingCalculateResponse` (the ad-hoc calculator's response) is unchanged and unrelated — mixing
the two would have blurred "a real Product/channel decision" with "a what-if calculator input,"
exactly the ambiguity a future snapshot consumer must not have to untangle.

### 5.4 A Recipe write may never increase how many lines reference an inactive Supply

§1 already established that Catalog's `ProductRecipeMaterialLine` is a generic reference with no
FK to Inventory, resolved and validated in the API composition root. It never stated an activity
rule, and the gap meant a recipe write would silently accept a Supply the UI's active-only picker
could never have offered. The frozen rule: an inactive Supply may remain in an **existing** recipe
line (reading, editing an unrelated field, or recalculating cost never cares that a referenced
Supply later went inactive — matching S4's own inactive-Supply consistency stance), but a write
may never **increase** how many lines reference one. See §5.6 for why this is a cardinality rule,
not a set-membership check — `ProductEndpoints`'s recipe-update handler loads the existing recipe
first and rejects with `SUPPLY_INACTIVE` (422) whenever a submission's per-Supply line count for
an inactive Supply exceeds the persisted recipe's own count for that Supply.

### 5.5 The Direct channel cannot gain real commercial terms

ADR-0005 §2 and this ADR's own §3 both describe `Kind = Direct` as the zero-fee degenerate case of
the one marketplace formula — never a special code path. Nothing enforced that a `FeeRuleVersion`
added to a Direct-kind channel actually stayed zero-fee, so an Owner could configure the seeded
"Venda Direta" channel with a real commission or fixed fee, silently turning Direct pricing into
marketplace pricing while the engine still called it Direct. `FeeRule.AddVersion` now takes the
owning channel's `SalesChannelKind` (default `Marketplace`, so every pre-existing call site is
unaffected) and rejects non-zero `commissionPercent`/`fixedFee` when it is `Direct`
(`DIRECT_CHANNEL_FEES_NOT_ALLOWED`). Separately, `SalesChannel.DirectChannelCode` ("DIRECT")
identifies the one seeded system channel, and `SalesChannel.UpdateDetails` rejects changing its
`Kind` away from `Direct` (`DIRECT_CHANNEL_KIND_IMMUTABLE`) — its `Code` was already immutable
(no endpoint ever accepts a `Code` change).

### 5.6 Inactive-Supply preservation is per-Supply cardinality, not set membership

§5.4's original fix compared the submitted recipe's Supply IDs against a `HashSet<Guid>` built
from the persisted recipe's Supply IDs: an inactive Supply was allowed if its ID was already in
that set. This is wrong precisely because §1 makes duplicate lines against the same Supply legal:
`Contains` answers "has this Supply ever appeared in this recipe," not "does this submission
introduce a NEW line against it." A persisted recipe with exactly one line against inactive Supply
X, resubmitted with TWO lines against X, passes a `Contains` check trivially — both lines "existed
before" as far as set membership can tell — yet the second line is a genuinely new inactive
reference the invariant must reject.

The frozen rule is per-Supply **cardinality**: group the persisted recipe's material lines by
`SupplyId` into `existingCount`, group the submitted lines the same way into `submittedCount`, and
for every inactive Supply require `submittedCount ≤ existingCount`. Active Supplies are entirely
unrestricted (duplicates remain legal there, per §1). This is computed in memory from two
`GroupBy` calls, not a per-line database query, and needs no change to how Supplies are looked up
(the existing single batched `IN (...)` query is enough). Removing an inactive reference, or
holding its count steady, both remain legal; only an *increase* rejects with `SUPPLY_INACTIVE`
(422) — the same stable code as before.

### 5.7 DIRECT identity is a reserved, bidirectional Code/Kind pair

§5.5 froze the canonical "DIRECT" row's own `Kind`, but said nothing about any OTHER channel's
`Kind`. Nothing stopped an Owner from creating a second channel (e.g. `Code = "SHOP"`) with
`Kind = Direct`, or converting an existing Marketplace channel to `Kind = Direct` — producing an
undefined second "degenerate zero-fee case" alongside the real one, which `FeeRule.AddVersion`'s
§5.5 protection would then ALSO apply to (since it keys off `SalesChannelKind`, not `Code`),
silently multiplying which channel "is" Direct sale. The frozen rule is now bidirectional and
lives in the one place both the constructor and the update path already funnel through
(`SalesChannel.UpdateDetails`, since the constructor calls it after assigning `Code`):

```text
Code == "DIRECT"  ⇔  Kind == Direct
```

`Code == "DIRECT" ∧ Kind ≠ Direct` → `DIRECT_CHANNEL_KIND_IMMUTABLE` (§5.5, unchanged).
`Code ≠ "DIRECT" ∧ Kind == Direct` → `DIRECT_CHANNEL_IDENTITY_RESERVED` (new). Marketplace and
Other channels are otherwise unaffected — they may still freely change between those two kinds.
