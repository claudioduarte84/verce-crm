# ADR-0028 — S8C.2 SKU-Mapping Deactivation Replay Contract

- **Status:** Accepted (pending external review)
- **Date:** 2026-10-08
- **Sprint:** S8C.2 (Stream C, Slice 1)
- **Builds on:** [ADR-0025](ADR-0025-s8c2-mercado-livre-listing-read-integration.md) §D.3 and §J (not amended),
  [ADR-0011](ADR-0011-identifiers-and-concurrency.md) §2 (aggregate `version`)
- **Decision source:** VERCE3D-S8C2-SLICE1-OPUS-ADR-GATE-037, answering the Terra gate 036 stop
  `S8C2_SLICE1_ARCHITECTURE_CLARIFICATION_REQUIRED`

## Context

ADR-0025 §J freezes `POST /sku-mappings/{mappingId}/deactivate` with body `{version}` → 204, and §D.3
freezes the lifecycle `ACTIVE → INACTIVE` (`OPERATOR_DEACTIVATED`), with INACTIVE terminal and never
reactivated. Neither document says what happens when the named mapping is **already INACTIVE**.

Slice 1 (mission 035) answered with a new public code `SKU_MAPPING_NOT_ACTIVE` (409). That code is not
in the frozen vocabulary (STATE-MACHINES §6, the C3 pt-BR catalog), so Terra stopped the gate.

Repository facts that constrain the answer:

- `version` is the aggregate revision (ADR-0011 §2): it starts at 1, advances by exactly one per unit
  of work that changes the root, and only `AggregateVersionInterceptor` mutates it. It is an EF
  concurrency token, so an update carries `WHERE version = <loaded value>`.
- A mapping has exactly two mutations, `Deactivate` and `Replace`. Both are terminal. A mapping created
  through B7 is therefore at version 1 while ACTIVE and at version 2 once INACTIVE, and it never
  changes again.
- The unit of work saves only when the change tracker has pending writes. A command that changes
  nothing commits an empty transaction: no version bump and no generic audit row.
- The repository already treats a repeated command whose effect is already achieved as converging,
  not failing (STATE-MACHINES §4.3 production-order creation, §5 "idempotent deletion" on disconnect).

## Decision

### 1. Outcome table

Checks run in this order: authorization, antiforgery, strict body, existence, version, state.

| # | Situation | HTTP | Code | Effect |
|---|---|---|---|---|
| A | Mapping ACTIVE, `version` = current | 204 | — | ACTIVE → INACTIVE `OPERATOR_DEACTIVATED`; `invalidated_at/by` set; version +1; generic audit row |
| B | Mapping INACTIVE (either reason), `version` = current | **204** | — | **No-op.** Nothing is written: no version change, no `invalidated_*` change, no audit row |
| C | `version` ≠ current, whatever the state | 409 | `CONCURRENCY_CONFLICT` | Nothing is written |
| D | Mapping does not exist | 404 | — | Nothing is written |
| E | Concurrent requests with the same current version on an ACTIVE mapping | one 204 (A), every other one 409 `CONCURRENCY_CONFLICT` | — | Exactly one transition, one audit row, version +1 |

`SKU_MAPPING_NOT_ACTIVE` is **removed** from the public contract. It is not replaced by another code;
the replacement behavior is row B.

### 2. Why the version check precedes the state check

The version check is what protects the operator's decision. With only two terminal mutations, a
stale version on an INACTIVE mapping means someone else invalidated it after this client last read it.
That someone may have **replaced** it (S → P2). Answering 204 would let the operator believe SKU S
has no active authority while S → P2 is active. 409 `CONCURRENCY_CONFLICT` makes the client reload
and see the replacement. Idempotency never overrides concurrency control.

Consequence, accepted explicitly: a network retry of a deactivation that already committed carries
the pre-deactivation version and therefore receives 409 `CONCURRENCY_CONFLICT`. The 204 no-op (row B)
applies only to a client whose request carries the current (post-invalidation) version, that is, one
that already observed the INACTIVE state. A blind retry cannot be distinguished from a concurrent
replacement without new state, and this decision adds none.

### 3. Why row B is 204 and not 409

- The frozen §J contract lists 204 as the only success and defines no refusal for this case.
- The requested end state ("this mapping grants no auto-link authority") already holds and cannot
  change again (INACTIVE is terminal). Refusing would report an error for a state the operator asked for.
- No existing public code fits. `CONCURRENCY_CONFLICT` is false here (the version matches), and
  `SKU_MAPPING_CONFLICT` means "an ACTIVE mapping exists".
