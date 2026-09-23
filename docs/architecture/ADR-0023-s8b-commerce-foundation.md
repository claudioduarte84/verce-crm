# ADR-0023 — S8B Commerce Foundation

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-23
- **Sprint:** S8B.ARCH

## Context

S8A shipped the canonical financial `Sale`, durable `Expense`, price brackets and the future
marketplace identity seam. Catalog still owns the technical Product/BOM, Pricing owns channels
and fee rules, and Sales owns recognized revenue. The next step is a provider-neutral commercial
catalogue that can express VERCE's intent independently from what an external marketplace is
currently reporting.

S8B must not guess provider APIs. Mercado Livre, Shopee and TikTok Shop differ in authentication,
permissions, listing semantics, fees and regional availability. Those facts require an official
provider-discovery gate before any connector is written. The foundation must therefore remain
valid when a capability is unsupported, not granted, temporarily unavailable or still unknown.

## Decision

### 1. Module and authority boundaries

`Verce.Modules.Commerce` becomes the fifteenth business module and owns the `commerce` schema.
The two central truths are deliberately separate:

| Truth | Owner | Meaning |
|---|---|---|
| Product/BOM/current estimated technical cost | Catalog + Costing | what VERCE can make and its current technical basis |
| SalesChannel/FeeRule/PriceBracket | Pricing | channel identity and deterministic fee/pricing authority |
| ChannelOffer | Commerce | VERCE's current commercial intent for one Product × SalesChannel |
| MarketplaceListing | Commerce | the last safe, normalized facts observed about one external listing |
| Sale/SaleItem | Sales | canonical recognized revenue, units and financial snapshots |
| Expense | Finance | cash/cost classification outside Sale snapshots |
| image bytes and immutable versions | Settings | validated, content-addressed asset storage |

Commerce references Product, SalesChannel, BrandAsset and Sale data through plain UUIDs and
composition-root ports. It has no navigation property or project reference to another business
module. Same-schema Commerce relationships use physical foreign keys. Adapters in `Verce.Api`
compose the modules and validate referenced identities and active-state guards.

Commerce does not own marketplace orders. S8C may observe external orders and call a Sales port;
Sales remains the only owner of canonical financial `Sale`. There is no `MarketplaceSale`.

### 2. ChannelOffer is commercial intent

`ChannelOffer` is a Commerce aggregate root with ordinary business identity
`(ProductId, SalesChannelId)`. Exactly one durable row exists for that pair; status changes never
free the identity for reuse.

It contains:

- `Id`, `ProductId`, `SalesChannelId`;
- `Status {ACTIVE, INACTIVE}`;
- `IntendedUnitPrice` (`numeric(18,2)`, strictly positive);
- `PriceSource {PRICING_ENGINE, MANUAL, IMPORTED_OBSERVED}`;
- optional `EstimatedSellerPaidShippingAmount` and its source
  `{MANUAL, IMPORTED_OBSERVED, PROVIDER_SYNC}`; S8B produces only the first two values;
- `ActivatedAt?`, `DeactivatedAt?`, `CreatedAt`, `UpdatedAt`, `Version`.

`ACTIVE` means VERCE intends to offer the Product on that channel. `INACTIVE` retains commercial
history. Activating requires the referenced Product and SalesChannel to be active. Later Product
or SalesChannel deactivation never deletes or silently changes the offer row.

The product-level fact is exact:

```text
isCommerciallyActive(product)
= product.active AND exists ACTIVE ChannelOffer
```

No redundant `commercial_active` flag is added to Product.

This product-level fact does not imply that every ACTIVE offer is currently transactable.
Product deactivation makes the exact fact false. SalesChannel deactivation leaves the exact
intent fact and offer status unchanged, but a separate operational guard blocks new activation,
publication and commercial operations on that channel while historical labels remain readable.

`IntendedUnitPrice` is VERCE's decision, not an external observation. A PricingEngine result may
seed it, an operator may override it, and a deterministically reconciled imported listing may
seed a previously absent offer as `IMPORTED_OBSERVED`. Subsequent listing observations never
overwrite offer price, status or source.

