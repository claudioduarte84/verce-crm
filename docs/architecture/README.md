# Architecture Decision Records

An ADR records **why** a decision was made, what was rejected, and what it costs.

## Rules

1. ADRs are **append-only**. Never rewrite the decision of an accepted ADR — write a new ADR
   that supersedes it, and mark the old one `Superseded by ADR-XXXX`.
   **One exception:** an ADR that is *factually wrong* (it asserts behaviour the platform does
   not have) is corrected in place, because leaving a false statement standing would mislead
   every future reader. The correction carries a dated `> Corrected …` note stating what was
   wrong and why, so the history is preserved rather than erased. ADR-0011 (`xmin` cannot detect
   child mutations; "every table has a uuid PK" was false) and ADR-0012 (a single `SaveChanges`
   cannot persist handler-created aggregates; catching a unique violation aborts a PostgreSQL
   transaction) were corrected this way on 2026-09-07 after the external gate.
2. Any deviation from an accepted ADR requires a superseding ADR. Code that contradicts an ADR
   is a defect, not a new decision.
3. Every ADR states its rejected alternatives. An ADR without alternatives is a note, not a
   decision.
4. Every ADR ends with compliance checks — the tests or reviews that keep it true.

## Status values

`Proposed` · `Accepted` · `Superseded by ADR-XXXX` · `Deprecated`

All S0 ADRs are **Accepted (pending external review)**: the architecture agent does not approve
its own work; a different model reviews this delivery.

## Index

