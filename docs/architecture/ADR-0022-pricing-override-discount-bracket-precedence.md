# ADR-0022 — Pricing Override, Discount and Bracket Precedence

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-22
- **Sprint:** S8A.ARCH

## Context

S5 established fee versions, clamps and immutable Quote snapshots. S8A activates PriceBracket
and must make override, discount, fee basis and explainability deterministic without repricing
historical revisions.

## Decision

V1 has one SalesChannel per QuoteRevision and Sale. Every item carries that same channel; an
item-level override is rejected with `QUOTE_MIXED_CHANNEL_NOT_SUPPORTED`. This keeps a PER_ORDER
fee attached to one commercial order; channel comparison belongs to S8B Channel Economics.

For each new pricing computation or revision the normative pipeline is:

1. Resolve `SalesChannel -> FeeRule -> FeeRuleVersion`.
2. Resolve the suggested-price bracket with CR-07.5 consistency search.
3. Set `unitPrice = manualPriceOverride ?? suggestedPrice`.
4. Calculate `discountAmount` from `unitPrice`.
5. Set `netUnitPrice = unitPrice - discountAmount`.
6. Re-resolve the fee bracket by `PRICE_CONTAINMENT` on `netUnitPrice`.
7. Calculate `commissionAmount = round2(netUnitPrice * commissionPercent)`, then apply the
   MinimumFee/MaximumFee clamp.
8. Calculate `effectiveMargin` under CR-08.4 from frozen rounded values.

An override uses price-containment resolution because price is already an input; it never keeps a
stale suggested-price bracket. Containment is `min_price <= netUnitPrice < max_price`, with the
open-ended final bracket and PostgreSQL non-overlap exclusion constraint. Commission basis is the
final buyer-paying `netUnitPrice` after discount, never suggested or pre-discount price. Clamp is
applied after rounding; MinimumFee and MaximumFee were already S5 capabilities, not new S8 work.

New QuoteItem snapshots preserve `bracket_id`, `bracket_resolution` (`CONSISTENCY_SEARCH`,
`PRICE_CONTAINMENT`, `VERSION_FLAT`, `BOUNDARY_PINNED`), `fee_basis_amount`, `priceOverridden`,
`discountApplied`, and `feeClampApplied` (`MIN|MAX|null`) so explanation does not reconstruct old
rules. Historical QuoteRevisions remain immutable and are never repriced.

## Consequences

`pricing.price_bracket` is activated in S8A with fee-rule-version identity, price bounds,
commission/fixed/clamp values and sort order. The target migration is `AddS8ASalesAndExpenses`.
The calculation rules named below are normative for new computations only.

## Supersedes / clarifies

Clarifies and supersedes the pre-S8 wording of CR-07.5, CR-07.6, CR-08.1, CR-08.2 and CR-08.4
where it implied a pre-discount or suggested-price fee basis. Resolves H-003 and H-005.