The canonical `DIRECT` SalesChannel receives ordinary ChannelOffers and uses its existing zero-fee
Pricing rule. Commerce creates no fake provider, account, listing or publication request for
DIRECT. A DIRECT-only Product belongs in Commercial Catalog and never in Published Items.

All offer mutations carry expected `Version`, use the repository's optimistic concurrency
contract and emit generic audit records. Price changes and activate/deactivate actions are
auditable. Offers are deactivated, not physically deleted.

### 3. Marketplace provider and account

`MarketplaceProvider` is Commerce reference data with initial textual codes
`MERCADO_LIVRE`, `SHOPEE` and `TIKTOK_SHOP`. `DIRECT` is a SalesChannel code and is never a
MarketplaceProvider. Provider is not globally unique on MarketplaceAccount; multiple shops for
one provider are valid.

`MarketplaceAccount` is an aggregate root identified externally by the ordinary unique pair
`(ProviderCode, ExternalAccountId)`. It also stores `SalesChannelId`, display name, `Active`,
`CredentialReference?`, connection state, sync timestamps/errors, audit timestamps and `Version`.
At creation the referenced SalesChannel must exist, be active, have `Kind = Marketplace` and not
use code `DIRECT`. `SalesChannelId` is immutable after creation because it defines the listing's
deterministic ChannelOffer channel. S8B rejects reassignment; changing the business channel means
deactivating the old configuration and creating a new account identity/configuration, or a later
explicit migration decision. Deactivation preserves the original SalesChannelId and all history.

Account lifecycle and connection/sync condition are orthogonal:

- active state: `ACTIVE|INACTIVE`;
- connection state: `NOT_CONFIGURED|DISCONNECTED|CONNECTED|ERROR`;
- sync state: `NEVER_SYNCED|SYNCED|ERROR`.

An inactive account permits no new sync or provider write, but preserves listings and does not
deactivate ChannelOffers. Connection identity survives credential refresh or rotation.

Commerce stores only a `CredentialReference`/secret-key identifier. Raw access tokens, refresh
tokens and client secrets are never ordinary Commerce columns, logs or error text. Secret
protection and cryptography stay with the existing Platform/Data Protection architecture.

### 4. Capabilities are two-sided facts

The closed initial capability vocabulary is:

```text
LISTINGS_READ
LISTINGS_WRITE
ORDERS_READ
FEES_QUOTE
ANALYTICS_READ
ADS_READ
SHIPPING_READ
INVENTORY_SYNC
```

Provider declaration and account grant are stored separately. Structural provider state is
exactly `UNKNOWN|SUPPORTED|UNSUPPORTED`; account authorization state is exactly
`UNKNOWN|GRANTED|DENIED`.

```text
effectiveCapability
= provider state is SUPPORTED
  AND account state is GRANTED
  AND account is ACTIVE
```

Operational execution additionally requires current provider/account runtime availability to
permit the action. Transient health remains in MarketplaceAccount connection/sync state and the
future connector's runtime result; S8B creates no per-capability health column. A timeout or
outage may block execution but never rewrites `SUPPORTED` to `UNSUPPORTED` or `GRANTED` to
`DENIED`. Missing structural/grant evidence disables the feature; it never requires a schema
change. S8B can represent manual/unknown states but does not claim official support. S8C.0
validates these facts from official provider material before any connector or endpoint is
implemented.

### 5. MarketplaceListing is observed external reality

`MarketplaceListing` is a Commerce aggregate root with ordinary, durable identity
`(MarketplaceAccountId, ExternalListingId)`. Status, unlinking or account deactivation never
frees that identity. Multiple external listings may link to one Product, including multiple
listings under the same account when their `ExternalListingId` values differ.

The aggregate stores current safe facts:

- account/external identity and optional `ExternalSku`;
- optional `ProductId` and optional same-schema `ChannelOfferId`;
- `TitleSnapshot?`, `ObservedPrice?`, `ListingUrl?`;
- normalized observed status `DRAFT|ACTIVE|PAUSED|INACTIVE|ERROR`;
- bounded provider-native status code, not an arbitrary raw payload;
- linkage state `UNLINKED|NEEDS_REVIEW|LINKED`;
- sync state `NEVER_SYNCED|SYNCED|ERROR`;
- `ProviderObservedAt?`, `LastSyncAttemptAt?`, `LastSuccessfulSyncAt?`;
- redacted/truncated `SyncError?`, timestamps and `Version`.