| ADR | Title | Status | Key rule |
|---|---|---|---|
| [0001](ADR-0001-system-architecture.md) | System Architecture | Accepted | Modular monolith, one DbContext, schema per module, boundaries enforced by tests |
| [0002](ADR-0002-money-precision-and-rounding.md) | Money, Precision and Rounding | Accepted | `decimal`/`numeric`, percent as fraction, half-up, engine is the only calculator |
| [0003](ADR-0003-cost-and-price-snapshots.md) | Cost and Price Snapshots | Accepted | Typed columns for reporting + JSONB for explanation; history never recomputed |
| [0004](ADR-0004-quote-numbering-and-revisioning.md) | Quote Numbering and Revisioning | Accepted | Atomic daily counter; bijective base-26 suffix; edit = clone + new revision |
| [0005](ADR-0005-marketplace-fee-rules.md) | Marketplace Fee Rules | Accepted | Fees are versioned data; direct sale is the zero-fee case of one formula |
| [0006](ADR-0006-estimated-vs-actual-cost.md) | Estimated vs Actual Cost | Accepted | Two storage locations, variance always derived |
| [0007](ADR-0007-document-template-engine.md) | Document Template Engine | Accepted | JSON template → HTML → Chromium → PDF; versions immutable once published |
| [0008](ADR-0008-ai-integration-and-secret-handling.md) | AI Integration and Secret Handling | Accepted | Key backend-only, encrypted, never logged; aggregated payload, no PII |
| [0009](ADR-0009-authentication-strategy.md) | Authentication Strategy | Accepted | Identity + cookies, single org, multi-user, permission constants |
| [0010](ADR-0010-audit-strategy.md) | Audit Strategy | Accepted | Business history + generic audit log; no event sourcing |
| [0011](ADR-0011-identifiers-and-concurrency.md) | Identifiers, Concurrency and Deletion | Accepted · **revised 2026-09-07** | PK by table category (B-006); explicit aggregate `version`, not `xmin` (B-002); soft delete only on master data |
| [0012](ADR-0012-domain-events-and-outbox.md) | Domain Events, Unit of Work and the Outbox | Accepted · **revised 2026-09-07** | Multi-wave UoW algorithm (B-001); full outbox lifecycle with lease, retry, terminal `FAILED`, idempotency (B-003) |
| [0013](ADR-0013-expense-inventory-double-counting.md) | Expense, Inventory and Double Counting | Accepted | Accounting treatment flag; one lot, one expense; two separate reports |
| [0014](ADR-0014-frontend-architecture-and-theming.md) | Frontend Architecture and Theming | Accepted | Frontend never computes money; themes are token sets, never behaviour |
| [0015](ADR-0015-brand-assets-and-application-branding.md) | Brand Assets and Application Branding | Accepted | Versioned assets; documents reference the version; eight-stage upload pipeline; no SVG in v1 |
| [0016](ADR-0016-document-render-snapshots.md) | Document Render Snapshots and Immutability | Accepted | Layout, branding and content frozen at issue; issued documents never regenerated |
| [0017](ADR-0017-inventory-ledger-and-unit-normalization.md) | Inventory Ledger, Non-Negative Stock Concurrency, and Unit Normalization | Accepted (pending external review) | Append-only signed-delta ledger; non-negative stock via the aggregate's own `Version` (H-007 A); closed unit conversion table; Filament is optional `Supply` metadata, not a separate aggregate |
| [0018](ADR-0018-s4-stateless-cost-laboratory-and-acquisition-basis.md) | S4 Stateless Cost Laboratory and Acquisition Basis | Accepted (pending external review) | Pure stateless engine; weighted average of cost-bearing purchase receipts; explicit simulation overrides; targeted wastage Setting compatibility migration |
| [0019](ADR-0019-s5-product-recipe-and-pricing-engine.md) | S5 Product Recipe Persistence and Pricing Engine Scope | Accepted (pending external review) · **revised 2026-09-19** | Current-state (non-versioned) Product Recipe reusing S4's CostEngine unmodified; flat FeeRule (no brackets/scoping before S8); rounding policy applied to the raw price; NINETY_NINE ceiling fix, organization-date fee resolution, self-describing Product Pricing snapshot contract and the DIRECT zero-fee invariant (Terra S5 independent review) |
| [0020](ADR-0020-s6-quote-conversion-and-per-order-allocation.md) | S6 Quote Revision Lifecycle, Commercial Conversion and PER_ORDER Fee Allocation | Accepted (pending external review) | Closes the three S6-entry debts: the full supersession × production-order matrix with `has_pending_revision` advisory-only, canceled-order consumption never reversed, revision *creation* never gated on production state, and the minimum Production Core moved into S6 to keep `QuoteApproved` transactionally correct (H-001); quote-level commercial outcome derived and non-retroactive, with only the win itself absorbing/monotonic, approval never auto-creates a Sale (H-009 A); `PER_ORDER` fee charged once and allocated across lines by line cost with floor + largest-remainder residual cents (H-004 remainder) |
| [0021](ADR-0021-s8a-sale-conversion-and-expense-model.md) | S8A Sale Conversion and Expense Model | Accepted (pending external review) | Canonical Sale conversion/manual entry, durable external-order seam, cancellation, immutable copied financial snapshots and Expense double-counting guards |
| [0022](ADR-0022-pricing-override-discount-bracket-precedence.md) | Pricing Override, Discount and Bracket Precedence | Accepted (pending external review) | One-channel order, manual override and post-discount fee-bracket precedence with explainable immutable snapshots |
| [0023](ADR-0023-s8b-commerce-foundation.md) | S8B Commerce Foundation | Accepted (pending external review) | Fifteenth Commerce module; ChannelOffer intent separated from MarketplaceListing observation; provider-neutral capabilities, reconciliation, read compositions and channel economics without provider integration |

