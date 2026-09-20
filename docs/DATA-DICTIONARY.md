# DATA DICTIONARY — Verce 3D | Laboratório de Custos

Definitions of terms, fields, enumerations and metrics. When a report, a screen label or an AI
prompt uses one of these terms, it means exactly what is written here and nothing else.

---

## 1. Units and their meaning

| Concept | Unit | Stored as | Notes |
|---|---|---|---|
| Money | BRL | `numeric(18,2)` presented, `numeric(18,6)` intermediate | never `float` |
| Percent | fraction | `numeric(9,6)` | `0.175` = 17,5%. The UI multiplies by 100 for display and divides on input. |
| Filament spool weight | gram | `numeric(12,3)` | `Supply.FilamentDetails.SpoolNetWeightGrams`, informational — a filament supply's actual stock is tracked in its own `BaseUnit` (usually `Gram`), not derived from spool weight |
| Filament diameter | mm | `numeric(6,4)` | `Supply.FilamentDetails.DiameterMm` |
| Supply quantity / stock | supply's own `BaseUnit` | `numeric(14,4)` | see `SupplyBaseUnit` below — S3 generalizes this across every supply, not filament grams only ([ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) §4) |
| Purchase / movement cost | R$ per base unit | `numeric(18,6)` snapshot, `numeric(18,2)` total | informational only, not a costing policy — see §5 |
| Energy | kWh | `numeric(12,4)` | |
| Energy tariff | R$/kWh | `numeric(18,6)` | may or may not include taxes — see `includes_taxes` |
| Power | watt | `integer` | |
| Print time | second | `integer` | displayed as `3h25` |
| Labor time | minute | `integer` | |

---

## 2. Core enumerations

### `PersonType`
`INDIVIDUAL` (pessoa física, CPF) · `COMPANY` (pessoa jurídica, CNPJ)

### `SupplyBaseUnit` *(supersedes `SupplyUnit` — [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md))*
`Gram` · `Kilogram` · `Unit` (unidade) · `Milliliter` · `Liter` · `Meter` · `Centimeter`.
Immutable once a supply is created. Only the deterministic pairs kg↔g, L↔mL, m↔cm convert into
each other; `Unit` has no compatible sibling.

### `SupplyCategory` *(reference data — Category 3, textual code PK, supersedes `SupplyCategoryKind`)*
`FILAMENT` · `RESIN` · `PACKAGING` · `HARDWARE` · `ELECTRONICS` · `FINISHING` · `CONSUMABLE` ·
`OTHER` — classification only, no calculation logic. Seeded rows, not a C# enum, so a new
category is data.

### `FilamentMaterialType`
`Pla` · `PlaPlus` · `Petg` · `Abs` · `Asa` · `Tpu` · `Nylon` · `Pc` · `Pva` · `Other` — set only
when `Supply.FilamentDetails` is present.

### `InventoryMovementType` *(supersedes `MaterialKind` / `StockMovementReason`)*
Actively creatable in S3: `InitialBalance` (once) · `PurchaseReceipt` · `ManualIncrease` ·
`ManualDecrease` · `Correction`. Reserved for later modules, not yet creatable by any endpoint:
`Consumption` · `ReturnIn` · `ReturnOut`. There is no separate `MaterialKind` discriminator —
`InventoryMovement` belongs to exactly one `Supply`, and that supply's own `SupplyCategory`
(e.g. `FILAMENT`) is how a report distinguishes filament movements from any other supply's.

