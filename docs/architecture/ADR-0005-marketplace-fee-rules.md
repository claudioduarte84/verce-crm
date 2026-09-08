# ADR-0005 — Marketplace Fee Rules

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-06
- **Sprint:** S0

## Context

Sales happen through direct sale and through marketplaces (Shopee, Mercado Livre, others).
Marketplaces charge a commission percentage plus a fixed fee, sometimes with minimum and
maximum caps, sometimes varying by price range, and **they change their rules regularly**.

A quote priced last March must keep the fee that applied last March, even after the marketplace
changes its table. And the brief is explicit: **never hard-code current marketplace rules**.

## Decision

### 1. Fee rules are versioned data

```
SalesChannel  ──1:N──►  FeeRule  ──1:N──►  FeeRuleVersion  ──1:N──►  PriceBracket
                        (scope,             (validity window,          (price range,
                         priority)           commission, fixed fee,     commission,
                                             min/max, application)      fixed fee)
```

- `FeeRule` defines **scope**: `ALL_PRODUCTS`, `PRODUCT_CATEGORY` or `PRODUCT`, plus a
  `priority` for resolution when several match.
- `FeeRuleVersion` defines **the values and when they applied**: `valid_from` / `valid_until`
  (half-open `[from, until)`), `commission_percent` (fraction), `fixed_fee`,
  `fixed_fee_application` (`PER_UNIT` | `PER_ORDER`), `minimum_fee`, `maximum_fee`, and a
  reserved `shipping_component`.
- `PriceBracket` defines **price-range variation** inside a version. Zero brackets means the
  version's own flat values apply at every price.

**Non-overlap is enforced by the database**, not by application code:

```sql
ALTER TABLE pricing.fee_rule_version
  ADD CONSTRAINT fee_rule_version_no_overlap
  EXCLUDE USING gist (
      fee_rule_id WITH =,
      daterange(valid_from, valid_until, '[)') WITH &&
  );
```

The same pattern guards `price_bracket` (over `numrange`) and
`energy.energy_tariff_version`. Two overlapping windows would make "which fee applied on this
date?" ambiguous — an ambiguity that must be impossible, not merely unlikely.

### 2. Direct sale is not a special case

"Venda Direta" is a `SalesChannel` of kind `DIRECT` with a seeded `FeeRuleVersion` of
`commission_percent = 0` and `fixed_fee = 0`. The pricing engine has **one** formula:

```
salePrice = (unitTotalCost + fixedFeePerUnit) / (1 - (commissionPercent + desiredMargin))
```

which collapses to `unitTotalCost / (1 - desiredMargin)` when the fees are zero — the brief's
direct-sale formula, exactly.

This is the single most valuable simplification in the pricing module: one code path, one set
of tests, no `if (channel.Kind == DIRECT)` that will eventually diverge. The seeded zero-fee
rule is therefore **mandatory seed data**, not a convenience.

### 3. Resolution

`FeeRuleResolver.Resolve(salesChannelId, productId, instant, price)`:

1. active `FeeRule`s for the channel whose scope matches the product
   (`PRODUCT` > `PRODUCT_CATEGORY` > `ALL_PRODUCTS`), ordered by `priority desc`, take the first;
2. its `FeeRuleVersion` where `valid_from <= instant < valid_until` (exactly one, by constraint);
3. its `PriceBracket` for the price, by the algorithm below.

Resolution happens in the **Application** layer, before the pure `PricingEngine` runs. The
engine receives plain numbers and has no temporal dependency — which is what makes it trivially
testable and makes snapshotting natural (the resolved values *are* the snapshot).

If no rule resolves, the operation fails with `FEE_RULE_NOT_FOUND`. It does **not** silently
default to zero: a missing fee rule priced as 0% commission would quietly under-price every
marketplace item.

### 4. The bracket circularity

The fee depends on the price; the price depends on the fee. Resolved by a **consistency
search**, specified in
[CR-07.5](../CALCULATION-RULES.md#cr-075--bracket-resolution-fee-depends-on-price-price-depends-on-fee):

1. for each bracket, compute the price implied by its own fees;
2. a bracket is *consistent* when the price it implies falls inside its own range;
3. exactly one consistent → use it; several → use the lowest price (deterministic and
   customer-favourable); none → pin to the lowest bracket boundary that still achieves the
   desired margin; still none → fail with `PRICING_BRACKET_DISCONTINUITY` and require a manual
   price.

Iterating to a fixed point was rejected: with discontinuous fee steps it can oscillate forever,
and "loop until it settles" is not a defensible answer to "why is this price what it is".
Failing loudly is.

### 5. Fee clamps are visible

When `minimum_fee` or `maximum_fee` changes the commission actually charged, the effective
margin no longer equals the desired margin. The snapshot records `fee_clamp_applied` (`MIN` /
`MAX`) and the breakdown shows it. A margin that silently misses its target is the failure this
product exists to prevent.

## Alternatives considered

- **Hard-coded fee constants or a strategy class per marketplace** — rejected outright by the
  brief and by common sense: every rule change would be a deploy, and historical quotes would
  be recomputed with today's constants.
- **A single mutable fee row per channel** — rejected: no history, so every past quote silently
  changes when a marketplace updates its table.
- **A rules engine / expression language** (store a formula string, evaluate it) — genuinely
  flexible, and rejected: it turns a reviewable data model into user-editable code, defeats
  static analysis, complicates snapshots (you must snapshot the expression *and* its inputs),
  and introduces an evaluation surface with security implications. The bracket model covers
  every fee structure the target marketplaces actually use.
- **Snapshotting only `fee_rule_version_id` and re-reading it later** — rejected in
  [ADR-0003](ADR-0003-cost-and-price-snapshots.md): historical correctness must not depend on a
  temporal query staying correct forever.

## Consequences

**Positive:** rule changes are data entry; history is exact; direct sale and marketplaces share
one tested formula; overlapping validity windows are impossible.

**Negative:** more tables than a single fee column, and an operator must understand "version"
versus "bracket" — mitigated by a UI that presents a timeline of "what we paid, since when".
Brackets add real complexity (CR-07.5) and are therefore not activated until S8; the model
exists from S5 so nothing has to be redesigned when they are.

## Compliance checks

- Architecture/lint check: no string literal naming a marketplace (`"Shopee"`, `"Mercado
  Livre"`) appears anywhere in `src/` outside seed data and tests.
- Integration test: inserting an overlapping `fee_rule_version` is rejected by the database.
- Integration test: a quote priced under version 1 keeps its fee after version 2 takes effect.
- Unit test: direct sale (0%, R$ 0,00) produces exactly `cost / (1 - margin)`.
