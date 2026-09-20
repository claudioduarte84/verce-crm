# ADR-0020 — S6 Quote Revision Lifecycle, Commercial Conversion and PER_ORDER Fee Allocation

- **Status:** Accepted (pending external review)
- **Date:** 2026-09-20
- **Sprint:** S6 entry (decisions only — no runtime code, no migration)

> **Correction pass (2026-09-20, same day).** Independent review
> (`VERCE3D-S6-ENTRY-CODEX-SOL-REVIEW-001`) found three blocking contradictions and three
> secondary imprecisions in the first draft of this ADR and its supporting docs. All six are
> corrected in place, since the ADR is still pending its first external acceptance:
> **BLOCKING-01** revision creation was gated on production-order state in `STATE-MACHINES §2`
> while this ADR said creation is always allowed — the stray guard is removed (§2 there, A.1/A.7
> here). **BLOCKING-02** several places described "editing a draft revision" as if a persisted
> revision were mutable — reworded throughout to clone-candidate-then-persist, and the PER_ORDER
> cross-line dependency this creates is now named explicitly (new **§A.7**). **BLOCKING-03**
> approval requires synchronous `ProductionOrder` persistence in the same transaction
> (ADR-0012 §2), which cannot be true if `ProductionOrder` does not exist until S9 — the minimum
> persistence this invariant needs moves into **S6** (new **§A.8**), S9 keeps the operational
> expansion. **MEDIUM-01/02** the commercial-outcome text overstated "monotonic" (only the *win*
> is absorbing; the current classification of a never-won quote is not) and blurred "the unit is
> the quote" with the fact that a quote can be counted in more than one period — both reworded in
> **§B.2** (and the same-period cardinality then frozen by the second pass below). **LOW-01** "the customer pays the fee exactly once" and "the same percentage" overstate
> what is exactly true — reworded in **§C.1/§C.3**. The allocation algorithm, its vectors (§C.4,
> §C.8) and every other already-approved decision listed in the review's preservation list are
> **unchanged**.
>
> **Second correction pass (2026-09-20, same day).** Re-review
> (`VERCE3D-S6-ENTRY-CODEX-SOL-REREVIEW`) found one remaining blocker, `REREVIEW-BLOCKING-01`:
> §B.2's per-transition loss counting contradicted its own "never counted twice within the same
> period" guarantee, and the product owner had to choose the cardinality. They selected
> **Option B — deduplicate by `QuoteId` + reporting period, `WON` before `LOST`** — applied in
> §B.2 here and normatively in [DATA-DICTIONARY §4.1](../DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão).
> No other decision in this ADR changed.

> **Scope.** This ADR closes the three architecture-debt rows that gate S6:
> **H-001** (revision ↔ production lifecycle), **H-009 A** (commercial conversion semantics) and
> the **H-004 remainder** (`PER_ORDER` cross-line allocation). It decides semantics and freezes
> contracts; it does not create `Quote`, `QuoteRevision`, `QuoteItem`, any endpoint, any event
> class or any schema. Implementation is S6's own delivery, after independent review of this ADR.

## Context

S5 froze pricing for a single product against a single channel
([ADR-0019](ADR-0019-s5-product-recipe-and-pricing-engine.md)). S6 builds the Quote Engine, and
three decisions must exist **before** it starts, because each of them shapes the quote state
machine rather than extending it:

| Debt | Recorded concern (verbatim from [ARCHITECTURE-DEBT](../ARCHITECTURE-DEBT.md) §1) |
|---|---|
| **H-001** | "behaviour when a superseded revision has an order in each state; whether `has_pending_revision` blocks queue actions; what happens to actual consumption already recorded against a canceled order" |
| **H-009 A** | "**Commercial** conversion semantics: quotes reopened after a terminal state, quotes whose current revision returns to `GENERATED`" |
| **H-004 (remainder)** | "`PER_ORDER` fixed-fee allocation: rounding of `fixedFee / quantity` **across lines**, residual-cent assignment, behaviour when quantity changes on a revision" |