Observed status, linkage and sync state are separate machines. A listing may therefore be
`ACTIVE`, `LINKED` and simultaneously have sync state `ERROR`.

`ProductId` is nullable so unmatched external identity is first-class. The exact durable shapes
are:

```text
LINKED       => ProductId IS NOT NULL; ChannelOfferId may be null
UNLINKED     => ProductId IS NULL AND ChannelOfferId IS NULL
NEEDS_REVIEW => ProductId IS NULL AND ChannelOfferId IS NULL
ChannelOfferId IS NOT NULL => LinkageState = LINKED
```

`LINKED + ProductId + null ChannelOfferId` is valid: identity is confirmed but no commercial
intent was connected or created. If ChannelOfferId is present, its ProductId must equal the
listing ProductId and its SalesChannelId must equal the immutable MarketplaceAccount
SalesChannelId. Same-schema FKs enforce row existence; Commerce domain/application validation
enforces these cross-row equalities. NEEDS_REVIEW stores no guessed Product/offer; future
candidate suggestions are transient read-model data.

MarketplaceListing is not price authority. `ObservedPrice` may differ from the offer's
`IntendedUnitPrice`; Commercial views surface both values and their divergence.

### 6. Reconciliation is explicit and non-destructive

Automatic linkage is permitted only from a deterministic, verified identity rule scoped to the
provider/account, such as an exact external SKU mapping registered as authoritative. Textual
similarity, fuzzy matches, multiple candidates and unmatched values never auto-link.

For deterministic linkage when no ChannelOffer exists, the workflow may create an ACTIVE offer
using the observed price with `PriceSource = IMPORTED_OBSERVED`, but only as one atomic,
audited reconciliation command. If an offer already exists, reconciliation never overwrites its
price, status, shipping policy or source.

Ambiguous candidates set linkage to `NEEDS_REVIEW`; no candidate sets `UNLINKED`. Both appear in
the `Não vinculadas` workflow. An operator can link, unlink or correct linkage with expected
Version. Unlinking clears Product/offer linkage but retains external identity, observations and
audit. Audit captures actor, time, previous/new Product, previous/new offer and correction reason
when supplied.

### 7. Current state plus bounded observation history

MarketplaceListing holds the current read-efficient state. Commerce also owns append-only
`MarketplaceListingObservation` rows containing only normalized/safe listing facts, provenance
`MANUAL|IMPORTED|PROVIDER_SYNC`, provider-observed time and ingestion time.

The first observation and each material change append a row; a poll that changes only freshness
updates current freshness and does not duplicate an identical observation. Full provider payloads
and secret-bearing HTTP bodies are not persisted. Error text is redacted and length-bounded.
Each append carries an `ObservationKey` unique within the listing so retrying the same
manual/import/sync event is idempotent. A separate safe-fact fingerprint suppresses consecutive
freshness-only duplicates but is not globally unique: a real state sequence `A -> B -> A` retains
the second A as a new historical observation.

Observation retention is configured by `commerce.listing_observation_retention_days`, initially
`180`. Cleanup may remove expired observations but must retain the latest observation per listing
and must not delete MarketplaceListing, its external identity, generic audit or manual linkage
history. Provider-specific diagnostic artifacts, if later required, need a separate S8C decision.

### 8. Commercial Catalog and Published Items are read compositions

Commercial Catalog is Product-centric and does not create a duplicated catalogue master table.
One Product row/card composes Catalog identity, Commerce images/tags/offers, current Costing facts,
Pricing fee facts and canonical Sale metrics. Multiple channels/listings expand beneath the same
Product.

S8B may truthfully expose Product identity, image, technical/commercial active facts, offers,
intended prices, current estimated cost, fee/economics previews and canonical Sale-derived
`unitsSold?`/`revenue?` together with `salesCoverage = COMPLETE|PARTIAL|UNKNOWN` for the requested
interval/source set. Coverage means:

- `COMPLETE`: authoritative canonical Sale coverage exists for the entire interval/source set;
- `PARTIAL`: known canonical Sales exist while at least one relevant marketplace source or time
  range is uncovered;
- `UNKNOWN`: no authoritative coverage evidence exists for the relevant marketplace source and
  interval, so absent metrics are null rather than authoritative zero.

DIRECT needs no provider sync; its Sale rows are authoritative for transactions recorded in
VERCE, not a claim that every real-world direct transaction was entered. Before S8C operational
ingestion, manual marketplace Sales are valid facts but yield PARTIAL when present and UNKNOWN
when no coverage evidence exists—never COMPLETE merely because some Sale rows exist. Future
marketplace COMPLETE requires effective ORDERS_READ, successful sync and full interval/source
coverage.
Finished-goods stock, committed units, WIP and production need are `null/unavailable` until S9;
energy remains an explicit missing cost component until S10; actual cost and realized
contribution remain unavailable until S11; provider traffic, conversion and ROAS remain deferred
to S12/S13/provider integrations. Missing facts are never fabricated as zero.

Published Items is listing-centric: one MarketplaceListing per row/card. It supports provider,
account/channel, normalized status, linked/unlinked/needs-review and sync-state filters. DIRECT
offers never appear because they have no external listing.

Both read models use SQL-side pagination, search, filtering and stable total ordering. No API
loads an unbounded set for React-side filtering.

### 9. Channel economics and fee seam

Commerce exposes a stateless server-side channel-economics query. It reuses ProductCostCalculator,
Pricing FeeRule/PriceBracket resolution and existing CommercialPricingEngine calculations through
composition-root ports. React formats returned facts only; it implements no fee, profit, margin,
markup or net-contribution formula.

The query accepts Product, channel(s), comparison quantity and an optional candidate intended
price. It never accepts client-supplied cost or fee facts. Quantity defaults to one and is returned
in the response; a `PER_ORDER` fee comparison is explicitly a single-product basket at that
quantity. Multi-line basket economics must use the existing allocator with an explicit line set.

Per-channel results expose intended/candidate price, estimated commission, allocated fixed fee,
seller-paid shipping estimate, known estimated unit cost, packaging component when available,
estimated net revenue, estimated profit, contribution margin and markup. Packaging already
inside ProductCostCalculator is displayed as a component and never added twice.

Every result carries `costCoverage`, `missingComponents`, fee provenance and fee freshness. Before
S10, energy is listed as unavailable, not inserted as zero. Unknown shipping is also absent, not
zero. Economics derived from known costs may be displayed only as `PARTIAL_ESTIMATE`; it may not
be labeled complete or actual.

The Commerce-owned `IChannelFeeProvider` application port returns normalized fee facts without
provider HTTP knowledge. S8B supplies `LocalFeeRuleProvider` in the composition root. Future
implementations may include provider connectors, but not before S8C.0. Provenance vocabulary is
`LIVE_API|CACHE|MANUAL|FALLBACK`; S8B normally returns `MANUAL` for local configured FeeRules and
may return `FALLBACK` only when an explicit configured fallback is used. It never returns
`LIVE_API` or claims cache freshness.

No persistent provider-fee cache is created in S8B. Provider discovery must first establish the
real request key, account/category context, TTL, regional behaviour and rate limits. S8C may then
add a bounded cache with `retrieved_at`, `expires_at` and provenance. Stale data must never be
presented as live.

Views present channel facts side-by-side. Commerce does not encode `best`, `winner` or a channel
recommendation. S13 may later explain opportunity from deterministic facts.

### 10. Images, tags and shipping

Commerce reuses Settings' safe BrandAsset upload/storage/version pipeline. A Commerce-owned
`ProductCommercialProfile` aggregate has ordinary unique `ProductId` and owns
`ProductCommercialImage` children that map to plain `BrandAssetId`, role `PRIMARY|GALLERY`,
stable sort order and optional alt text. The referenced asset must use the new `PRODUCT_IMAGE`
BrandAssetType. There is at most one PRIMARY image per profile. Catalog Product does not gain an
arbitrary path, and Commerce stores no file bytes or user path. Current catalogue presentation
resolves the asset's current version; historical Sale/document snapshots remain governed by
their own immutable-version rules.

