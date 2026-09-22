# ADR-0021 — S8A Sale Conversion and Expense Model

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-22
- **Sprint:** S8A.ARCH

## Context

S6 approval creates the idempotent operational `ProductionOrder`; it is not a commercial sale.
S8A needs one canonical financial record, durable expense treatment, and a non-destructive seam
for later marketplace ingestion without implementing Commerce, S8B, or S8C.

## Decision

### Sale conversion, eligibility and cardinality

`QuoteRevision(APPROVED) -> explicit operator command -> Sale`. Approval never creates a Sale.
An approved revision remains sale-eligible after supersession because supersession does not change
its approved status. Every other status fails with `QUOTE_REVISION_NOT_SALE_ELIGIBLE`.

There is at most one non-canceled Sale per revision: `UNIQUE (quote_revision_id) WHERE
quote_revision_id IS NOT NULL AND status <> 'CANCELED'`. Split/partial sales are
`OUT_OF_SCOPE_V1`. A client supplies `conversion_request_id`; replay returns the existing Sale
through the repository's `ON CONFLICT DO NOTHING + SELECT` convergence pattern. A different id
against an existing live Sale fails `SALE_ALREADY_EXISTS_FOR_REVISION`. A canceled Sale permits a
new deliberate conversion.

`SequentialNumberAllocator` remains the only numbering mechanism, with `series = SALE` and
`YYMMDD-N` format.

### Origin, channel and future marketplace seam

`Sale.source` answers how Sale entered VERCE and is exactly `QUOTE_CONVERSION`, `MANUAL_ENTRY`,
or `MARKETPLACE_ORDER`. `sales_channel_id` answers where it occurred. Thus `MANUAL_ENTRY` may
use DIRECT or any marketplace channel; `MANUAL_DIRECT` is not a valid origin.

The S8A target keeps `source`, `marketplace_account_id`, `external_order_id`, and `fee_source`
(`LOCAL_RULE|PROVIDER_REPORTED`) on Sale. `MARKETPLACE_ORDER` requires both marketplace identity
fields and no quote revision; `QUOTE_CONVERSION` requires a quote revision and no marketplace
identity; `MANUAL_ENTRY` has neither identity class. `(marketplace_account_id, external_order_id)`
is unfiltered unique: cancellation never frees a provider order identity.

S8C later maps provider `PENDING` to no Sale, `CONFIRMED` to the canonical Sale, pre-sale
`CANCELED` to no Sale, and post-sale `CANCELED` to Sale cancellation. Unlinked items block the
order before a Sale exists. This ADR specifies no provider endpoint or Commerce implementation.

### Snapshots, manual sales and lifecycle

Conversion copies, never recomputes, QuoteItem commercial values and revision totals, identities,
fee-rule provenance, customer name snapshot, and line financial/cost amounts. The immutable
QuoteItem cost-snapshot tree remains historical detail through `quote_item_id`; it is not copied.
New S8 Sales begin `cost_basis = ESTIMATED`; S11 alone transitions it to `MIXED` then `ACTUAL`.

Standalone sales freeze their accepted ProductCostCalculator or explicitly authorized cost basis;
absence fails `COST_BASIS_UNAVAILABLE`. Sale has only `CONFIRMED -> CANCELED`; cancellation needs
a nonblank reason, actor, timestamp, status history and audit. It does not mutate ProductionOrder,
and Production delivery does not forbid cancellation. Intended authorization is `sales:read` and
`sales:manage`.

### Expenses and double-counting

Treatments remain `OPERATING_EXPENSE`, `INVENTORY_PURCHASE`, and `ASSET_ACQUISITION`. Marketplace
fees are Sale snapshots, production cost is production/costing truth, seller-paid per-sale shipping
is `Sale.shipping_amount`, carrier invoices and ads are operating expenses, equipment is an asset,
and an inventory purchase is an `INVENTORY_PURCHASE` expense linked to its PurchaseReceipt
InventoryMovement by plain UUID.

`finance.expense.inventory_movement_id` is nullable. When present it requires
`INVENTORY_PURCHASE`, and a filtered unique constraint permits at most one linked Expense per
PurchaseReceipt. `expense.sales_channel_id` is nullable attribution for ads, shipping, and other
operating expenditure; it never duplicates fees. Expense and category mutations are audited and
use `expenses:read`/`expenses:manage` intent.

## Consequences

Sales is the sole financial truth for revenue, units sold and margin. Commerce later observes
provider reality and calls a Sales port implemented in `Verce.Api`; it does not create a parallel
MarketplaceSale. Cross-module references remain plain UUIDs, never physical cross-module FKs.
The future target migration is `AddS8ASalesAndExpenses`; this ADR creates no migration or code.

## Supersedes / clarifies

Clarifies ADR-0020's statement that approval makes a revision sale-eligible: eligibility is not
automatic conversion. Amends ADR-0013's lot-era linkage for the S3 PurchaseReceipt ledger without
rewriting that historical decision. Resolves H-002 and H-008 A architecture decisions.