| [0024](ADR-0024-s8c1-marketplace-connector-authorization-foundation.md) | S8C.1 Marketplace Connector and Authorization Foundation | Accepted — approved and frozen; implemented in `01e5d6a` (S8C.1 COMPLETE) | G-01..G-08 and RT-01..RT-03: bound sessions, one PostgreSQL operation arbiter, conservative duplicate identity handling, new secret generation after store loss, S8B upgrade and test-only fake; compliance checks in ADR |
| [0025](ADR-0025-s8c2-mercado-livre-listing-read-integration.md) | S8C.2 Mercado Livre Listing Read Integration and Normalized Observations | Accepted — approved and frozen; implementation not started | S8C.1 defects D-01..D-03 first; real ML adapter on the S8C.1 port with NOT_SENT only before `SendAsync` (Astra G01_CLOSED); G-08 HTTP layer; provider-neutral listing reader; lease-token-fenced runs with durable run items and FAILED_ONLY retry runs; explicit SKU mappings as sole auto-link authority; item-derived run outcomes and listing-sync health; no quantity, writes, webhooks or polling · §E.6 runtime-guard wording narrowly clarified by ADR-0026 |
| [0026](ADR-0026-s8c2-listing-sync-authority-continuation.md) | S8C.2 Listing-Sync Authority Continuation and Item-Derived Terminal Outcome | Accepted (pending external review) | One shared authority predicate (coherent CONNECTED identity or one direct proven REFRESH continuation; no history scan; null snapshot has no authority) checked at effect time under the connection row lock; terminal outcome from run items only; finish runtime write authority-gated; ADR-0024 unchanged |
| [0027](ADR-0027-s8c2-listing-sync-required-action.md) | S8C.2 Listing-Sync `requiredAction` Contract | Accepted (pending external review) | Fills ADR-0025 §J: `requiredAction` = explicit current action outside listing sync; ADR-0024 seven-value vocabulary only; availability from the §G.2 blockedReason (+ connection RequiredAction for AUTHORIZATION_NOT_USABLE); run-level only for run-only evidence (403, credential rejection/loss, store outage, volume, defect) unless superseded/re-consented; fences and transient states → NONE |
| [0028](ADR-0028-s8c2-sku-mapping-deactivation-replay.md) | S8C.2 SKU-Mapping Deactivation Replay Contract | Accepted (pending external review) | Fills ADR-0025 §J for `POST /sku-mappings/{id}/deactivate`: version checked before state; INACTIVE + current version → 204 no-op (no write, no version bump, no audit); stale version → 409 `CONCURRENCY_CONFLICT`; unknown → 404; concurrent requests → one 204, one 409; `SKU_MAPPING_NOT_ACTIVE` is not a public code |
| [0029](ADR-0029-s8c2-gate061-clarifications.md) | S8C.2 Gate 061 Clarifications | Accepted | Complements ADR-0025 (supersedes nothing): exact-lexeme `ObservedPrice` validation (§B.3); whole-call `NON_RETRYABLE` during enumeration stops as `LISTING_ENUMERATION_INCOMPLETE` with runtime untouched (§E.3); runtime effect only for a run finishing without a stop reason (§E.6); 30 s monotonic safe-read budget including the body read (§F.4); no forward-only migration unless a future feature reads `latest_observation_fingerprint` as data. |
| [0030](ADR-0030-s8c2-gate065-credential-and-retry-clarifications.md) | S8C.2 Gate 065 Clarifications | Accepted | Complements ADR-0025/0029 (supersedes nothing): stored credential material is validated by its adapter before any request; unusable material ⇒ existing `CREDENTIAL_CONFIRMED_LOST` (run) / `PROBE_CREDENTIAL_UNREADABLE` (probe) / `REFRESH_TOKEN_UNAVAILABLE` (refresh); local pre-send failures are never provider evidence; executable Retry-After wait `d = max(base + jitter, floor)`. |

## Template

```markdown
# ADR-XXXX — Title

- **Status:** Proposed | Accepted | Superseded by ADR-YYYY
- **Date:** YYYY-MM-DD
- **Sprint:** SN

## Context
The forces at play. What makes this decision necessary and non-obvious.

## Decision
What we will do, precisely enough to implement.

## Alternatives considered
Each rejected option and the specific reason it was rejected.

## Consequences
Positive, negative and neutral. Be honest about the negative.

## Compliance checks
The tests or reviews that keep this decision true.
```