A large part of the surrounding design is **already frozen** and is not reopened here:
[STATE-MACHINES §1](../STATE-MACHINES.md#1-quote-revision-status) (the seven revision states, the
supersession pointer, the transition table, the expiration job),
[§2](../STATE-MACHINES.md#2-revision-creation-edit-semantics) (edit = clone + new revision),
[§3](../STATE-MACHINES.md#3-altering-an-approved-quote) (altering an approved quote),
[§4.3](../STATE-MACHINES.md#43-idempotency-of-creation) (one production order per approved
revision), [ADR-0003](ADR-0003-cost-and-price-snapshots.md) (snapshots),
[ADR-0004](ADR-0004-quote-numbering-and-revisioning.md) (numbering and revisioning) and
[ADR-0012](ADR-0012-domain-events-and-outbox.md) (events and the outbox). What follows adds the
missing determinism and corrects three internal contradictions discovered while reading them.

---

## Part A — H-001: revision ↔ production lifecycle

### A.1 What was already decided (restated, not changed)

- **Status lives on `QuoteRevision`, never on `Quote`** (STATE-MACHINES §1). A quote has no
  status; it has a current revision that has one.
- **A revision is immutable once created**; only `Status`, `SupersededByRevisionId` and appended
  history rows ever change (DOMAIN-MODEL §8). Commercial change means a **new revision**, never
  a rewrite. This ADR **confirms** that principle rather than restating it as new.
- **Approval is tied to a specific revision**, not to the quote — every price, cost, customer and
  fee snapshot lives on the revision (DOMAIN-MODEL §8, ADR-0003). This ADR **confirms** it.
- **`SUPERSEDED` means "replaced before being decided"** (STATE-MACHINES §1.2). Exactly one
  deterministic semantic already exists and is kept: `SupersededByRevisionId` is set on the
  previous revision whenever a newer one is created, *regardless of status*, while the `Status`
  transition to `SUPERSEDED` happens **only** for revisions that were still undecided
  (`GENERATED`, `SENT`, `NEGOTIATING`). An `APPROVED` revision that is replaced keeps
  `APPROVED` forever and merely receives the pointer. "Is this revision current?" is
  `SupersededByRevisionId IS NULL`; "how did it end?" is `Status`. These are two different
  questions and must never be collapsed into one column.
- **Production eligibility and idempotency**: approval raises `QuoteApproved`, handled
  synchronously by Production, which creates at most one `ProductionOrder` per revision;
  `production.production_order.quote_revision_id` is **UNIQUE** and the insert uses
  `ON CONFLICT DO NOTHING` (STATE-MACHINES §4.3). The idempotency key is therefore the
  **approved `QuoteRevisionId`**. A retry, a replayed event, a double-clicked button and a
  concurrent approval all converge on exactly one order. This ADR **confirms** it and adds no
  second mechanism.

### A.2 The supersession × production-order matrix (new)

H-001 asks for "behaviour when a superseded revision has an order in each state". §3 covered two
grouped cases and left `CANCELED` uncovered. The complete, deterministic matrix — where `R(n)` is
the approved revision that owns the order and `R(n+1)` is the newer revision:

| `R(n)` order state | Creating `R(n+1)` | Approving `R(n+1)` |
|---|---|---|
| *(no order)* | allowed | create order for `R(n+1)` |
| `QUEUED` | allowed; set `HasPendingRevision` | cancel `R(n)`'s order (`CancellationReason = SUPERSEDED_BY_REVISION`, `SupersededByOrderId` = new order); create order for `R(n+1)` |
| `IN_PRODUCTION` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `READY` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `SHIPPED` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `DELIVERED` | allowed; **no flag** | allowed; create order for `R(n+1)`; the delivered order is **left untouched** |
| `CANCELED` | allowed; **no flag** | allowed; create order for `R(n+1)`; the canceled order is **left untouched** |

Two rules govern the whole matrix:

1. **Creating a revision is never blocked by the shop floor.** The commercial conversation is
   always allowed to continue (§3 Case B step 1).
2. **A terminal order never blocks and is never mutated.** `DELIVERED` and `CANCELED` are
   terminal (STATE-MACHINES §4.1); approving a newer revision neither cancels them nor points
   `SupersededByOrderId` at the new order. `SupersededByOrderId` is set **only** when an order is
   actually canceled because it was superseded — otherwise it would claim a delivered order was
   replaced, which is false.

### A.3 Correction — the approval guard set

STATE-MACHINES §1.5 blocks approval when a previous revision's order is *"beyond `QUEUED`"*, and
§3 Case B lists `DELIVERED` among the blocking states — yet §3 step 3 says *"Once the old order
reaches a terminal state, approval proceeds."* `DELIVERED` is both "beyond `QUEUED`" and terminal,
so the two statements contradict each other.

**Resolved in favour of §3 step 3**, which is the more specific statement and the commercially
correct one:

> `PRODUCTION_ORDER_IN_PROGRESS` is raised **iff** a production order for a previous revision of
> this quote is in `IN_PRODUCTION`, `READY` or `SHIPPED` — that is, **non-terminal and not
> `QUEUED`**.

A delivered order must not block forever: a customer who received ten units and now wants five
more is ordinary repeat business, and blocking it would make the quote permanently unusable. Note
that because a new revision is a **clone** (STATE-MACHINES §2), approving it creates an order for
the new revision's **full** quantity — the system does not diff against what was already
delivered. Differential "change orders" remain the deferred capability §3 already records; the
operator is responsible for editing the cloned revision to the scope actually being ordered, and
a repeat order is usually better modelled as a **new quote**.

### A.4 `HasPendingRevision` is advisory and never blocks (new)

**Decision: `has_pending_revision` blocks nothing.** It does not gate
`QUEUED → IN_PRODUCTION` or any other production transition. It drives presentation (the
*"aguardando nova aprovação"* badge §3 already describes) and a non-blocking warning when an
operator starts work while a revision is pending.

Rationale: the flag means *a revision is pending approval*, and §3 Case A step 2 is explicit that
*"the new revision may never be approved"*. Blocking the printer on a maybe would idle the shop
floor for a negotiation that may be abandoned. If the operator does start, the consequence is
exactly the §3 Case B safety valve — the new revision's **approval** is then blocked until the
order is resolved. The warn-don't-refuse posture matches the existing precedent for stock
([CR-13.11](../CALCULATION-RULES.md#cr-1311--stock-is-advisory), "stock is advisory": an
over-consumption warning never blocks the calculation).

**Lifecycle.** The column is a **cached projection**, maintained inside the same transaction as
the revision transition that changes its value. Its authoritative definition is:

```
order.HasPendingRevision  ⇔  the order is non-terminal
                             AND the order's quote has a revision newer than the order's own
                                 source revision whose status is non-terminal
                                 (GENERATED | SENT | NEGOTIATING)
```

Consequences: set when a newer revision is created over a non-terminal order; cleared when that
newer revision reaches `CANCELED`, `EXPIRED` or `SUPERSEDED` with no other non-terminal newer
revision remaining (§3 Case A step 4 — the order simply returns to the normal queue, with **no**
state transition, since it was never canceled); consumed when the newer revision is `APPROVED`
(the matrix in A.2 then applies); never set on a terminal order.

### A.5 Actual consumption recorded against a canceled order (new)

**Decision: recorded actual consumption is an immutable physical fact. Canceling an order never
reverses it, never deletes it and never re-points it at another order.**

- The `production_order_item_actual_material` rows and the `StockMovement(OUT)` they emitted
  stand permanently. The inventory ledger is append-only
  ([ADR-0017](ADR-0017-inventory-ledger-and-unit-normalization.md)) and
  `unit_cost_at_consumption` was frozen at consumption time
  ([ADR-0006](ADR-0006-estimated-vs-actual-cost.md) §2). Material really left the shelf; the
  books must say so. This confirms STATE-MACHINES §4.2 ("consumed material stays consumed") and
  extends it to the supersession-driven cancellation path.
- Consumption is **never copied or transferred** to the superseding order. The new order starts
  with zero actual consumption and its own planned BOM, so its variance measures its own work.
- Therefore a canceled order can carry real actual cost with **no delivered output**. That cost
  remains attributable to the canceled order, and must not be silently absorbed into the
  superseding order, nor into a `Sale` that references a different revision.

**Boundary.** How that write-off is *classified* in the actual-vs-estimated cost report — the same
question as a `FAILED` item's wastage (STATE-MACHINES §4.4) — is **H-006's** scope (decision due
before S10). H-006 must honour the invariant above: the consumption exists, is attributable to
exactly one order, and is never reversed.

### A.6 Downstream contract — no new event

`QuoteApproved` (synchronous `IDomainEvent`, Quoting → Production) **is** the production
conversion contract. It already exists in DOMAIN-MODEL §15 and is modelled wave-by-wave in
ADR-0012 §7. Introducing `ProductionRequested`, `ProductionConversionIntent` or an
`ApprovedQuoteRevision` event would duplicate it with a second name for one meaning — rejected.

Its payload follows the existing rule ("events carry IDs and the minimum payload, never whole
aggregates"): `QuoteId`, `QuoteRevisionId`, `ApprovedAt`, `ApprovedBy`. The consumer resolves
everything else from the immutable revision.

Three **handler additions** are required so the A.4 projection cannot drift, all synchronous and
in the same transaction (an effect required to keep a projection truthful is never an
`IIntegrationEvent` — ADR-0012 §1):

| Event | Existing handlers | Added handler |
|---|---|---|
| `QuoteRevised` | Quoting | **Production** — set `HasPendingRevision` per A.4 |
| `QuoteCanceled` | Quoting, Production | **Production, corrected** — re-evaluate A.4 (clear the flag); it can never cancel an order, see below |
| `QuoteExpired` | Quoting | **Production** — re-evaluate A.4 (clear the flag) |

DOMAIN-MODEL §15 describes `QuoteCanceled`'s Production effect as *"block queued order"*. Under
the frozen state machine that effect is **unreachable**: only an `APPROVED` revision ever has an
order, and `APPROVED` is terminal, so the revision owning an order can never itself be canceled.
The reachable — and intended, per §3 Case A step 4 — effect is clearing the pending-revision flag
on the *prior* approved revision's order. Corrected accordingly.

### A.7 Revision construction is clone-then-recalculate, never an in-place edit (BLOCKING-02)

**Freeze, restated precisely because a stray sentence elsewhere implied otherwise:** a persisted
`QuoteRevision`'s commercial snapshot is immutable, full stop. There is no "draft revision" that
gets mutated in place. A commercial change is performed by:

```
1. load the current, persisted revision R(n)
2. clone its immutable snapshot into an in-memory revision candidate — not yet a revision,
   not yet persisted, no identity of its own
3. apply the requested commercial changes to the candidate
4. determine the candidate's pricing-affected lines (below) and recalculate ONLY their
   dependent derived values
5. validate the complete candidate
6. persist it atomically as R(n+1)
7. R(n+1) is now itself an immutable, persisted commercial snapshot
```

STATE-MACHINES §2 restates this as `EditQuote`'s canonical sequence — the two must never diverge.
One STATE-MACHINES §2 draft previously added a step gating **construction** on production-order
state; that was `BLOCKING-01` and is removed there. Nothing in this sequence is gated by
production state — only **approving** the resulting `R(n+1)` can be (§1.5, §3.2).

**ADR-0003's verbatim-copy rule, made precise for step 4.** ADR-0003 already requires that an
unchanged line's snapshot survive a new revision untouched. Read literally against a single line
in isolation, "unchanged" is unambiguous. Read against the **whole revision**, it needs one
narrow qualifier, because a line can be affected *through* another line:

> A line's **source** fields (product identity, description, manually authored quantity, manual
> cost, discount, override) are copied verbatim unless the operator directly changed them.
> A line's **derived** fields (suggested price, fee amounts, totals, margin) are copied verbatim
> **only if none of their declared inputs changed** — and one of those declared inputs, for a
> line in a `PER_ORDER` fee group, is the group's allocation basis and membership, which can
> change even when the line itself was not touched.

**Pricing-affected line.** A line is pricing-affected — and must recalculate its dependent derived
fields — when any of the following is true:

- its own quantity, estimated cost or pricing inputs changed;
- its own channel/fee-version context changed;
- **or** another line in the *same* `PER_ORDER` fee group changed in a way that changes the
  group's allocation basis or membership (a sibling's quantity, cost, add, remove, or a fee
  change) — see [§C.6](#c6-per_order-dependency-closure-and-full-recomputation) for the exact
  closure rule and the values this actually touches
  (`allocatedOrderFee`, `fixedFeePerUnit`, `SuggestedUnitPrice`, `lineFeeAmount`,
  `LineTotalAmount` when derived from the recalculated suggested price).

A line outside the changed fee group, or with no changed input at all, is **not** pricing-affected
and is copied verbatim — including a manual price override or discount the operator entered on it,
which CR-08.1's `unitPrice = manualPriceOverride ?? suggestedPrice` already keeps independent of
`SuggestedUnitPrice`: recalculating the suggested price of a pricing-affected, overridden line
recomputes the *suggested* value for the breakdown, but never silently erases the override, the
discount, or the customer-facing `UnitPrice` that already won over it. Whether an override should
instead force a bracket re-resolution is **H-005's** question (S8) and is not decided here — S6
recalculates only the fields this ADR and CR-07.7 already define.

A change in one `PER_ORDER` fee group never reprices a **different** fee group in the same
revision — the closure is scoped to lines sharing one `FeeRuleVersion` (§C.2). S6 implements only
the currently-supported single-group case; if H-003 (S8) later authorises item-level channel
overrides, the identical per-group algorithm and closure apply to each group independently, with
no change to either.

### A.8 The minimum Production Core moves into S6 (BLOCKING-03)

ADR-0012 §2 already freezes the transactional invariant this ADR relies on throughout Part A:
`QuoteApproved` is synchronous, `ProductionOrder` creation participates in the **same**
transaction as approval, and approval **rolls back** if that invariant cannot be established
(STATE-MACHINES §1.5, §4.3). That invariant is not optional and is **not weakened here** — but it
cannot be true of an aggregate that does not exist until three sprints later. Assigning all of
`ProductionOrder` to S9 while requiring S6 to persist one synchronously in the same transaction
was the contradiction; it is resolved by **moving scope, not by weakening the invariant**.

**Decision.** S6 includes the minimum Production Core needed to uphold the Quote-approval
transactional invariant. S9 remains the full **operational** Production sprint. Concretely:

| In S6 (Production Core) | Stays in S9 (operational expansion) |
|---|---|
| `ProductionOrder` aggregate — identity, `QuoteRevisionId` (**unique**), `Status`, `HasPendingRevision`, `SupersededByOrderId`, `CancellationReason` | `ProductionOrderItem`, planned/actual material, item status |
| The full `ProductionOrderStatus` enum (all six states), so the §1.5 guard and the §3.0/A.2 matrix type-check and are enforceable from day one | Printer assignment, scheduling, print-queue execution, shop-floor document, shipping label |
| Creation in `QUEUED` from `QuoteApproved`, `ON CONFLICT (quote_revision_id) DO NOTHING` (STATE-MACHINES §4.3) | `QUEUED → IN_PRODUCTION → READY → SHIPPED → DELIVERED` and their UX |
| `QUEUED → CANCELED` with `CancellationReason = SUPERSEDED_BY_REVISION` when A.2's matrix requires it | `IN_PRODUCTION/READY/SHIPPED → CANCELED` (operator-initiated write-off) |
| The three synchronous handlers in the table above (`QuoteRevised`, `QuoteCanceled`, `QuoteExpired` maintaining `HasPendingRevision`) | Production-side operator UX around those flags |

This is a **dependency slice**, not a roadmap collapse: **S6 does not become "the Production
sprint."** It ships exactly enough persistence for `QuoteApproved` to be transactionally correct,
and provides no operational production workflow — no queue screen, no shop-floor document, no
item-level tracking. A user approving a quote in S6 gets a real, idempotent `ProductionOrder` row
sitting in `QUEUED`; nothing yet moves it forward except a future S9 action. `ProductionOrder`
persistence therefore **first appears in S6**, not S9 — any earlier text implying otherwise
(ROADMAP's S9 section, before this correction) is stale and is fixed alongside this ADR.

No second creation path is introduced: `ProductionOrder` creation stays synchronous and
in-transaction (A.6); it is never moved to the outbox, in S6 or in S9's later extension of the
same aggregate. Idempotency (§4.3's three layers) is unchanged and is now exercisable in S6 itself
rather than only in an S9 that does not yet exist when S6 needs it.

---

## Part B — H-009 A: commercial conversion semantics

### B.1 The commercial fact is `APPROVED`; approval does not create a `Sale`

`APPROVED` already means "customer accepted this revision" (STATE-MACHINES §1.1). **No new state
is introduced** — no `AcceptedByCustomer`, no `Converted`. Inventing a second state for a meaning
the model already carries would create two sources of truth for one fact.

Approval **does not create a `Sale`**. ROADMAP S8 specifies sale *"creation from an approved
revision"* as an S8 action, and DOMAIN-MODEL §15 raises `SaleCreated` from **Sales**, not from
Quoting. This asymmetry with production is deliberate and is confirmed here:

| | Production | Sale |
|---|---|---|
| Trigger | automatic, synchronous, on approval | explicit operator action (S8) |
| Why | the shop floor must know immediately; the order is part of the approval invariant | recognising revenue is a separate financial act, and a deal can be approved long before it is billed |

**S6 emits no commercial-conversion event.** Adding `CommercialConversionRequested` would imply
automatic sale creation and contradict both sources above — rejected (and it would be the
duplicate-event mistake ADR-0012 warns about).

**S6's obligation to S8** is therefore a *state*, not a message:

> A `QuoteRevision` is **sale-eligible** iff `Status = APPROVED`. Because the revision is
> immutable, S8 can copy its frozen lines at any later time and reference it via
> `Sale.QuoteRevisionId` without re-running pricing (DOMAIN-MODEL §9).

Whether more than one non-canceled `Sale` may reference one approved revision (partial or split
sales) is **H-002's** decision, due S8. It is **deferred, not forbidden and not supported** here;
S6 does nothing that would prevent either answer. The recommended default, offered to H-002
without binding it, is one non-canceled `Sale` per approved revision.

### B.2 Quote-level commercial outcome — derived and non-retroactive (new)

This is the core of H-009 A. The conversion metric in
[DATA-DICTIONARY §4.1](../DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão) is defined on
**the current revision's status**, and that breaks precisely in the two situations the debt names:

- A quote approved in January, then revised (the new revision is `GENERATED`), has a current
  revision that is neither approved nor decided. The won deal **silently leaves** January's
  numerator *and* denominator, retroactively lowering a closed month's conversion rate.
- A quote expired in January and revived by a new revision leaves January's denominator,
  retroactively *raising* that month's rate.

Both rewrite history, which CLAUDE.md rule 5 and ADR-0003 forbid everywhere else in this system.

**Decision.** The commercial outcome belongs to the **quote** and is **derived** from the
append-only `quote_status_history` (never stored — the same discipline ADR-0006 applies to
variance). Two distinct claims were previously compressed into one word, "monotonic"; corrected
by naming them separately (MEDIUM-01):

```
firstApprovalAt(quote) = MIN(changed_at) over status-history rows of this quote's revisions
                         WHERE to_status = 'APPROVED'          -- null if never approved

hasEverWon(quote) = firstApprovalAt is not null

commercialOutcome(quote) =     -- the CURRENT reporting classification, read at any instant
    WON    if hasEverWon(quote)
    LOST   if NOT hasEverWon(quote) AND the current revision is CANCELED or EXPIRED
    OPEN   otherwise
```

- **`hasEverWon` is the part that is monotonic (absorbing).** Once `firstApprovalAt` is set it
  never becomes null again and never moves: a later revision — approved, canceled or expired —
  cannot un-win a quote, and a second approval of a superseding revision is the **same deal at a
  new scope**, never a second win. This matches the production rule exactly: §3 Case A step 4
  leaves the original approval standing when the superseding revision falls through. Consequently
  `commercialOutcome = WON` is itself absorbing too: once true, it stays `WON` forever.
- **The current classification of a *never-won* quote is NOT monotonic.** Before a first approval,
  `commercialOutcome` can legitimately move `OPEN → LOST → OPEN`: a revision expires or is
  canceled (`OPEN → LOST`, dated at that transition), and the quote is later revived by a new
  revision (`LOST → OPEN` again, since the new revision is `GENERATED`, not terminal). This is
  expected and is not a bug — it mirrors §1.6's revival rule exactly, and it is precisely why the
  **historical** metric below, not the *current* classification, is what a report must read.
- **Losses count only while the quote has never been won.** A terminal `CANCELED`/`EXPIRED`
  transition of the then-current revision, occurring before `firstApprovalAt` exists, is an
  eligible loss. Once a quote is won, subsequent terminal outcomes are scope changes, not losses.

**Same-period cardinality — the reporting unit is one quote-period decision (Option B, frozen
2026-09-20).** An earlier draft of this section counted each eligible terminal transition as its
own loss "event", which is wrong at the KPI level in two concrete ways: a quote that expired on
3 January, was revived, and was canceled again on 20 January would put **two** entries in
January's denominator; and a quote that expired on 3 January and was **approved** on 20 January
would appear in January's `lost` *and* `won` at once. The frozen rule:

```
for one QuoteId and one reporting period, the quote contributes AT MOST ONE outcome:

periodOutcome(quote, period) =
    WON    if firstApprovalAt(quote) falls inside the period
    LOST   else if ≥ 1 eligible pre-win CANCELED/EXPIRED transition falls inside the period
    —      otherwise

precedence: WON > LOST > no contribution
won(period)  = COUNT(DISTINCT QuoteId) with periodOutcome = WON
lost(period) = COUNT(DISTINCT QuoteId) with periodOutcome = LOST
```

So: expire-then-cancel inside one January is **one** January loss, not two. Expire-then-approve
inside one January is **one** January win and **zero** January losses. A quote still enters
`won()` at most once **ever** (keyed on `firstApprovalAt`), so approving a superseding revision in
a later period adds nothing to that later period. Across periods the quote may legitimately be
counted more than once — a January loss and a March win — but never more than once **within** a
period. The full worked table is in
[DATA-DICTIONARY §4.1](../DATA-DICTIONARY.md#41-conversion-rate-taxa-de-conversão), which is the
metric's normative home.

**This is a reporting rule only.** `quote_status_history` remains append-only and complete: every
transition, including the second January cancellation the KPI does not double-count, stays
recorded and auditable. Nothing is deleted, collapsed or back-dated, and **no** period-outcome
table, event or aggregate is introduced — the grouping is evaluated on read.

**Non-retroactivity — not monotonicity — is the property being bought**, and it holds regardless
of which of the claims above applies. Once a period closes, no later action changes its reported
conversion. A quote lost in January and won in March is counted once in January as a loss and once
in March as a win — both statements were true when made, neither is edited afterwards, and the
current, real-time `commercialOutcome` label for that quote is `WON` from March onward even though
its January contribution to that closed period's metric never changes.

A quote may therefore be simultaneously **won** and have a non-terminal current revision (an
active re-negotiation). It legitimately appears both in the historical won count and in
*"orçamentos em aberto"*; the S7 UI must label that case rather than hide it.

### B.3 Reversal of a won deal is a Sales-side fact

There is no "un-approve" transition and none is added. A deal that collapses after approval is
recorded where the money is: `Sale` is canceled (`CONFIRMED → CANCELED`, STATE-MACHINES §5.3) and
excluded from every revenue and profit metric while remaining visible. Conversion is read from
`Quote`; revenue is read from `Sale` (DOMAIN-MODEL §9) — the two answer different questions and
are deliberately allowed to disagree.

---

## Part C — H-004 remainder: `PER_ORDER` fee allocation across lines

### C.1 The fee is charged exactly once, and it is embedded in prices

`PER_ORDER` already means *"charged once per order and allocated across units"*
([DATA-DICTIONARY §2](../DATA-DICTIONARY.md#2-core-enumerations), `FixedFeeApplication`). Frozen
for the multi-line case:

> A `PER_ORDER` fixed fee is applied **exactly once per quote revision** — never once per line,
> never once per unit.

The fee is **our** marketplace cost, recovered through the prices we quote, exactly as commission
already is via the denominator (CR-07.2). It is **not** a separate customer-facing charge: no fee
line appears on the proposal, and CR-08.6's totals remain pure sums of line values.

> **What is exact, precisely (LOW-01 correction).** The raw `PER_ORDER` fee enters the revision's
> pricing basis **exactly once** and is *partitioned* across the affected lines' analytical
> allocations such that `Σ allocatedOrderFee_i = rawOrderFee` (C.4's invariant, verified exactly,
> to the cent). That partition is the mathematically exact statement. The **final customer-facing
> total** is a separate, downstream number: it also reflects the price-rounding policy (CR-07.4),
> any manual price override (CR-08.1) and any discount (CR-08.2) — none of which are partition
> arithmetic, and none of which are claimed to be "exact" here. Saying "the customer pays the fee
> exactly once" conflated the exact analytical invariant with those already-frozen, independent
> rounding and override rules; the invariant that actually holds unconditionally is the sum above.

**This corrects a latent double-count.** CR-08.3 currently adds `round2(fixedFee)` — the *whole*
order fee — to **every** line's `lineFeeAmount`, and CR-07.3 divides the *whole* fee by each
line's own quantity. Both are correct for a single-line quote (which is all S5 could produce) and
both over-charge the fee once per additional line. Corrected in CR-07.3, CR-07.7 and CR-08.3.

### C.2 Allocation scope

Allocation runs over the set of lines in the revision that resolve to the **same applicable
`FeeRuleVersion`** — the "fee group". In S6 this is expected to be exactly one group covering all
lines, because mixed-channel revisions are **H-003's** decision (due S8) and are not authorised
yet. If S8 authorises item-level channel overrides, the same algorithm applies per group with no
change to it. A `FeeRuleVersion` carries exactly one fixed fee
([ADR-0005](ADR-0005-marketplace-fee-rules.md)), so there is exactly one order-level fee per
group; no fee-composition framework is introduced.

### C.3 Allocation basis: line estimated cost

```
basis_i      = round6( unitTotalCost_i × quantity_i )      -- InternalScale, pre-price, pre-fee
totalBasis   = Σ basis_i     (over the fee group)
exactShare_i = orderFee × basis_i / totalBasis             -- full decimal precision, unrounded
```

**Why line cost.** With cost-proportional allocation, and writing `Φ` for the order fee and
`c`, `m` for commission and margin:

```
alloc_i  = Φ · C_i / ΣC
price_i  = (C_i + alloc_i) / (1 − c − m)  =  C_i · (1 + Φ/ΣC) / (1 − c − m)
```

**Precisely when this holds (LOW-01 correction).** The derivation above divides by one shared
`(1 − c − m)` for the whole group, so `price_i = C_i × (1 + Φ/ΣC) / (1 − c − m)` is exact **only
where `c` (commission) and `m` (desired margin) are the same across the lines being compared**.
`FeeRuleVersion` fixes one `c` per fee group by construction, so commission is always uniform
within a group; `DesiredMarginPercent` is set **per line** (DOMAIN-MODEL §8), so it is not always
uniform. The precise claim is therefore: **cost-proportional allocation raises every line's
*allocation-driven pricing basis* — `C_i × (1 + Φ/ΣC)` — by the same percentage `Φ/ΣC`**,
independently of that line's own margin, rounding policy or manual override. It does **not** by
itself claim that two lines end up with the same percentage change in their *final* price when
their margins, price-rounding outcomes, discounts or overrides differ — those are separate,
already-frozen effects applied after this basis is computed. Cost is also known *before* pricing
(it is S4 `CostEngine` output), so the allocation is **non-circular** — unlike a price-based
basis, where the fee would depend on the price that depends on the fee, the circularity CR-07.5
had to solve for brackets.

Rejected alternatives:

- **Quantity proportion** — a constant *absolute* uplift per unit. A quote of 100 stickers plus
  one machined part makes the stickers absorb almost the entire fee, inflating a R$ 1,00 item by
  a double-digit percentage. Rejected as commercially distorting.
- **Equal per line** — a R$ 5.000 line and a R$ 10 line absorb the same fee, moving the cheap
  line's price by ~25% and the expensive one by 0,04%. Rejected for the same reason; retained
  only as the zero-basis fallback (C.5), where no better signal exists.
- **Pre-fee commercial subtotal proportion** — needs a two-pass price computation (price without
  the fee, allocate, re-price), reintroduces circularity as soon as lines carry different margins
  or manual overrides, and in the uniform case merely reproduces cost proportion with more
  machinery. Rejected as strictly more complex for the same answer.

Note that effective margin equals desired margin under *any* allocation (each line's fee share
enters both its price numerator and its fee deduction), so CR-08.5 holds regardless; the basis is
chosen purely for price sanity and explainability.

### C.4 Algorithm — floor plus largest remainder

Allocation is a **partition** of an exact 2-decimal amount, so the parts must sum to the whole.
Rounding each share independently cannot guarantee that (three shares of `3,333…` each rounded
half-up give `9,99 ≠ 10,00`). The deterministic algorithm:

```
1. exactShare_i = orderFee × basis_i / totalBasis          (full precision decimal)
2. alloc_i      = floor2(exactShare_i)                     (truncate toward zero, 2 decimals)
3. residual     = round( (orderFee − Σ alloc_i) × 100 )    (an integer, 0 ≤ residual < lineCount)
4. rank lines by remainder_i = exactShare_i − alloc_i, DESCENDING;
   tie-break by quote_item.line_number ASCENDING
5. add R$ 0,01 to each of the first `residual` lines in that ranking
```

Step 4's tie-break is `line_number`, which is unique per revision
(`U (quote_revision_id, line_number)`) and stable for the life of the revision. **Database row
order is never used**, and `sort_order` — a presentation concern the operator can change — is
never used either.

**Invariant (must be a test): `Σ alloc_i = orderFee` exactly, for every quote, always.**

### C.5 Degenerate inputs

| Input | Behaviour |
|---|---|
| `orderFee = 0` | every `alloc_i = 0`; the algorithm runs and terminates with `residual = 0` |
| `orderFee < 0` | **cannot occur** — `FeeRuleVersion` already validates `fixedFee ≥ 0` (ADR-0005, and `FEE_RULE_VERSION_FIXED_FEE_INVALID` in S5). No negative-fee semantics are introduced |
| `totalBasis = 0` (every line has zero estimated cost) | **fallback: equal split per line**, using the identical floor + largest-remainder algorithm with `basis_i = 1` for every line. No division by zero, no rejection |
| a single line has `basis_i = 0` while `totalBasis > 0` | that line receives `alloc_i = 0`; nothing special is needed |
| the fee group has no lines | nothing to allocate; approval and sending are already refused for an empty quote (`QUOTE_HAS_NO_ITEMS`) |

The zero-basis fallback is equal-per-line rather than per-unit because with no cost signal there
is no defensible reason to prefer one line, and a quantity split would import exactly the
distortion C.3 rejected.

### C.6 `PER_ORDER` dependency closure and full recomputation

*(This is the fee-specific instance of the general revision-construction rule in
[§A.7](#a7-revision-construction-is-clone-then-recalculate-never-an-in-place-edit-blocking-02);
read that section first for the "candidate", "pricing-affected line" and verbatim-copy vocabulary
used below.)*

While constructing a revision candidate (§A.7), any change that alters a `PER_ORDER` fee group's
allocation basis or membership — a line's quantity, a line's estimated cost, a line added to the
group, a line removed from the group, or the group's fee itself changing — makes **every line in
that fee group** pricing-affected, whether or not the operator touched that specific line, because
`totalBasis` and therefore every `exactShare_i` in C.3's formula changed. The candidate's complete
allocation for that group is recomputed **from the current line set**, never patched incrementally:
patching would accumulate residual-cent drift across successive candidates and eventually break
the `Σ alloc_i = orderFee` invariant (C.4). A fee group **not** touched by the change is not
pricing-affected and its lines' fee-related fields are copied verbatim from `R(n)` — §A.7's "lines
outside the group" rule.

Concretely, for each pricing-affected line the candidate recomputes `allocatedOrderFee`,
`fixedFeePerUnit` (CR-07.3), `SuggestedUnitPrice` (CR-07.2) and `lineFeeAmount`/`LineTotalAmount`
where they depend on those (CR-08.3); it does **not** touch `UnitPrice` when
`PriceOverridden = true`, `DiscountKind`/`DiscountValue`, `ProductId`, or any other source field
the operator did not change on that line — see §A.7 for why an override survives a reallocation.

Once persisted, `R(n+1)`'s allocation is exactly as immutable as the rest of its snapshot: once a
revision reaches `APPROVED` (or any other terminal status) it is frozen, its lines and quantities
can no longer change, and its allocations are frozen history. A further commercial change
constructs `R(n+2)` from scratch (§A.7), which recomputes its own allocation independently — this
is the H-001 immutability rule, applied to fees.

### C.7 Rounding: partitioning is not rounding

[ADR-0002 §4](ADR-0002-money-precision-and-rounding.md) mandates `MidpointRounding.AwayFromZero`
"everywhere, no exceptions" — that rule governs **rounding a computed value to its presentation
scale**. Allocation is a different operation: it *partitions* an already-rounded 2-decimal amount
into 2-decimal parts under a sum constraint. The floor-plus-largest-remainder method in C.4 is
the standard exact-sum partition and is **not an exception to ADR-0002**, because no value is
being rounded — each part is *selected* so the parts total the original. Every ordinary rounding
inside the pricing of an allocated line (the price, the commission, the line total) continues to
use half-away-from-zero at the scales CR-00.3 lists.

### C.8 Canonical allocation vectors

Exact BRL values for S6's test corpus. In every case `Σ alloc = orderFee`.

```
A — Single line
    orderFee 5,00 · L1 cost 20,00 × qty 2 → basis 40,00 · totalBasis 40,00
    alloc: L1 = 5,00                                              Σ = 5,00
    fixedFeePerUnit(L1) = round6(5,00 / 2) = 2,500000
    (identical to the pre-existing single-line rule — S5 behaviour is unchanged)

B — Two equal-basis lines
    orderFee 5,00 · L1 10,00 · L2 10,00 · totalBasis 20,00
    exact: 2,50 / 2,50 · floor2: 2,50 / 2,50 · residual 0
    alloc: 2,50 · 2,50                                            Σ = 5,00

C — Three equal-basis lines (residual cent)
    orderFee 10,00 · L1 10,00 · L2 10,00 · L3 10,00 · totalBasis 30,00
    exact: 3,333333… each · floor2: 3,33 each → Σ 9,99 · residual 1 cent
    remainders tie (0,003333… each) → line_number ascending → L1
    alloc: 3,34 · 3,33 · 3,33                                     Σ = 10,00
    (half-up rounding of each share would give 9,99 — this is why floor + remainder is required)

D — Unequal basis
    orderFee 10,00 · L1 100,00 · L2 50,00 · L3 25,00 · totalBasis 175,00
    exact: 5,714285… / 2,857142… / 1,428571…
    floor2: 5,71 / 2,85 / 1,42 → Σ 9,98 · residual 2 cents
    remainders: L3 0,008571 > L2 0,007142 > L1 0,004285 → L3, then L2
    alloc: 5,71 · 2,86 · 1,43                                     Σ = 10,00

E — Quantity change (full recomputation of D)
    L1 quantity 1 → 2, so L1 basis 100,00 → 200,00 · totalBasis 275,00
    exact: 7,272727… / 1,818181… / 0,909090…
    floor2: 7,27 / 1,81 / 0,90 → Σ 9,98 · residual 2 cents
    remainders: L3 0,009090 > L2 0,008181 > L1 0,002727 → L3, then L2
    alloc: 7,27 · 1,82 · 0,91                                     Σ = 10,00
    L1's share moved 5,71 → 7,27: recomputed from scratch, not patched
    fixedFeePerUnit(L1) = round6(7,27 / 2) = 3,635000

F — Line removal (remove L3 from D)
    remaining L1 100,00 · L2 50,00 · totalBasis 150,00
    exact: 6,666666… / 3,333333… · floor2: 6,66 / 3,33 → Σ 9,99 · residual 1 cent
    remainders: L1 0,006666 > L2 0,003333 → L1
    alloc: 6,67 · 3,33                                            Σ = 10,00
    L3's 1,43 is not redistributed as a patch — the whole group is recomputed

G — Zero allocation basis (all lines zero estimated cost)
    orderFee 5,00 · L1 basis 0,00 · L2 basis 0,00 · totalBasis 0,00
    fallback: equal split per line (basis_i = 1 each)
    alloc: 2,50 · 2,50                                            Σ = 5,00
    with three zero-basis lines and orderFee 10,00 → 3,34 · 3,33 · 3,33  Σ = 10,00
```

---

## The S6 aggregate, in one place

Recommended shape — semantics only, no classes and no columns:

| Concept | Role |
|---|---|
| **`Quote`** | The **aggregate root** and the transactional/concurrency boundary. Owns identity (`Number`, `YYMMDD-N`, allocated once and shared by every revision — ADR-0004), the `CurrentRevisionId` pointer, and **no money and no status**. |
| **`QuoteRevision`** | Owns **status** (the only place it exists), the revision index/suffix, validity, and the **immutable commercial snapshot**. Approval, supersession and expiry all act here. |
| **`QuoteItem`** (+ `QuoteItemCostSnapshot`) | Owns the line-level immutable snapshot, including this line's **allocated order-fee share**. |
| `QuoteStatusHistory` | Append-only business history; the authoritative source for B.2's derived outcome. |

- **Numbering.** The `Quote` root owns the number; every revision of one quote shares it and
  appends its own suffix (`260906-4` → `260906-4B`). Confirmed unchanged (ADR-0004).
- **Revision numbering.** `RevisionIndex` 1 is semantically "A" and renders **no** suffix; index 2
  renders `B`; bijective base-26 thereafter. Both `revision_suffix` and `display_number` are
  **persisted**, never recomputed on read. Confirmed unchanged (ADR-0004 §2).
- **Concurrency.** Writes reach revisions and items **through the root** (CLAUDE.md rules 21/28),
  so `Quote.Version` is bumped by any child change and two concurrent commands on one quote
  serialise: the loser gets `CONCURRENCY_CONFLICT`. Creating the next revision and approving the
  current one therefore cannot interleave. Two further layers already exist for approval
  specifically: the revision's own status guard (`QUOTE_REVISION_ALREADY_DECIDED`) and
  `UNIQUE (quote_revision_id)` on the production order (STATE-MACHINES §4.3).
- **Expiration.** The **current revision** expires, not the quote — the quote has no status to
  expire. Superseded and terminal revisions are excluded by the job's own query
  (STATE-MACHINES §1.6). A quote whose current revision expires may be revived only by creating a
  new revision; per B.2 that revival does not erase the loss already recorded.

### Snapshot contract

What a revision must freeze so that later commercial and production flows never depend on
mutable current state (semantics, not columns; most of this already exists in DOMAIN-MODEL §8 and
DATA-MODEL §9):

| Group | Frozen at issue |
|---|---|
| Customer | identity + name, document, contacts and addresses as they were |
| Line / product | product identity, recipe identity, name snapshot, description, **quantity** |
| Costing | every cost component, wastage, unit total cost, the cost engine version |
| Pricing | desired margin, suggested price, final unit price (+ override flag), discount, line total, the rounding policy applied |
| Fee identity | `SalesChannelId`, `FeeRuleVersionId`, `FixedFeeApplication` |
| Fee values | commission percent, the **raw** order fee, any clamp applied |
| **Fee allocation** | **this line's allocated share of the order fee** — the value CR-07.3 already requires ("the item snapshot records both the raw fee and the allocation") and the input to `lineFeeAmount` |
| Rounding | the price rounding policy in force at issue |
| Approval metadata | approved-at, approved-by, and the append-only status history that dates every transition |

Nothing here is recomputed later: a `Sale` copies these frozen values, and a `ProductionOrder`
copies the exploded BOM (ADR-0006 §3).

---

## Scope boundaries

| Sprint | Owns |
|---|---|
| **S6** | `Quote`/`QuoteRevision`/`QuoteItem`/snapshots/history, the state machine and guards in Part A, the derived outcome in Part B.2, the allocation in Part C, `ExpireQuotesJob`, `QuoteApproved` and the three handler additions in A.6, **and the minimum Production Core in A.8** (`ProductionOrder` aggregate, full status enum, `QUEUED` creation, `QUEUED→CANCELED` on supersession, `HasPendingRevision`) |
| **S7** | Quote UX and PDF; labelling a won quote that is being re-negotiated (B.2) |
| **S8** | `Sale` creation from a sale-eligible revision; sale cardinality (H-002); mixed-channel fees (H-003); brackets and fee clamps (H-005) |
| **S9** | Operational Production: `ProductionOrderItem`, planned/actual material, printer assignment and scheduling, the `QUEUED→IN_PRODUCTION→READY→SHIPPED→DELIVERED` UX and the rest of the A.2 matrix's operator-facing effects |

**Deliberately not designed here** (recorded so a future sprint does not assume an omission):
partial shipment, split production, partial invoicing or payment, credit notes, quote merge or
split, multiple approvals of one revision, multi-currency, a tax engine, cross-order fee pooling,
and differential "change orders" between revisions (already deferred by STATE-MACHINES §3).

## Alternatives considered

**H-001**

- **Block approval whenever any previous order is beyond `QUEUED`** (the literal §1.5 wording) —
  rejected: it would include `DELIVERED`, permanently blocking repeat business on a quote whose
  earlier scope was successfully fulfilled, and it contradicts §3 step 3.
- **Auto-cancel an in-progress order when a new revision is approved** — rejected for the reason
  §3 already gives: it destroys real material without a human decision.
- **Make `has_pending_revision` block queue actions** — rejected: the pending revision may never
  be approved, so this idles a printer on a maybe. The block belongs on approval, where a person
  is already deciding.
- **Transfer a canceled order's actual consumption to the superseding order** — rejected: it
  would silently move a write-off into a different order's variance and make both orders' actual
  cost untrue.
- **A new `ProductionConversionRequested` event** — rejected as a duplicate of `QuoteApproved`.
- **Gate revision *creation* on production-order state** (an earlier STATE-MACHINES §2 draft) —
  rejected as `BLOCKING-01`: it directly contradicted the A.2 matrix and would have blocked
  ordinary negotiation while a previous scope was still on the shop floor.
- **Leave `ProductionOrder` entirely in S9** — rejected as `BLOCKING-03`: ADR-0012's synchronous,
  same-transaction invariant for `QuoteApproved` cannot be upheld against an aggregate that does
  not exist yet. Resolved by moving the minimum persistence into S6 (§A.8), not by weakening the
  invariant or by deferring the guard to a "soft" check.
- **Weaken the invariant to an eventual/outbox-based production conversion for S6–S8** — rejected:
  it would let an `APPROVED` revision exist with no order for an arbitrary window, which is
  exactly the failure ADR-0012 §2 was written to prevent.

**H-009 A**

- **Count each quote exactly once ever, at its first terminal decision, with the outcome being
  "approved if ever approved"** — the closest competitor. Rejected because it is *retroactive*: a
  quote lost in January and later won would silently raise January's already-published conversion
  rate. Non-retroactivity is the property the whole system is built on (CLAUDE.md rule 5,
  ADR-0003), and it is worth the cost that such a quote appears in two periods.
- **Keep the current-revision-status definition** — rejected: it is the defect H-009 A names.
- **Add an explicit `Converted` / `AcceptedByCustomer` state** — rejected: `APPROVED` already
  carries that meaning, and a second state would create two sources of one truth.
- **Have approval create the `Sale` automatically** — rejected: it contradicts ROADMAP S8 and
  DOMAIN-MODEL §9, and it would recognise revenue at a moment the operator has not chosen.
- **Call the whole derived outcome "monotonic"** — rejected as `MEDIUM-01`: only `hasEverWon` is
  absorbing; a never-won quote's current classification legitimately cycles
  `OPEN → LOST → OPEN` as it expires and is revived. The word overstated what actually holds and
  is replaced by naming the two claims separately.
- **Describe the metric's unit as strictly "the quote", meaning one lifetime slot** — rejected as
  `MEDIUM-02`: a single quote legitimately contributes a loss in an earlier period and its one win
  in a later one, so it is not a lifetime partition. The unit is one **quote-period decision**.
- **Count every eligible terminal transition as its own loss (`Option A`)** — the alternative put
  to the product owner for the same-period case, and **rejected by them** in favour of `Option B`
  (deduplication by `QuoteId` + period, `WON` first). Option A is arithmetically defensible —
  it measures "how many times did we lose" — but it lets one quote inflate a single period's
  denominator by expiring and being re-quoted inside that month, and it lets the same quote appear
  in that month's `won` and `lost` simultaneously, which no reader of a conversion rate expects.
  Option B costs the ability to see "how many times a deal collapsed before closing" in the
  headline KPI; that signal remains fully available in the append-only history for any future
  operational report that wants it.

**H-004 remainder**

- **Quantity-proportional, equal-per-line and pre-fee-subtotal bases** — rejected in C.3, with
  the price-distortion and circularity reasoning.
- **Round each share half-up independently** — rejected: it cannot satisfy the sum constraint
  (vector C is the counter-example: three shares of `3,333…` give `9,99 ≠ 10,00`).
- **Show the order fee as a separate customer-facing line** — rejected: it is our marketplace
  cost, not a charge to the customer, and it would break CR-08.6's "totals are sums of lines".
- **Patch allocations incrementally on each edit** — rejected: accumulates residual-cent drift.
- **A general multi-component fee engine** — rejected as over-design: a `FeeRuleVersion` carries
  exactly one fixed fee.
- **Claim allocation makes every line's *final price* move by the same percentage** — rejected as
  `LOW-01`: true only of the shared pricing basis `C_i × (1 + Φ/ΣC)`, not of the final price once
  per-line margin, rounding and overrides are applied. Restated precisely in §C.3.
- **Say "the customer pays the fee exactly once"** — rejected as `LOW-01`: the exact, provable
  statement is the partition invariant `Σ allocatedOrderFee_i = rawOrderFee`; the customer-facing
  total is a further, separately-governed computation (rounding, override, discount). Restated in
  §C.1.

## Consequences

- S6 can be implemented without inventing semantics: every state transition, guard, idempotency
  key, allocation value and derived metric above is deterministic and testable.
- Three latent documentation contradictions from the first draft are removed (A.3 the approval
  guard set, A.6 the `QuoteApproved`/`GenerateQuotePdfRequested` naming and the unreachable
  `QuoteCanceled` effect, C.1 the per-line fee double-count), and three more found at independent
  review are removed in this correction pass (A.7/STATE-MACHINES §2 the revision-creation guard,
  A.8 the S6/S9 production-persistence contradiction, B.2 the overstated "monotonic" outcome).
  Each would otherwise have become a defect in code or a false claim in a report.
- Conversion reporting becomes **non-retroactive**: closed periods stop moving. The cost is that
  a quote lost and later won is counted once in each of two periods — a loss, then later a win.
  That is the honest reading of what happened, and it is the deliberate trade in B.2. Within any
  single period a quote is counted exactly once (Option B), so the denominator can never be
  inflated by one deal collapsing and being re-quoted inside the same month.
- `PER_ORDER` allocation adds one persisted value per line (the allocated share). CR-07.3 already
  required it in words; S6's implementation adds the column additively to
  `quoting.quote_item_cost_snapshot`. No existing schema changes.
- The single-line `PER_ORDER` behaviour S5 already ships is **unchanged** (vector A), so nothing
  closed in S5 regresses.
- `has_pending_revision` never blocks the shop floor, so an operator can start work that a later
  approval then blocks (§3 Case B). That is intended: the block lands on the commercial side,
  where a human is already making a decision, not on a printer that is already running.
- **S6 now persists a minimum `ProductionOrder`** (§A.8). This is a scope addition relative to the
  first draft, not a new sprint: S6 still ships no operational production workflow, and S9's own
  scope shrinks by exactly the slice that moved, so total delivered scope across S6+S9 is
  unchanged.
- **Revision construction is now named precisely** (§A.7): "editing a quote" is documentation
  shorthand for constructing a new revision candidate; no code or doc may model a persisted,
  in-place-mutable "draft revision".

## Compliance checks

- **H-001**: state-machine tests for every cell of A.2, including the `DELIVERED` and `CANCELED`
  rows that previously had no defined behaviour; a test that `HasPendingRevision` never refuses a
  production transition; a test that canceling an order neither reverses a stock movement nor
  re-points actual consumption; a test that **constructing** `R(n+1)` succeeds regardless of the
  previous order's state (A.7/STATE-MACHINES §2), while only **approving** it can be rejected with
  `PRODUCTION_ORDER_IN_PROGRESS`.
- **H-009 A**: tests that a quote approved then revised remains `WON` and keeps its original
  `firstApprovalAt`; that a second approval does not produce a second win **and adds nothing to
  the later period**; that a quote expired in one period and approved in a later one yields
  exactly one loss in the first and one win in the second; that no action changes a closed
  period's counts; that a never-won quote's *current* `commercialOutcome` is observed moving
  `OPEN → LOST → OPEN` across an expire-then-revive cycle, confirming that claim is deliberately
  **not** monotonic while `hasEverWon` is. Plus the same-period cardinality corpus: expire +
  cancel inside one period → `lost = 1`; expire + approve inside one period → `won = 1`,
  `lost = 0`; and an assertion that no `QuoteId` ever appears in both `won(period)` and
  `lost(period)`, nor twice in either, for any period.
- **H-004**: the A–G vectors above as named unit tests, plus a property test asserting
  `Σ alloc_i = orderFee` over randomised line sets, quantities and fees; a test that changing one
  line's quantity recalculates every sibling line's allocation in the same fee group (the
  dependency closure in A.7/C.6) while leaving a line in a *different* fee group byte-for-byte
  unchanged; a test that a manual price override on a pricing-affected line survives a
  reallocation untouched.
- **Cross-cutting**: CR-08.5 (effective margin equals desired margin) must still hold with a
  `PER_ORDER` fee present on a multi-line revision; a test that `ProductionOrder` creation still
  rolls back `QuoteApproved` when the invariant cannot be established, now exercised against the
  S6-owned aggregate rather than a stub.