- It keeps the frozen C3 catalog and STATE-MACHINES §6 unchanged.

The no-op keeps the original invalidation as evidence: `invalidated_reason` (including
`REPLACED_BY_NEW_MAPPING`), `invalidated_at` and `invalidated_by_user_id` are never rewritten. This
deliberately differs from `ChannelOffer.Deactivate`, which re-stamps its timestamp, because a mapping's
invalidation is terminal evidence, not a toggle.

### 4. Version semantics (answers)

1. Public `version` = the aggregate revision, the optimistic-concurrency token.
2. It advances once per committing unit of work that changes the mapping: 1 at creation, 2 at
   deactivation or replacement.
3. Deactivation returns 204 with no body; the client learns version 2 only by re-reading the list.
4. A second deactivation that carries the earlier version (1) is stale ⇒ row C.
5. A replay is distinguishable from a concurrent update only by the version the client presents.
   Version current ⇒ the client has seen the INACTIVE state (row B); version stale ⇒ conflict (row C).
6. All of this runs on the existing schema and interceptor; no migration.

### 5. Audit

- Row A writes the generic audit row for the transition (unchanged).
- Row B writes nothing: there is no tracked change, so the unit of work does not save, the
  interceptor does not bump `version`, and the generic audit does not fire. No domain event exists
  or is created for it.
- Rows C and D write nothing.

### 6. Concurrency (row E)

Two requests both load the mapping at version v (ACTIVE) and both deactivate:

- The first commit runs `UPDATE … WHERE version = v`, succeeds, and leaves version v+1.
- The second either loads after that commit (sees v+1 ≠ v ⇒ row C before any write), or its
  `UPDATE … WHERE version = v` matches zero rows after the first commit releases the row lock. EF
  raises `DbUpdateConcurrencyException`, which the service maps to `CONCURRENCY_CONFLICT`.

Every interleaving therefore yields exactly one 204 and one 409, one transition, one audit row and a
version that advanced exactly once. A concurrent replace and deactivate behave the same way: one wins,
the other gets its own conflict code (`SKU_MAPPING_CONFLICT` for confirm-with-replace,
`CONCURRENCY_CONFLICT` for deactivate).

### 7. Domain invariant unchanged

`MarketplaceAccountSkuMapping.Deactivate`/`Replace` keep rejecting an INACTIVE mapping with
`InvalidOperationException("SKU_MAPPING_NOT_ACTIVE")`. That is an internal guard against calling
the transition on terminal state; it is never a public code. The application service checks state
first and does not call the transition for row B.

## Alternatives considered

1. **Keep `SKU_MAPPING_NOT_ACTIVE` (409).** Rejected: it enlarges the frozen public vocabulary and the
   C3 catalog for a state the operator asked for, and gains no safety that row C does not already give.
2. **409 with an existing code.** Rejected: `CONCURRENCY_CONFLICT` would be false (versions match) and
   `SKU_MAPPING_CONFLICT` means something else.
3. **204 for a stale version when the mapping is already INACTIVE** (to absorb blind retries). Rejected:
   it hides a concurrent replacement and removes the concurrency control the version exists for.
4. **Re-stamp `invalidated_at/by` on replay** (the `ChannelOffer` pattern). Rejected: it rewrites
   evidence and would emit an audit row for nothing.

## Consequences

- No new public code. STATE-MACHINES §6 and the C3 catalog stay as frozen.
- Deactivation is idempotent for any client that has seen the current state, and conflict-safe otherwise.
- An at-least-once blind retry after a lost 204 surfaces as `CONCURRENCY_CONFLICT`; the UI's reload
  shows the mapping already INACTIVE. This is accepted.

## Compliance checks

Slice 1 integration tests must pin:

- row A: 204, reason `OPERATOR_DEACTIVATED`, `invalidated_*` set, version +1, audit row written;
- row B: 204 for an `OPERATOR_DEACTIVATED` mapping and for a `REPLACED_BY_NEW_MAPPING` mapping, each at
  its current version; afterwards reason, `invalidated_at`, `invalidated_by_user_id` and version are
  unchanged and no audit row was added;
- row C: 409 `CONCURRENCY_CONFLICT` for a stale version on an ACTIVE mapping and on an INACTIVE one;
  nothing written;
- row D: 404;
- row E: two concurrent deactivations with the same version yield exactly one 204 and one 409
  `CONCURRENCY_CONFLICT`, one audit row, version +1;
- the public response never contains `SKU_MAPPING_NOT_ACTIVE`.

Changing a row requires a superseding ADR.