### `QuoteStatus`
See [STATE-MACHINES §1](STATE-MACHINES.md#1-quote-revision-status).
`GENERATED` · `SENT` · `NEGOTIATING` · `APPROVED` · `CANCELED` · `EXPIRED` · `SUPERSEDED`

### `ProductionOrderStatus`
`QUEUED` · `IN_PRODUCTION` · `READY` · `SHIPPED` · `DELIVERED` · `CANCELED`

### `SalesChannelKind`
`DIRECT` (venda direta) · `MARKETPLACE` (Shopee, Mercado Livre, …) · `OTHER`

### `FixedFeeApplication`
`PER_UNIT` — the fixed fee is charged for each unit sold (default; matches most marketplaces).
`PER_ORDER` — charged once per order and allocated across units (CR-07.3).

### `EnergySource`
`ESTIMATED` (machine power × time) · `ESTIMATED_OVERRIDE` (kWh typed on the recipe) ·
`MANUAL` (kWh typed after production) · `SMART_PLUG` (measured by device)

### `AccountingTreatment`
| Value | Meaning | Enters "Custos do mês"? |
|---|---|---|
| `OPERATING_EXPENSE` | Consumed in the period (energy bill, marketing, rent) | **yes** |
| `INVENTORY_PURCHASE` | Bought as stock (a filament spool, a bag of screws) | **no** — enters through material cost when consumed |
| `ASSET_ACQUISITION` | Durable equipment (a printer) | **no** — enters through machine hourly rate |

### `CostBasis` (on `sales.sale`)
`ESTIMATED` — cost still comes from the quote snapshot.
`ACTUAL` — every linked production item reported actual consumption.
`MIXED` — some items reconciled, some not.

### `DiscountKind`
`NONE` · `PERCENT` (fraction of the unit price) · `AMOUNT` (absolute R$ per unit)

### `OutboxStatus`
`PENDING` — awaiting `available_at`; **claimable only while `attempt_count < max_attempts`** ·
`PROCESSING` — leased by a worker until `lease_until` ·
`PROCESSED` — consumer succeeded (terminal, prunable after 90 days) ·
`FAILED` — attempts exhausted or non-retryable (terminal until an `Owner` requeues).

`failure_disposition` accompanies `FAILED` and **only** `FAILED`. Two values:
`ACTIVE` (unresolved, counts toward `degraded` health) · `DISMISSED` (a human decided this effect
must never be retried; actor and reason recorded; **not claimable**). Dismissal never deletes the
message or its history.

> There is no `RESOLVED`. A requeued message that succeeds ends at `PROCESSED` with no
> disposition — success *is* resolution.

Operational meanings that matter when reading a row:
- `attempt_count` increments **at claim**, so a crashed worker consumes an attempt. That is
  deliberate: it stops a crash-loop from retrying forever.
- `last_error` is **never cleared**, including on requeue — failure evidence is not erased.
- `attempt_count` **resets to 0 on manual requeue** (a fresh budget); the history it represented
  survives in `outbox_message_attempt`, keyed by generation (below).
- A **retryable** failure on the final permitted attempt is terminal (`FAILED`), exactly like a
  crash on that attempt. One budget rule, both paths.
- A `PROCESSING` row whose `lease_until` has passed is not stuck; the reclaim sweep returns it
  to `PENDING`.

### `AccountSetupTokenPurpose`
`BOOTSTRAP` — first Owner, issued by the CLI · `RESET` — routine password reset by an Owner ·
`RECOVERY` — last-Owner break-glass, issued by the CLI.
All are single-use, 30-minute, and stored **only as a SHA-256 hash**.

### `AuditSource`
`API` — a user request · `JOB` — a scheduled job (`user_id` is null) · `CLI` — an administrative
command (bootstrap, recovery) · `SYSTEM` — an internal change with no attributable human.

> `MIGRATION` is **not** a value: migrations do not pass through the audit interceptor, so
> listing it would describe a capability that does not exist.

### `BrandAssetType` (lookup table, not a C# enum)
`PRIMARY_LOGO` · `COMPACT_LOGO` · `NEGATIVE_LOGO` · `SYMBOL` · `FAVICON` · `DOCUMENT_LOGO` ·
`OTHER`

### `BrandingRole`
| Role | Consumer |
|---|---|
| `SYSTEM_LOGO` | application shell |
| `SYSTEM_LOGO_COMPACT` | collapsed sidebar, small viewports |
| `FAVICON` | browser tab |
| `DOCUMENT_DEFAULT_LOGO` | any document block set to `INHERIT_DEFAULT` |

One assignment per role. The app asks for *the system logo*, never for a named asset.

### `LogoSource` (document block)
`INHERIT_DEFAULT` — resolve `DOCUMENT_DEFAULT_LOGO` · `SPECIFIC_ASSET` — a named
`brand_asset_id` · `NONE` — render nothing.

Resolved at render time to a concrete `brand_asset_version_id`, then frozen into the snapshot.

### `DocumentPurpose`
`PREVIEW` — disposable, prunable after `documents.preview_retention_days` (30).
`ISSUED` — **permanent and immutable**; never updated, never deleted, never regenerated.

### `BindingKind`
`SCALAR` · `COLLECTION` · `IMAGE` · `MONEY` · `DATE` · `PERCENT` · `RICH_TEXT`
Determines server-side formatting. Templates never supply format code.

### `VisibleWhenOperator`
`IS_NULL` · `IS_NOT_NULL` · `IS_EMPTY` · `IS_NOT_EMPTY` · `EQUALS` · `NOT_EQUALS` · `GT` ·
`GTE` · `LT` · `LTE`, grouped with `all` / `any`, nesting depth ≤ 3.
A closed set: there is no expression language in a template.

### `AiInsightKind`
`FACT` — supported directly by the data sent.
`HYPOTHESIS` — a plausible explanation the data does not prove.
`RECOMMENDATION` — a proposed action.
The prompt requires this separation explicitly; results that do not carry a kind are rejected.

---

## 3. Field semantics that are easy to get wrong

| Field | Meaning | Common mistake |
|---|---|---|
| `quote_revision.valid_until` | Business date, org timezone, frozen at issue | Recomputing it from the current setting |
| `quote_revision.revision_index` | 1 = original | Assuming 0-based, or that index 1 renders "A" |
| `quote_revision.revision_suffix` | Persisted string; `''` for index 1 | Recomputing on read |
| `quote_revision.superseded_by_revision_id` | "Not current" marker | Confusing it with `status = SUPERSEDED`: an APPROVED revision can be superseded and keeps its status |
| `quote_item.unit_price` | Final unit price **before** discount | Treating it as the net price |
| `quote_item.net_unit_price` | After discount | — |
| `quote_item.unit_cost_amount` | Estimated cost per unit at issue time | Reading current cost instead |
| `quote_item_cost_snapshot.*` | Frozen values, never recomputed | Joining to live master data to "refresh" |
| `fee_rule_version.commission_percent` | Fraction | Storing `18` instead of `0.18` |
| `supply.latest_purchase_unit_cost` | Last purchase only, informational | Treating it as an actual costing policy (FIFO/LIFO/weighted-average) — that choice is still open, see §5 |
| `supply.current_stock_base_unit` | Cached projection of the signed sum of `inventory_movement` | Mutating it directly anywhere outside `Supply.Post()` — there is no such code path |
| `expense.amount` with `INVENTORY_PURCHASE` | Cash out, not period cost | Summing it into monthly costs |
| `production_order.quote_revision_id` | The idempotency key | Allowing two orders per revision |
| `inventory_movement.entered_quantity` / `entered_unit` | Immutable operator-entered fact (`numeric(18,8)` plus closed unit catalogue) | Reconstructing an entered purchase from its normalized delta |
| `inventory_movement.quantity_delta_base_unit` | Signed normalized base-unit delta; the internal constructor validates its sign against `type` and rejects zero | Storing an unsigned value and inferring sign from `type` separately — they could disagree |
| `sale.total_cost_amount` | Best-known cost; see `cost_basis` | Assuming it is always actual |
| `quote_revision.technical_highlights` | Free `{label, value}` pairs for presentation | Turning `material` or `tolerance` into required domain columns |
| `quote_revision.payment_terms` etc. | **Copied** from settings at issue | Reading the current setting when rendering an old proposal |
| `brand_asset.current_version_id` | The version used for **new** renders | Using it to re-render an old document |
| `generated_document.brand_asset_version_ids` | The versions actually used | Resolving the asset's current version instead |
| `generated_document.render_data_snapshot` | The frozen, resolved context | Rebuilding the context from live tables |
| `generated_document.purpose` | `ISSUED` is immutable evidence | Treating a preview as the document the customer received |
| `customer.creation_sequence` | Internal immutable database sequence used only as the final Customer pagination tie-breaker | Exposing it as a Customer number, generating it with `MAX + 1`, requiring gap-free values or reusing it after soft delete |
| `<root>.version` | Aggregate-wide concurrency token, bumped by **any** child change | Expecting `xmin`, or expecting a child edit to leave it alone |
| `audit_log.wave_index` | Which save wave of one command produced the row | Reading two rows for one entity as duplicated audit — they are two real state changes sharing a `correlation_id` |
| `outbox_message.attempt_count` | Attempts consumed **in the current generation**; incremented **at claim** | Reading it as a lifetime total, or assuming a crashed worker gets its attempt back |
| `outbox_message.execution_generation` | The current retry **round**; starts at **1**, incremented by manual requeue | Deriving it from the history table, or mixing it into a business key |
| historical attempt identity | `(outbox_message_id, execution_generation, attempt_number)` | Keying history on `attempt_number` alone — it resets on requeue and collides |
| `requeue_count` | **Not stored** — derived as `execution_generation - 1` | Adding a second mutable counter that can drift |
| `outbox_message.processing_token` | The **lease fence**; every worker transition must match it | Using `worker_id` as the fence — it is a diagnostic label only |
| `outbox_message.worker_id` | Diagnostic label only | Treating it as proof of ownership |
| `<root>.version` initial value | **1** at creation, never 0 | Assuming 0, or assuming one bump per child changed |
| `<root>.version` during the creating UoW | stays **1** even if a later wave modifies the new root | Expecting `1 → 2` inside the same command |
| `user.setup_status` | Login gate, checked **before** password verification | Inferring "not set up" from `password_hash IS NULL` |
| `account_setup_token.consumed_at` vs `invalidated_at` | Used by a human vs superseded by a newer token | Conflating them and losing what actually happened |
| `generated_document.render_request_id` | Identifies the render **request**, not the content | Using the template version as the dedup key, which would block a legitimate re-issue |

---

## 4. Metric definitions

Every metric below is derived from transactional tables. None is materialized in v1.

### 4.1 Conversion rate *(Taxa de conversão)*

> **Formal definition.** The share of **decided** quotes that were approved.

*(H-009 A closed by [ADR-0020 §B.2](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md).
The definition below was previously keyed on the **current revision's** status, which silently
removed a won quote from a closed period as soon as anyone revised it, and removed a lost quote
from its period as soon as anyone revived it. Outcomes are now derived from the append-only
status history and never move once recorded.)*

*(Same-period cardinality frozen 2026-09-20 — product decision **Option B**, applied by mission
`VERCE3D-S6-H009A-PERIOD-DEDUPE-001`. The previous draft defined `lost(period)` as one entry per
eligible **transition**, which let a single quote that expired and was then canceled again inside
one January contribute **two** denominator entries, and let a quote that expired and was then
approved inside one January contribute to `lost` **and** `won` simultaneously. Both contradicted
this section's own "never counted twice within the same period" guarantee. The KPI is now
deduplicated by `QuoteId` + reporting period, with `WON` taking precedence.)*

The outcome belongs to the **quote** and is **derived** (never stored). It is **not** uniformly
"monotonic" — that overstated an earlier draft of this definition (correction pass,
2026-09-20, MEDIUM-01): only the *fact of having ever won* is absorbing; the *current*
classification of a quote that has never won can legitimately move back and forth:

```
firstApprovalAt(quote) = MIN(changed_at) over quote_status_history rows of this quote's
                         revisions WHERE to_status = 'APPROVED'      -- null if never approved

hasEverWon(quote) = firstApprovalAt is not null      -- MONOTONIC: false → true, never back

commercialOutcome(quote) =     -- the CURRENT classification, read at any instant
    WON    if hasEverWon(quote)
    LOST   if NOT hasEverWon(quote) AND the current revision is CANCELED or EXPIRED
    OPEN   otherwise
```

`commercialOutcome` itself is monotonic **only once it reaches `WON`** — from there it never
changes. Before that, a never-won quote's current outcome can cycle `OPEN → LOST → OPEN`: its
revision expires or is canceled (`OPEN → LOST`), and a later revision revives it
(`LOST → OPEN` — reviving is not itself a decision). This is expected, matches
[STATE-MACHINES §1.6](STATE-MACHINES.md#16-expiration)'s revival rule, and is exactly why period
metrics are computed from the **history**, never from the live `commercialOutcome` of every quote
as of today.

#### The period KPI: one decision per quote per period

The reporting unit is a **quote-period decision**: for one `QuoteId` and one reporting period, a
quote contributes **at most one** closed-decision outcome, resolved by precedence
`WON > LOST > no contribution`:

```
periodOutcome(quote, period) =
    WON    if firstApprovalAt(quote) falls inside the period
    LOST   else if the quote has ≥ 1 eligible pre-win CANCELED/EXPIRED transition inside the
                period — "eligible" meaning it occurred while the quote had no earlier
                APPROVED transition (i.e. strictly before firstApprovalAt, or at any time if
                the quote was never approved)
    —      otherwise (no closed decision in this period)

won(period)     = count of DISTINCT QuoteIds with periodOutcome = WON
lost(period)    = count of DISTINCT QuoteIds with periodOutcome = LOST
decided(period) = won(period) + lost(period)

conversionRate(period) = decided > 0 ? won / decided : null
```

Equivalently, and this is the clearer way to implement it: take the eligible decision history,
group it by `QuoteId` + reporting period, and classify each group once — win in the period wins,
otherwise any pre-win loss in the period, otherwise nothing. No quote can appear in `won(period)`
and `lost(period)` for the same period, and no quote can appear twice in either.

Worked cases (all in the organization timezone, rule 4 below):

| Quote's history | Jan | Feb | Mar |
|---|---|---|---|
| Jan 03 `EXPIRED`, never revived | `LOST` | — | — |
| Jan 03 `EXPIRED`, Jan 10 revived, Jan 20 `CANCELED` | `LOST` (**1, not 2**) | — | — |
| Jan 03 `EXPIRED`, Jan 10 revived, Jan 20 `APPROVED` | `WON` (**and `LOST` = 0**) | — | — |
| Jan 03 `EXPIRED`, Feb 10 revived, Feb 20 `CANCELED` | `LOST` | `LOST` | — |
| Jan 03 `EXPIRED`, Mar 20 first `APPROVED` | `LOST` | — | `WON` |
| Mar 10 revision A `APPROVED`, Apr 10 revision B `APPROVED` | — | — | `WON` in Mar; **Apr contributes nothing** |

Rules that make this honest:

1. **The counting unit is one quote-period decision** — not one lifecycle transition, and not one
   lifetime slot per quote. Inside a single period a quote is counted **at most once**, with
   `WON` beating `LOST`; across its lifetime it may be counted in several periods, but it can
   enter `won()` **at most once ever** (keyed on `firstApprovalAt`). Revisions never multiply the
   count: a quote revised four times inside one negotiation, expiring and being revived twice
   along the way, still contributes exactly one outcome to each period in which it was decided.
2. **A quote still open is not a failure.** `GENERATED`, `SENT` and `NEGOTIATING` are excluded
   from numerator *and* denominator, and are reported separately as *"orçamentos em aberto"*.
3. `SUPERSEDED` is never an outcome — it is an internal transition between revisions.
4. The period is keyed on the **decision date** (the `changed_at` of the status-history row that
   produced the terminal state, or of the first `APPROVED` row for a win), not the creation date,
   and it is bucketed in the **organization timezone** — the same `America/Sao_Paulo` clock
   `ValidUntil` and every other business date already use. No new calendar rule is introduced.
5. When `decided = 0`, the metric is `null` and renders as `—`. It is never rendered as 0%,
   which would read as total failure.
6. **A win is permanent and dated once.** Revising an approved quote — or approving the
   superseding revision — never un-wins it and never produces a second win: that is the same deal
   at a new scope, so a later period gains **nothing** from it. This mirrors production, where the
   original approval stands if the superseding revision falls through
   ([STATE-MACHINES §3](STATE-MACHINES.md#3-altering-an-approved-quote)).
7. **Losses count only before a quote has ever been won**, and a period's loss is dated to that
   period — but a period counts at most one of them however many eligible transitions it holds
   (rule 1). A quote lost in January and won in March is a loss in January *and* a win in March:
   both were true when reported, and **no closed period ever changes**. That stability is the
   point; the cost is that such a quote is counted in two periods — once per period, never twice
   in one.
8. A won quote may still have a non-terminal current revision (an active re-negotiation). It
   legitimately appears in both the historical won count and *"orçamentos em aberto"*; the UI
   must label that case rather than hide it.
9. Reversal of a won deal is a **Sales** fact, not a quote one — there is no "un-approve". A
   canceled `Sale` leaves every revenue metric while the quote stays won
   ([DOMAIN-MODEL §9](DOMAIN-MODEL.md#9-sales-module)).
10. **Deduplication is a reporting rule, not a history rule.** `quote_status_history` stays
    append-only and complete: every `APPROVED`, `CANCELED` and `EXPIRED` transition is recorded,
    auditable and visible on the revision timeline, including the second January cancellation that
    the January KPI does not count a second time. Nothing is deleted, collapsed, back-dated or
    hidden — the grouping above exists only in how the metric reads that history. No table, column
    or event is introduced to store a period outcome; it is derived on read, like every other
    metric in this section.

**Secondary metric — cohort conversion** *(conversão por safra)*:
```
cohortConversion(month) = approved quotes CREATED in month / all quotes CREATED in month
```
Useful for trends, but it structurally under-reports recent months (quotes created last week
have not had time to be decided). The UI must label it and grey out incomplete cohorts.

### 4.2 Revenue and profit

| Metric | Formula | Source |
|---|---|---|
| Faturamento (revenue) | `Σ sale.net_amount` where `status = CONFIRMED` | Sales only |
| Custo dos produtos vendidos | `Σ sale.total_cost_amount` | Sales |
| Taxas de canal | `Σ sale.channel_fee_amount` | Sales |
| Lucro bruto | `Σ sale.gross_profit_amount` | Sales |
| Margem efetiva | `lucro bruto / faturamento` | Sales |
| Despesas operacionais | `Σ expense.amount` where treatment = `OPERATING_EXPENSE` | Finance |
| **Resultado de caixa** | `faturamento − despesas operacionais − compras de estoque` | Both |

**Revenue is read from `Sale` only. Never from `Quote`.** A quote is a proposal; counting it as
revenue would inflate every financial figure by the deals that never closed.

### 4.3 Cost variance *(estimado × real)*

```
gramsVariance    = Σ actual grams − Σ planned grams          (per filament, per item, per period)
energyVariance   = Σ actual kWh   − Σ estimated kWh
costVariance     = actualTotalCost − estimatedTotalCost
relativeVariance = costVariance / estimatedTotalCost         (null when estimate = 0)
marginVariance   = realizedMargin − effectiveMarginAtQuote
```
Variance is **always computed at read time**. It is never stored, because storing it would
create a third number that can disagree with the two it derives from.

### 4.4 Consumption

```
supplyConsumption(period)   = Σ inventory_movement.quantity_delta_base_unit
                              where type = Consumption
                                and occurred_at in period
                              (grouped by supply_id; filter supply.category_code = 'FILAMENT'
                               for the filament-only view)
energyConsumption(period)   = Σ energy_consumption_session.kwh where started_at in period
```

> `Consumption` is reserved in `InventoryMovementType` but not yet emitted by any S3 endpoint —
> this formula is forward-looking until the Production module (S8–S11) records actual consumption
> as `Consumption` movements. See [ADR-0017 §1](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md).

### 4.5 Home dashboard

| Tile | Definition |
|---|---|
| Vendas no mês | §4.2 revenue, current month, org timezone |
| Lucro | §4.2 gross profit, current month |
| Custos | operating expenses, current month (**excludes** inventory purchases) |
| Orçamentos em aberto | count of quotes whose current revision ∈ {GENERATED, SENT, NEGOTIATING} |
| Conversão | §4.1, trailing 90 days by default |
| Pedidos em produção | count of production orders ∈ {QUEUED, IN_PRODUCTION} |
| Últimos orçamentos | 10 most recent quotes by `issued_at desc`, current revision |

Home search matches `quote.number_text` (exact and prefix) and `customer.name` (trigram).
Sorting: date, value (`total_amount`), status, customer name.

---

## 5. Policies

### Purchase cost policy (implemented in S4) <a id="purchase-cost-policy-deferred-to-s4"></a>

S3 records a `PurchaseReceipt` movement's `UnitCostSnapshot`/`TotalCostSnapshot` and caches the
latest one on `Supply.LatestPurchaseUnitCost` — **last purchase only**, purely informational (see
[ADR-0017 §5](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md)). S4 does not read
that cache. It implements `WEIGHTED_AVERAGE_ACQUISITION` over immutable, positive, cost-bearing
`PurchaseReceipt` movements: `Σ(quantity_base × unit_cost_base) / Σ(quantity_base)`, rounded to
six decimal places. Manual increases/decreases/corrections, current stock and costless purchases
do not participate. This is an acquisition estimate, not a moving average of remaining stock.

No eligible purchase yields `COST_BASIS_UNAVAILABLE`. A calculation line may instead provide a
non-negative manual cost per base unit; the result source and policy are both explicit as
`MANUAL_OVERRIDE`, and no Supply or movement is modified.

The S4 cost result itself is transient (no Costing schema): entered quantities retain up to eight
decimal places, normalized/effective quantities use four, and component/totals use six internal
money places while the UI displays BRL with two. See ADR-0018.

### Price rounding policy
*(setting `pricing.price_rounding_policy`)* — see
[CR-07.4](CALCULATION-RULES.md#cr-074--price-rounding-policy-setting-pricingprice_rounding_policy).
Default `CENT`. All non-default policies round **up**.

### Quote validity
*(setting `quote.default_validity_days`, default 15)* — copied onto each revision at issue.
Changing the setting affects only revisions issued afterwards.

### Stock enforcement
**Inventory movements themselves are a hard, blocking invariant as of S3**: a `ManualDecrease` (or
a `Correction` whose derived delta is negative) that would drive a supply's stock below zero is
rejected (`INSUFFICIENT_STOCK`), and two concurrent decreases against the same supply cannot both
succeed — see [ADR-0017 §3](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md).
This reverses the v1 "warn, never block" default for that one surface.

**Quotes and production orders are unaffected by this**: creating either with insufficient stock
still only warns, never blocks, exactly as before — a shop that prints on demand routinely quotes
material it has not bought yet. Whether *that* changes remains the open decision from the S0
report; S3 did not touch it.

---

## 6. Portuguese ↔ English label map

For UI copy and AI prompt construction. The database and code always use the English term.

| pt-BR (UI) | Code / DB |
|---|---|
| Cliente | Customer |
| Endereço | Address |
| Produto | Product |
| Ficha técnica / Receita | ProductRecipe |
| Filamento | `Supply.FilamentDetails` (no separate `Filament` class as of S3 — [ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md) §6) |
| Insumo | Supply |
| Categoria de insumo | SupplyCategory |
| Embalagem | Packaging (`SupplyCategory` code `PACKAGING`) |
| Movimentação de estoque | InventoryMovement |
| Estoque | Stock |
| Estoque baixo | Low stock (`CurrentStockBaseUnit <= MinimumStock`) |
| Saldo inicial | Initial balance (`InventoryMovementType.InitialBalance`) |
| Ajuste manual | Manual adjustment (`ManualIncrease` / `ManualDecrease` / `Correction`) |
| Máquina / Impressora | Machine |
| Tarifa de energia | EnergyTariff |
| Laboratório / Experimento | CostExperiment |
| Orçamento | Quote |
| Revisão | QuoteRevision |
| Item do orçamento | QuoteItem |
| Canal de venda | SalesChannel |
| Taxa / Comissão | FeeRule / commission |
| Margem desejada | DesiredMargin |
| Margem efetiva | EffectiveMargin |
| Preço sugerido | SuggestedPrice |
| Desconto | Discount |
| Venda | Sale |
| Despesa | Expense |
| Pedido de produção | ProductionOrder |
| Etiqueta de envio | ShippingLabel |
| Custo previsto | Estimated cost |
| Custo real | Actual cost |
| Variação | Variance |
| Proposta comercial | Quote document (`QUOTE` document type) |
| Modelo de documento | DocumentTemplate |
| Escopo | Scope |
| Não incluso | OutOfScope |
| Informações técnicas | TechnicalHighlights |
| Condições de pagamento | PaymentTerms |
| Prazo de entrega | DeliveryTerms |
| Garantia | Warranty |
| Marca / Identidade visual | Branding |
| Logotipo | BrandAsset |
| Dados da empresa | CompanyProfile |
| Perda | Wastage |
| Mão de obra | Labor |
| Taxa de conversão | ConversionRate |