Commerce owns data-driven `CommercialTag` plus ProductCommercialProfile and MarketplaceListing
join tables. Tags have code, name and active state; examples such as Natal or Páscoa are rows,
never booleans or enums. This supports filtering only. Date windows, budgets, ads and a
campaign-management aggregate are out of S8B.

ChannelOffer's optional seller-paid shipping estimate is commercial planning data. `null` means
unknown/unavailable; explicit zero means no seller-paid estimate under the configured policy.
It never duplicates or updates `Sale.shipping_amount`, which remains the realized Sale snapshot.
S8C may refresh provider-derived shipping facts only through explicit provenance and freshness.

### 11. Permissions, audit and concurrency

Permissions are:

- `commerce:read`: Owner, Operator, Viewer;
- `commerce:manage`: Owner, Operator;
- `commerce:accounts:manage`: Owner only, because account/credential-reference configuration is
  security-sensitive.

Viewer receives read compositions but no offer, account, linkage or correction mutation control.
Account reads never reveal credential-reference internals. Existing audit infrastructure records
offer activation/deactivation/price/shipping changes, account create/update/deactivate,
capability changes, and listing link/unlink/manual correction.

ChannelOffer, MarketplaceAccount and MarketplaceListing are aggregate roots with explicit
Version. Manual commands require expected Version. A future sync upsert is idempotent by durable
external identity and may update observed fields only; it never changes operator-owned offer or
linkage fields. Version conflict reloads/retries observed facts and never overwrites concurrent
operator intent.

### 12. Database and API surface

The future S8B migration creates only the `commerce` schema and Commerce tables, plus the
`PRODUCT_IMAGE` Settings reference-data row and the Commerce observation-retention setting. It
does not create provider-specific tables.

Conceptual route families are:

```text
GET  /api/commerce/catalog
GET  /api/commerce/offers
POST /api/commerce/offers
PUT  /api/commerce/offers/{id}
POST /api/commerce/offers/{id}/activate|deactivate

GET  /api/commerce/published-items
POST /api/commerce/published-items/{id}/link|unlink

GET  /api/commerce/marketplace-accounts
POST /api/commerce/marketplace-accounts
PUT  /api/commerce/marketplace-accounts/{id}
POST /api/commerce/marketplace-accounts/{id}/activate|deactivate

POST /api/commerce/channel-economics/preview
```

These are local provider-neutral operations. No publication, provider discovery, external write,
polling, webhook or operational-order endpoint exists in S8B.

### 13. Frontend and navigation

`/products` remains the technical Product/BOM registry. S8B later adds `/catalog` for Catálogo
Comercial and `/published-items` for Itens Publicados. Marketplace Accounts is an Owner-managed
Commerce/configuration surface. The names remain distinct in navigation:

```text
Produtos e receitas -> technical Product/BOM
Catálogo Comercial  -> Product/channel commercial composition
Itens Publicados    -> external listing composition
```

The future directional hierarchy (Visão Geral, Comercial, Produtos, Produção, Estoque,
Financeiro, Canais, Relatórios, Configurações) is a UX backlog, not authorization to expose
unbuilt modules. S8B implementation uses current conventions for usable screens; a global design
system/sidebar/dashboard modernization remains outside S8B.

### 14. Future boundaries

S8C.0 must inspect official material for Mercado Livre, Shopee and TikTok Shop: authentication,
listing read/write, orders, fees, analytics, ads, shipping, inventory sync, rate limits, sandbox
and Brazilian/regional restrictions. No endpoint or capability is inferred from memory.

S8C owns provider connectors, publication execution, polling/webhooks and operational marketplace
order observation. `ORDERS_READ` absent means no operational orders and no automatic Sale.
Commerce observes the external order; Sales creates the canonical financial Sale.

S9 owns finished-goods stock, commitments, WIP and production need. S10 owns energy. S11 owns
actual cost/reconciliation. S12 owns reporting/provider-analytics reconciliation. S13 may add
advisory opportunity scoring. S8B stores none of those future facts.

### 15. Acceptance contracts for implementation

Each name below is a permanent Given/When/Then contract for S8B implementation:

- `DirectOnlyProductAppearsInCommercialCatalogNotPublishedItems` — given an active Product and
  ACTIVE DIRECT offer, when both reads execute, then Catalog includes it and Published Items does
  not.
- `CommercialActivationRequiresActiveProductAndActiveOffer` — given any Product/offer state
  combination, commercial active is true only when Product and at least one offer are active.
- `OneChannelOfferPerProductAndChannel` — given an existing pair, a second offer for it is
  rejected under concurrent writes.
- `InactiveOfferRetainsHistory` — deactivation preserves identity, audit and historical reads.
- `ExternalListingIdentityIsDurable` — status/account/link changes never permit reuse of one
  account + external listing identity.
- `ListingCanRemainUnlinked` — an observed listing with null Product remains queryable under
  `Não vinculadas`.
- `ExactSkuLinkCanBeConfirmedDeterministically` — an account-scoped authoritative mapping can be
  confirmed and audited.
- `AmbiguousListingDoesNotAutoLink` — fuzzy or multi-candidate matching yields NEEDS_REVIEW and
  no Product/offer mutation.
- `ExistingOfferIsNeverSilentlyOverwrittenByListing` — reconciliation preserves existing price,
  status, source and shipping policy.
- `ObservedPriceMayDivergeFromIntendedPrice` — both values and delta are returned without either
  becoming the other.
- `InactiveChannelHistoricalReferencesRemainReadable` — channel deactivation blocks new
  activation but labels existing offers/listings using the real historical channel.
- `ViewerCannotMutateCommerce` — Viewer can read Catalog/Published Items but receives 403 and no
  UI controls for mutations.
- `CommercialCatalogUsesServerSidePagination` — filters/sort/page execute in SQL with stable
  ordering and total count.
- `PublishedItemsUsesServerSidePagination` — listing filters/sort/page execute in SQL with stable
  ordering and total count.
- `NoFakeMarketplaceAccountForDirect` — DIRECT activation creates no provider/account/listing.
- `ProductDeactivationPreservesCommerceHistory` — Product inactive makes commercial-active false
  without deleting offers/listings.
- `AccountDeactivationStopsSyncNotIntent` — account inactive blocks sync/write and preserves
  listings/offers.
- `ListingStatusLinkageAndSyncAreIndependent` — ACTIVE + LINKED + ERROR is representable.
- `ManualUnlinkPreservesExternalIdentityAndObservations` — unlink removes linkage only and audits
  the previous association.
- `ChannelEconomicsUsesServerAuthorities` — client-supplied costs/fees are rejected or ignored and
  results match Costing/Pricing authorities.
- `UnavailableEnergyIsNotZero` — economics reports ENERGY missing and partial coverage, never a
  zero energy fact.
- `UnknownShippingIsNotZero` — null policy yields missing shipping coverage, not zero cost.
- `ProductImageUsesValidatedAssetPipeline` — Product images accept only Settings-validated
  PNG/JPEG/WebP assets and no arbitrary path/SVG.
- `ObservationHistoryIsBoundedAndSafe` — identical polls do not append payload copies; retention
  preserves the latest safe observation and external identity.
- `TransientProviderFailureDoesNotRewriteCapability` — given provider SUPPORTED and account
  GRANTED, a runtime outage blocks execution without changing either structural fact.
- `UnlinkedListingCannotCarryProductOrOffer` — UNLINKED persists null ProductId and ChannelOfferId.
- `NeedsReviewListingCannotCarryProductOrOffer` — NEEDS_REVIEW persists no tentative Product or
  ChannelOffer identity.
- `LinkedListingRequiresProduct` — LINKED without ProductId is rejected.
- `ListingOfferMustMatchLinkedProductAndAccountChannel` — a non-null ChannelOfferId resolves to
  the listing Product and the account's immutable SalesChannel.
- `MarketplaceAccountSalesChannelIsImmutable` — changing an existing account's SalesChannelId is
  rejected.
- `MarketplaceManualSalesDoNotImplyCompleteCoverage` — MANUAL_ENTRY facts on a marketplace
  channel never make coverage COMPLETE by themselves.
