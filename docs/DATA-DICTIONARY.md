# DATA DICTIONARY — Verce 3D | Laboratório de Custos

Definitions of terms, fields, enumerations and metrics. When a report, a screen label or an AI
prompt uses one of these terms, it means exactly what is written here and nothing else.

---

## 1. Units and their meaning

| Concept | Unit | Stored as | Notes |
|---|---|---|---|
| Money | BRL | `numeric(18,2)` presented, `numeric(18,6)` intermediate | never `float` |
| Percent | fraction | `numeric(9,6)` | `0.175` = 17,5%. The UI multiplies by 100 for display and divides on input. |
| Filament weight | gram | `numeric(12,3)` | consumption is always grams, never kg |
| Filament price | R$/kg | `numeric(18,6)` | purchase is per spool, price is normalized per kg |
| Energy | kWh | `numeric(12,4)` | |
| Energy tariff | R$/kWh | `numeric(18,6)` | may or may not include taxes — see `includes_taxes` |
| Power | watt | `integer` | |
| Print time | second | `integer` | displayed as `3h25` |
| Labor time | minute | `integer` | |
| Quantity | supply-dependent | `numeric(14,4)` | must match the supply's `unit` |

---

## 2. Core enumerations

### `PersonType`
`INDIVIDUAL` (pessoa física, CPF) · `COMPANY` (pessoa jurídica, CNPJ)

### `SupplyUnit`
`UNIT` (unidade) · `GRAM` · `KILOGRAM` · `MILLILITER` · `LITER` · `CENTIMETER` · `METER` ·
`SQUARE_METER` · `HOUR`

### `SupplyCategoryKind`
`CONSUMABLE` (cola, verniz, tinta) · `COMPONENT` (argola, parafuso, ímã) ·
`PACKAGING` (sacola, caixa) · `LABEL` (etiqueta) · `ACCESSORY` · `OTHER`

`PACKAGING` is the flag that makes packaging a separate line in the cost breakdown. It is a
category kind, not a separate entity.

### `MaterialKind`
`FILAMENT` · `SUPPLY` — the discriminator shared by stock movements and consumption records.

### `StockMovementReason`
`PURCHASE` · `PRODUCTION_CONSUMPTION` · `MANUAL_ADJUSTMENT` · `INVENTORY_COUNT` ·
`LOSS` · `RETURN`

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
| `filament.current_price_per_kg` | Live price for **new** calculations only | Using it to explain an old quote |
| `expense.amount` with `INVENTORY_PURCHASE` | Cash out, not period cost | Summing it into monthly costs |
| `production_order.quote_revision_id` | The idempotency key | Allowing two orders per revision |
| `stock_movement.quantity` | Signed by `direction` semantics | Storing an unsigned value and losing the direction |
| `sale.total_cost_amount` | Best-known cost; see `cost_basis` | Assuming it is always actual |
| `quote_revision.technical_highlights` | Free `{label, value}` pairs for presentation | Turning `material` or `tolerance` into required domain columns |
| `quote_revision.payment_terms` etc. | **Copied** from settings at issue | Reading the current setting when rendering an old proposal |
| `brand_asset.current_version_id` | The version used for **new** renders | Using it to re-render an old document |
| `generated_document.brand_asset_version_ids` | The versions actually used | Resolving the asset's current version instead |
| `generated_document.render_data_snapshot` | The frozen, resolved context | Rebuilding the context from live tables |
| `generated_document.purpose` | `ISSUED` is immutable evidence | Treating a preview as the document the customer received |
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

```
decided(period)  = quotes whose current revision reached APPROVED, CANCELED or EXPIRED
                   within the period
approved(period) = subset of decided(period) whose current revision status is APPROVED

conversionRate(period) = decided > 0 ? approved / decided : null
```

Rules that make this honest:

1. **The unit is the quote, not the revision.** A quote revised four times counts once.
2. **A quote still open is not a failure.** `GENERATED`, `SENT` and `NEGOTIATING` are excluded
   from numerator *and* denominator, and are reported separately as *"orçamentos em aberto"*.
3. `SUPERSEDED` is never an outcome — it is an internal transition between revisions.
4. The period is keyed on the **decision date** (the status-history row that produced the
   terminal state), not the creation date.
5. When `decided = 0`, the metric is `null` and renders as `—`. It is never rendered as 0%,
   which would read as total failure.

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
filamentConsumption(period) = Σ stock_movement.quantity
                              where material_kind = FILAMENT
                                and reason = PRODUCTION_CONSUMPTION
                                and occurred_at in period
energyConsumption(period)   = Σ energy_consumption_session.kwh where started_at in period
```

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

### Filament price policy
*(setting `inventory.filament_price_policy`)* <a id="filament-price-policy"></a>

| Policy | Behaviour | Status |
|---|---|---|
| `LAST_PURCHASE` | Registering a lot sets `current_price_per_kg` to that lot's price and appends price history | **default, v1** |
| `MANUAL` | Lots record purchases but never change the current price; the operator sets it | v1 |
| `WEIGHTED_AVERAGE` | Current price = value-weighted average of remaining stock | **deferred** — requires per-lot consumption tracking (which spool was used), which the shop floor does not currently record |

Whatever the policy, a price change **appends history** and **never touches an existing
snapshot**.

### Price rounding policy
*(setting `pricing.price_rounding_policy`)* — see
[CR-07.4](CALCULATION-RULES.md#cr-074--price-rounding-policy-setting-pricingprice_rounding_policy).
Default `CENT`. All non-default policies round **up**.

### Quote validity
*(setting `quote.default_validity_days`, default 15)* — copied onto each revision at issue.
Changing the setting affects only revisions issued afterwards.

### Stock enforcement
v1 **warns** on insufficient stock; it never blocks a quote or a production order. Blocking is
an explicit open decision (see the S0 report), because a shop that prints on demand routinely
quotes material it has not bought yet.

---

## 6. Portuguese ↔ English label map

For UI copy and AI prompt construction. The database and code always use the English term.

| pt-BR (UI) | Code / DB |
|---|---|
| Cliente | Customer |
| Endereço | Address |
| Produto | Product |
| Ficha técnica / Receita | ProductRecipe |
| Filamento | Filament |
| Insumo | Supply |
| Embalagem | Packaging (supply category kind) |
| Lote | Lot |
| Estoque | Stock |
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