- `UnknownMarketplaceCoverageIsNotZero` — without authoritative coverage evidence, absent
  marketplace units/revenue are null with UNKNOWN coverage.
- `PartialMarketplaceSalesReturnPartialCoverage` — known manual marketplace facts are returned
  with PARTIAL coverage while relevant sources/ranges remain uncovered.
- `MissingCapabilityDoesNotRequireSchemaChange` — unsupported/unknown capability disables a
  feature using stored states.
- `AccountCapabilityIsProviderIntersectionGranted` — effective is true only for provider
  SUPPORTED and account GRANTED on an active account.
- `OrdersReadAbsentDoesNotCreateMarketplaceOrderSale` — without effective ORDERS_READ, Commerce
  creates no provider-ingested operational order and no MARKETPLACE_ORDER-origin Sale; valid
  MANUAL_ENTRY Sales are unaffected.
- `ProviderDiscoveryMayDisableUnsupportedFeatures` — S8C.0 can mark a capability unsupported
  without migration or provider-specific schema.

Future test layers are domain unit tests, PostgreSQL constraints/concurrency/integration tests,
API authorization and SQL-pagination tests, frontend Vitest permission/read-model tests and one
Playwright local-only journey: create Product, activate DIRECT offer, verify Catalog inclusion and
Published Items exclusion, create/link a manual observed listing fixture, verify Published Items,
then deactivate the offer and verify commercial-active semantics. It performs no external call.

## Alternatives considered

1. **Put offer/listing fields on Product.** Rejected: it couples technical master data to
   provider/commercial lifecycles and cannot represent multiple channels/accounts/listings.
2. **Treat MarketplaceListing as the offer.** Rejected: external observation and VERCE intent may
   legitimately diverge in price and state.
3. **Create a materialized CommercialCatalog table.** Rejected: it duplicates current Product,
   Pricing and Commerce truth before measured need. The catalog is a paginated composition.
4. **Represent DIRECT through a fake provider/account/listing.** Rejected: it invents external
   identity and pollutes Published Items.
5. **Auto-link fuzzy SKU/title matches.** Rejected: a wrong Product association can activate the
   wrong price and later create incorrect Sales.
6. **Persist raw provider payloads indefinitely.** Rejected: secret/PII risk and unbounded data
   swamp. Only safe normalized observations are retained.
7. **Build a provider fee cache now.** Rejected: cache keys and TTL cannot be correct before
   provider discovery.
8. **Copy Pricing/Costing formulas into Commerce or React.** Rejected: it creates conflicting
   financial authorities.
9. **Implement the global visual redesign in S8B.** Rejected: it is cross-cutting S15 backlog and
   would obscure the Commerce domain delivery.

## Consequences

S8B can implement a useful local-first commercial catalogue and manual listing foundation without
pretending any provider integration exists. The price/state divergence that real connectors will
reveal is representable from day one. The model is slightly broader than a single offer table,
but each extra state exists to prevent a known ambiguity: provider support versus account grant,
observed status versus sync health, and link state versus external identity.

Provider fee caching and publication requests remain intentionally absent. S8C may add them after
official discovery without changing ChannelOffer, MarketplaceAccount or MarketplaceListing
identity.

## Supersedes / clarifies

Clarifies ADR-0021's future `marketplace_account_id` seam: Commerce owns that account identity,
while Sales remains canonical financial truth. Extends ADR-0005 without changing it: marketplace
rules remain data and DIRECT remains the zero-fee normal channel. Reuses ADR-0015's safe asset
pipeline and ADR-0010/ADR-0011 audit/concurrency contracts.

## Compliance checks

- Architecture tests enumerate Commerce as the fifteenth module and reject business-module
  project references/navigation properties.
- PostgreSQL tests prove both ordinary unique identities, same-schema FKs, checks and concurrent
  duplicate rejection.
- Unit/integration tests implement every named acceptance contract in §15.
- API tests prove permissions and never expose credential references/secrets.
- Query tests prove SQL pagination/filtering/stable ordering for both read compositions.
- Model-drift and migration review prove exactly one S8B migration when implementation begins;
  this architecture delivery creates none.
- S8C.0 documentation must cite official provider sources before any connector code is accepted.
