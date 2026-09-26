# STATE MACHINES — Verce 3D | Laboratório de Custos

Transitions listed here are the **complete** set. Any transition not listed is invalid and must
be rejected by the domain with a stable error code — never silently ignored, never allowed by
an "update status" endpoint that takes an arbitrary target.

Implementation rule: transitions are domain methods on the aggregate (`revision.Approve(user)`),
never a settable `Status` property. Every transition appends a history row.

---

## 1. Quote revision status

Status lives on **`QuoteRevision`**, not on `Quote`. A quote does not have a status; it has a
current revision that has one.

### 1.1 States

| State | Meaning | Terminal | Expires |
|---|---|---|---|
| `GENERATED` | Created, not yet sent to the customer | no | yes |
| `SENT` | Delivered to the customer | no | yes |
| `NEGOTIATING` | Customer responded, terms under discussion | no | yes |
| `APPROVED` | Customer accepted this revision | **yes** | **no** |
| `CANCELED` | Deal dropped (by us or the customer) | **yes** | **no** |
| `EXPIRED` | Validity elapsed without a decision | **yes** | — |
| `SUPERSEDED` | Replaced by a newer revision before being decided | **yes** | **no** |

`SUPERSEDED` is an **addition to the six states in the brief**. It is required to satisfy two
stated rules simultaneously — "every revision must be preserved" and "only the current revision
can be approved" — without corrupting the meaning of the other states. Justification and the
rejected alternatives are in
[ADR-0004](architecture/ADR-0004-quote-numbering-and-revisioning.md).

### 1.2 The supersession pointer

Every revision has `SupersededByRevisionId`. It is set on the previous revision whenever a new
revision is created, **regardless of status**. Two different things are therefore recorded:

- **Is this revision current?** → `SupersededByRevisionId IS NULL`.
- **How did this revision end?** → `Status`.

This distinction matters: when an **approved** revision is replaced, its status stays
`APPROVED` forever — the fact that the customer approved it on that date is history and must
not be erased. Only revisions that were still undecided (`GENERATED`, `SENT`, `NEGOTIATING`)
transition to `SUPERSEDED` when replaced. Terminal revisions keep their status and merely
receive the pointer.

### 1.3 Transition table

| From | To | Trigger | Guards |
|---|---|---|---|
| — | `GENERATED` | create quote / create revision | numbering allocated in the same transaction |
| `GENERATED` | `SENT` | user sends | is current revision |
| `GENERATED` | `NEGOTIATING` | user marks | is current revision |
| `GENERATED` | `APPROVED` | user approves | is current; `quote.allow_direct_approval = true`; §1.5 guards |
| `GENERATED` | `CANCELED` | user cancels | is current; reason required |
| `GENERATED` | `EXPIRED` | `ExpireQuotesJob` | is current; `ValidUntil < today(org tz)` |
| `GENERATED` | `SUPERSEDED` | new revision created | automatic |
| `SENT` | `NEGOTIATING` | user marks | is current |
| `SENT` | `APPROVED` | user approves | is current; §1.5 guards |
| `SENT` | `CANCELED` | user cancels | is current; reason required |
| `SENT` | `EXPIRED` | job | is current; `ValidUntil < today` |
| `SENT` | `SUPERSEDED` | new revision created | automatic |
| `NEGOTIATING` | `SENT` | user re-sends | is current |
| `NEGOTIATING` | `APPROVED` | user approves | is current; §1.5 guards |
| `NEGOTIATING` | `CANCELED` | user cancels | is current; reason required |
| `NEGOTIATING` | `EXPIRED` | job | is current; `ValidUntil < today` |
| `NEGOTIATING` | `SUPERSEDED` | new revision created | automatic |
| `APPROVED` | — | — | terminal; never expires, never superseded (pointer only) |
| `CANCELED` | — | — | terminal |
| `EXPIRED` | — | — | terminal |
| `SUPERSEDED` | — | — | terminal |

### 1.4 Diagram

```
                  ┌──────────────────────────────────────────────┐
                  │                                              │
          ┌───────▼────────┐   send    ┌──────────┐  respond  ┌──▼───────────┐
  create  │   GENERATED    ├──────────►│   SENT   ├──────────►│ NEGOTIATING  │
 ────────►│                │           │          │◄──────────┤              │
          └──┬──┬──┬───┬───┘           └──┬──┬──┬─┘  re-send  └──┬───┬───┬───┘
             │  │  │   │                  │  │  │                │   │   │
     approve │  │  │   │ new revision     │  │  │                │   │   │
             │  │  │   └──────────┐       │  │  └────────────────┼───┼───┼──┐
             │  │  │              │       │  │                   │   │   │  │
             │  │  │ cancel       │       │  │ cancel            │   │   │  │
             │  │  └──────────────┼───────┼──┼───────────────────┼───┘   │  │
             │  │                 │       │  │                   │       │  │
             │  │ expire (job)    │       │  │                   │       │  │
             │  └─────────────────┼───────┼──┴───────────────────┼───────┘  │
             │                    │       │                      │          │
             ▼                    ▼       ▼                      ▼          ▼
        ┌─────────┐        ┌────────────┐   ┌───────────┐   ┌─────────┐
        │APPROVED │        │ SUPERSEDED │   │ CANCELED  │   │ EXPIRED │
        │(terminal│        │ (terminal) │   │(terminal) │   │(terminal│
        │ no exp.)│        └────────────┘   └───────────┘   └─────────┘
        └────┬────┘
             │ domain event QuoteApproved
             ▼
       ProductionOrder (idempotent)
```

### 1.5 Approval guards

`Approve()` is rejected when any of the following holds:

| Guard | Error code |
|---|---|
| Revision is not the current one | `QUOTE_REVISION_NOT_CURRENT` |
| Revision status is terminal | `QUOTE_REVISION_ALREADY_DECIDED` |
| `ValidUntil < today(org tz)` | `QUOTE_REVISION_EXPIRED` |
| The quote has no items | `QUOTE_HAS_NO_ITEMS` |
| Any item has a non-positive final price | `QUOTE_ITEM_INVALID_PRICE` |
| A production order for a **previous** revision of this quote is in `IN_PRODUCTION`, `READY` or `SHIPPED` — i.e. **non-terminal and not `QUEUED`** ([ADR-0020 §A.3](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)) | `PRODUCTION_ORDER_IN_PROGRESS` (see §3) |

Approval, production-order creation and the status history row all happen in **one transaction**
— spanning several save/dispatch waves, since the `QuoteApproved` handler creates an aggregate
that a later wave persists ([ADR-0012 §2](architecture/ADR-0012-domain-events-and-outbox.md#3-the-unit-of-work-algorithm)).
If the production order cannot be created, **the approval rolls back**.

### 1.6 Expiration

`ExpireQuotesJob` runs hourly and selects, in one query:

```sql
SELECT r.id
FROM quoting.quote_revision r
JOIN quoting.quote q ON q.current_revision_id = r.id
WHERE r.superseded_by_revision_id IS NULL
  AND r.status IN ('GENERATED','SENT','NEGOTIATING')
  AND r.valid_until < :today_in_org_timezone
```

Properties:
- `APPROVED` and `CANCELED` are excluded by the status filter — they never expire.
- Superseded revisions are excluded — they already ended.
- `valid_until` is a `date` computed in the organization timezone at issue time and stored on
  the revision, so changing `quote.default_validity_days` never moves an existing deadline.
- The job is idempotent: re-running it changes nothing.
- Each expiration writes a history row with `Trigger = SYSTEM_JOB`.
- A quote expiring is a domain event (`QuoteExpired`), so a future notification is additive.

**Reviving an expired or canceled quote** is done by creating a new revision, which starts at
`GENERATED` with a fresh `ValidUntil`. There is no "un-expire" transition — that would mutate
history, which rule 5 of CLAUDE.md forbids.

---

## 2. Revision creation (edit semantics)

> **BLOCKING-01 correction ([ADR-0020 §A.1](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md)).**
> An earlier draft of step 2 below gated revision **creation** on the state of a previous
> production order. That directly contradicted §3.0's matrix, where *creating* `R(n+1)` is
> `allowed` in every single row. The guard never belonged here: **creating a revision is never
> blocked by the state of a previous revision's `ProductionOrder`.** Only **approving** the new
> revision can be blocked, and only by the §1.5 guard (`PRODUCTION_ORDER_IN_PROGRESS`, restated in
> §3.2 step 2). Removed accordingly.

Editing a quote **never** mutates a persisted revision — there is no mutable "draft revision" in
this domain. What the UI calls "editing a quote" is always **constructing a new revision**: clone
the current one into an in-memory candidate, apply the change to that candidate, recalculate only
what the change actually affects, then persist the candidate as `R(n+1)` in one atomic step. The
persisted `R(n)` is never touched except for the two fields §1.2 already allows
(`Status`, `SupersededByRevisionId`).

`EditQuote` is:

```
1. Load current, persisted revision R(n). It is immutable and stays untouched throughout.
2. Construct a revision candidate by deep-cloning R(n): items, snapshots, customer snapshot,
   notes. The candidate exists only in memory; it has no identity of its own until step 7 and is
   never itself persisted before the requested change is applied.
3. Apply the requested commercial changes to the candidate (quantity, product, manual cost,
   discount, line add/remove, customer/content fields, ...).
4. Determine the candidate's pricing-affected lines and recalculate ONLY their derived pricing
   fields (see ADR-0020 §A.7 for the exact dependency-closure rule — it is not simply "the lines
   the user touched", because a PER_ORDER fee group couples lines together). Every other field of
   every other line — and every field of a pricing-affected line that is not itself derived from
   a changed input — is copied verbatim from R(n).
5. Validate the complete candidate (CR-07.1's denominator guard and the other item/revision
   invariants).
6. Persist the validated candidate as R(n+1) — this is the only point at which it becomes a real,
   immutable revision:
   R(n+1).RevisionIndex = R(n).RevisionIndex + 1
   R(n+1).Status        = GENERATED
   R(n+1).SourceRevisionId = R(n).Id
   R(n+1).ValidUntil    = today(org tz) + validityDays
7. R(n).SupersededByRevisionId = R(n+1).Id
   if R(n).Status in (GENERATED, SENT, NEGOTIATING) → R(n).Status = SUPERSEDED
   (terminal statuses are preserved unchanged)
8. Quote.CurrentRevisionId = R(n+1).Id
9. Raise QuoteRevised.
```

There is **no production-order guard anywhere in this sequence** — see the correction box above.
`Quote.Version` (CLAUDE.md rules 21/28) is the only thing that can make step 1's load stale, and
that is ordinary optimistic concurrency (`CONCURRENCY_CONFLICT`), not a business rule.

Step 4 is deliberate and narrow: recalculating a line the operator did not touch, and that no
changed input feeds, would violate the snapshot rule from the operator's point of view ("I only
changed the quantity of item 2 and item 1 got more expensive"). A line's **own** cost is never
silently re-resolved against a current filament price or recipe just because a sibling line
changed — that remains the explicit, user-triggered *"Atualizar preços para os valores atuais"*
action. The one narrow, automatic exception is the `PER_ORDER` allocation dependency defined in
[ADR-0020 §A.7](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md): lines
that share a fee group are commercially coupled by construction, so a change to one line's basis
necessarily changes every other line's allocated share, whether the operator touched that other
line or not.

### Revision suffix

`RevisionIndex` 1 is the original and renders **no suffix**; index 2 renders `B`.
The suffix is bijective base-26 over `A..Z`:

```
index 1  → "A"  (never displayed)
index 2  → "B"
index 26 → "Z"
index 27 → "AA"
index 28 → "AB"
index 53 → "BA"
```

```csharp
public static string ToSuffix(int revisionIndex)   // 1 => "", 2 => "B", 27 => "AA"
{
    if (revisionIndex <= 1) return string.Empty;
    var n = revisionIndex;
    var sb = new StringBuilder();
    while (n > 0)
    {
        n--;                                    // bijective, not positional
        sb.Insert(0, (char)('A' + n % 26));
        n /= 26;
    }
    return sb.ToString();
}
```

Displayed number: `260906-4` (index 1), `260906-4B` (index 2), `260906-4C` (index 3).
`RevisionSuffix` is **persisted**, not computed on read, so a future change to the algorithm
cannot rewrite the identity of documents already issued.

---

## 3. Altering an APPROVED quote

This is the rule the brief asked to be defined explicitly. Behaviour depends on how far the
linked production order has gone.

> **H-001 closed by [ADR-0020](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md).**
> §3.0 below states the complete per-state matrix (the two grouped cases that follow left
> `CANCELED` undefined and disagreed with §1.5 about `DELIVERED`), §3.3 decides that
> `has_pending_revision` never blocks queue actions, and §3.4 decides what happens to actual
> consumption recorded against a canceled order.

### 3.0 The complete matrix

`R(n)` is the approved revision that owns the production order; `R(n+1)` is the newer revision.

| `R(n)` order state | Creating `R(n+1)` | Approving `R(n+1)` |
|---|---|---|
| *(no order)* | allowed | create order for `R(n+1)` |
| `QUEUED` | allowed; set `HasPendingRevision` | cancel `R(n)`'s order (`SUPERSEDED_BY_REVISION`, `SupersededByOrderId` = new order); create order for `R(n+1)` |
| `IN_PRODUCTION` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `READY` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `SHIPPED` | allowed; set `HasPendingRevision` | **rejected** — `PRODUCTION_ORDER_IN_PROGRESS` |
| `DELIVERED` | allowed; **no flag** | allowed; create order for `R(n+1)`; the delivered order is **left untouched** |
| `CANCELED` | allowed; **no flag** | allowed; create order for `R(n+1)`; the canceled order is **left untouched** |

Two rules govern every row: **creating** a revision is never blocked by the shop floor, and a
**terminal** order (`DELIVERED`, `CANCELED`) never blocks approval and is never mutated by it.
`SupersededByOrderId` is set **only** on an order actually canceled because it was superseded —
never on a delivered one, which was not replaced but fulfilled.

Because a new revision is a clone (§2), approving one after a `DELIVERED` order creates an order
for the new revision's **full** quantity; the system does not diff against what was already
delivered. Repeat business is usually better modelled as a **new quote**.

### 3.1 Case A — no production order, or the order is still `QUEUED`

**Automatic supersession.**

```
1. New revision R(n+1) is created (status GENERATED). R(n) keeps status APPROVED and
   receives the supersession pointer.
2. The existing ProductionOrder is flagged HasPendingRevision = true and shown as
   "aguardando nova aprovação" in the production queue. It is NOT canceled yet — the new
   revision may never be approved.
3. When R(n+1) is APPROVED:
     - the old order transitions to CANCELED with
       CancellationReason = SUPERSEDED_BY_REVISION and SupersededByOrderId = new order id;
     - a new ProductionOrder is created for R(n+1).
4. If R(n+1) is instead CANCELED or EXPIRED, the flag is cleared and the original order
   returns to the normal queue, still linked to the still-APPROVED R(n).
```

### 3.2 Case B — the production order is `IN_PRODUCTION`, `READY` or `SHIPPED`

**Blocking, not silent mutation.**

*(`DELIVERED` was listed here originally and is not: it is terminal, so by step 3 below approval
proceeds. Corrected in [ADR-0020 §A.3](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md);
see the matrix in §3.0.)*

```
1. Creating a new revision is ALLOWED — the commercial conversation must never be blocked by
   the shop floor. The new revision is created as GENERATED.
2. APPROVING that new revision is REJECTED with PRODUCTION_ORDER_IN_PROGRESS until the
   operator explicitly resolves the existing order:
       - cancel it (work in progress is written off, recorded with a reason), or
       - complete it (DELIVERED), accepting that it delivered the previous scope.
3. Once the old order reaches a terminal state, approval proceeds and creates a new order.
4. Every step is recorded: quote status history, production order status history, audit log.
```

Rationale: the alternatives are worse. Silently editing an order already being printed
misleads the shop floor; auto-canceling in-progress work destroys real material without a
decision; partially amending an order (a "change order" that diffs the two revisions and emits
a delta) is genuinely useful but is a multi-sprint feature with its own reconciliation rules.
It is recorded as a **deferred capability**, not as an omission.

### 3.3 `has_pending_revision` is advisory — it never blocks

The flag **blocks nothing**. It does not gate `QUEUED → IN_PRODUCTION` or any other production
transition; it drives the *"aguardando nova aprovação"* badge and a non-blocking warning when an
operator starts work while a revision is pending.

The flag means *a revision is pending approval*, and Case A step 2 is explicit that it may never
be approved. Idling a printer for a negotiation that may be abandoned costs real throughput; and
if the operator does start, Case B is exactly the safety valve — the **approval** is then blocked
until the order is resolved. This is the same warn-never-refuse posture stock already uses
([CR-13.11](CALCULATION-RULES.md#cr-1311--stock-is-advisory): "stock is advisory").

It is a **cached projection**, maintained in the same transaction as the revision transition that
changes it, and defined by:

```
order.HasPendingRevision ⇔ the order is non-terminal
                           AND its quote has a revision newer than the order's own source
                               revision whose status is non-terminal
                               (GENERATED | SENT | NEGOTIATING)
```

So it is set when a newer revision appears over a non-terminal order; cleared when that revision
reaches `CANCELED`, `EXPIRED` or `SUPERSEDED` and no other non-terminal newer revision remains
(Case A step 4 — the order returns to the queue with **no** state transition, having never been
canceled); consumed when the newer revision is `APPROVED` (§3.0 then applies); and never set on a
terminal order. `QuoteRevised`, `QuoteCanceled` and `QuoteExpired` each re-evaluate it
synchronously ([DOMAIN-MODEL §15](DOMAIN-MODEL.md#15-domain-events)).

### 3.4 Actual consumption recorded against a canceled order

Recorded actual consumption is an **immutable physical fact**. Canceling an order — whether by
the operator or by supersession — never reverses it, never deletes it and never re-points it:

- `production_order_item_actual_material` rows and the `InventoryMovement(Consumption)` they emitted stand
  permanently; the ledger is append-only ([ADR-0017](architecture/ADR-0017-inventory-ledger-and-unit-normalization.md))
  and `unit_cost_at_consumption` was frozen at consumption time
  ([ADR-0006 §2](architecture/ADR-0006-estimated-vs-actual-cost.md)). Material really left the
  shelf. This is §4.2's "consumed material stays consumed", extended to the supersession path.
- Consumption is **never transferred** to the superseding order, which starts at zero actual
  consumption with its own planned BOM, so its variance measures only its own work.
- A canceled order can therefore carry real actual cost with no delivered output. That cost stays
  attributable to that order and must never be absorbed into the superseding order or into a
  `Sale` referencing a different revision.

How that write-off is **classified** in the actual-vs-estimated report — the same question as a
`FAILED` item's wastage (§4.4) — is **H-006**'s scope (due before S10), which must honour the
invariant above.

---

## 4. Production order status

> **Ownership split ([ADR-0020 §A.8](architecture/ADR-0020-s6-quote-conversion-and-per-order-allocation.md), BLOCKING-03 correction).**
> The `QuoteApproved` guard in §1.5 and the idempotent creation in §4.3 are part of the Quote
> approval transaction, so the minimum `ProductionOrder` persistence and state values they depend
> on are **S6** scope, not S9. Concretely, S6 owns: the aggregate, the `QUEUED` creation and the
> `QUEUED → CANCELED (SUPERSEDED_BY_REVISION)` transition, the full status **enum** (so the §1.5
> guard can type-check against `IN_PRODUCTION`/`READY`/`SHIPPED`/`DELIVERED`), and
> `has_pending_revision`. **S9** owns everything that moves an order through the rest of this
> state machine operationally — `QUEUED → IN_PRODUCTION → READY → SHIPPED → DELIVERED`, the
> printer/scheduling UX, items, planned/actual material and the shop-floor document. Until S9
> ships, an S6 order can be created and (if superseded while `QUEUED`) canceled, but nothing moves
> it past `QUEUED` under its own power.

### 4.1 States

| State | Meaning | Terminal |
|---|---|---|
| `QUEUED` | Created, waiting for the printer | no |
| `IN_PRODUCTION` | Printing/finishing started | no |
| `READY` | Finished and packed, awaiting dispatch | no |
| `SHIPPED` | Handed to the carrier / customer notified | no |
| `DELIVERED` | Confirmed with the customer | **yes** |
| `CANCELED` | Abandoned or superseded | **yes** |

### 4.2 Transitions

| From | To | Trigger | Guards |
|---|---|---|---|
| — | `QUEUED` | `QuoteApproved` event | idempotent on `quote_revision_id` |
| `QUEUED` | `IN_PRODUCTION` | user starts | sets `StartedAt` |
| `QUEUED` | `CANCELED` | user cancels / supersession | reason required |
| `IN_PRODUCTION` | `READY` | user completes | sets `CompletedAt`; actual consumption should be recorded (warn if absent) |
| `IN_PRODUCTION` | `CANCELED` | user cancels | reason required; consumed material stays consumed |
| `READY` | `SHIPPED` | user ships | sets `ShippedAt` |
| `READY` | `IN_PRODUCTION` | user reopens | correction path; recorded |
| `READY` | `CANCELED` | user cancels | reason required |
| `SHIPPED` | `DELIVERED` | user confirms | sets `DeliveredAt` |
| `SHIPPED` | `CANCELED` | user cancels | reason required (return) |
| `DELIVERED` | — | — | terminal |
| `CANCELED` | — | — | terminal |

```
QuoteApproved
     │
     ▼
 ┌────────┐  start  ┌───────────────┐ complete ┌───────┐  ship  ┌─────────┐ confirm ┌───────────┐
 │ QUEUED ├────────►│ IN_PRODUCTION ├─────────►│ READY ├───────►│ SHIPPED ├────────►│ DELIVERED │
 └───┬────┘         └───────┬───────┘◄─────────┴───┬───┘        └────┬────┘         └───────────┘
     │                      │          reopen      │                 │
     └──────────────────────┴──────────────────────┴─────────────────┘
                                  cancel
                                    ▼
                               ┌──────────┐
                               │ CANCELED │
                               └──────────┘
```

### 4.3 Idempotency of creation

`production.production_order.quote_revision_id` carries a **unique constraint**. The
`QuoteApproved` handler performs:

```sql
INSERT INTO production.production_order (...) VALUES (...)
ON CONFLICT (quote_revision_id) DO NOTHING;
-- then SELECT the row and return it
```

so a retried request, a replayed event, a double-clicked button and a concurrent approval all
converge on exactly one order. The handler returns the existing order rather than failing —
approval is naturally idempotent from the user's point of view.

Three layers, in the order they take effect:

1. **The status guard plus the row lock.** Approval updates the revision row, so two concurrent
   approvals serialize on it and the second is rejected with `QUOTE_REVISION_ALREADY_DECIDED`
   before any insert is attempted.
2. **`UNIQUE (quote_revision_id)`** — defense in depth.
3. **`ON CONFLICT DO NOTHING`** — the replay path.

> **Never catch the unique violation and continue.** In PostgreSQL a statement error aborts the
> transaction (`25P02`); every subsequent command fails until `ROLLBACK`, so the following
> `SELECT` and the `COMMIT` would both fail. Only `ON CONFLICT` (used here) or an explicit
> `SAVEPOINT` / `ROLLBACK TO SAVEPOINT` is valid —
> [ADR-0012 §8](architecture/ADR-0012-domain-events-and-outbox.md#11-the-quote-approval-invariant).

### 4.4 Item status

`ProductionOrderItem.Status ∈ {PENDING, PRINTING, DONE, FAILED}`. Item states are advisory and
do not drive the order state machine in v1 — the operator moves the order. `FAILED` items
justify a re-print and are the natural place where wastage becomes visible as actual
consumption without a corresponding delivered unit.

---

## 5. Other lifecycles

### 5.1 Cost experiment (Laboratory)

```
DRAFT ──save──► SAVED ──convert──► CONVERTED (terminal for editing)
  │               │
  └──clone────────┴──► new DRAFT (SourceExperimentId set)
SAVED ──archive──► ARCHIVED
```
`CONVERTED` records `ConvertedToProductId`. A converted experiment is read-only; cloning it
produces a new `DRAFT`.

### 5.2 Document template version

```
DRAFT ──publish──► PUBLISHED ──archive──► ARCHIVED
```
- Only `PUBLISHED` versions may render production documents.
- A `PUBLISHED` version is **immutable**; editing creates a new `DRAFT` at `VersionNumber + 1`.
- Archiving a version never invalidates documents already generated from it: every
  `GeneratedDocument` keeps `DocumentTemplateVersionId`, so any past PDF remains reproducible.

### 5.3 Sale

```
CONFIRMED ──cancel──► CANCELED
```
Sales are financial facts; they are canceled, never deleted, and a canceled sale is excluded
from every revenue and profit metric while remaining visible in listings. Cancellation requires a
nonblank reason, actor, timestamp, status history and audit; it does not mutate ProductionOrder.
Only an `APPROVED` QuoteRevision is explicitly sale-eligible. Supersession does not change an
approved revision's eligibility, and approval itself never creates a Sale.

### 5.4 AI insight run

```
PENDING ──dispatch──► RUNNING ──► SUCCEEDED
                          └─────► FAILED (ErrorMessage recorded, no partial results kept)
```
Driven by the outbox; retried with backoff up to the configured attempt limit.

### 5.5 Outbox message

*(Added 2026-09-07, gate blocker B-003. Full specification in
[ADR-0012 Part II](architecture/ADR-0012-domain-events-and-outbox.md#part-ii--the-transactional-outbox).)*

```
                    ┌──────────────────────────────────────────┐
                    │  retryable failure: available_at += backoff
                    ▼                                          │
   enqueue     ┌─────────┐   claim (lease)   ┌────────────┐    │
  ───────────► │ PENDING │ ────────────────► │ PROCESSING │ ───┤
  (in the      └─────────┘  FOR UPDATE       └─────┬──────┘    │
   business         ▲        SKIP LOCKED           │ success   │
   transaction)     │                              ▼           │
                    │ lease expired          ┌───────────┐     │
                    │ (crash) → reclaim      │ PROCESSED │     │
                    │                        └───────────┘     │
                    │  manual requeue (Owner)                  │
                    │                          attempts exhausted
                    │                          or non-retryable
                    │                                          ▼
                    │                                    ┌────────┐
                    └────────────────────────────────────┤ FAILED │
                                                         └────────┘
```

| Transition | Trigger | Guards |
|---|---|---|
| — → `PENDING` | enqueued **inside** the business transaction | commits with the business row or not at all |
| `PENDING` → `PROCESSING` | dispatcher claim | **eligible only**: `available_at <= now()` **and** `attempt_count < max_attempts`; sets `processing_token` + `lease_until`, increments `attempt_count` |
| `PROCESSING` → `PROCESSED` | consumer success **with a matching `processing_token`** | terminal; prunable after 90 days |
| `PROCESSING` → `PENDING` | retryable failure **and** `attempt_count < max_attempts` | `available_at = now + backoff` (1 m / 5 m / 30 m / 2 h) |
| `PROCESSING` → `FAILED` | retryable failure **and** `attempt_count >= max_attempts` | `RETRY_BUDGET_EXHAUSTED` → **never** returned to `PENDING` |
| `PROCESSING` → `PENDING` | **lease expired** and `attempt_count < max_attempts` | reclaim sweep; the attempt is **not** refunded |
| `PROCESSING` → `FAILED` | **lease expired** and `attempt_count >= max_attempts` | `LEASE_EXPIRED_ON_FINAL_ATTEMPT` |
| `PROCESSING` → `FAILED` | `NonRetryableOutboxException` | immediate, budget irrelevant |
| `FAILED` → `PENDING` | **manual requeue**, `Owner` only, reason required | **`execution_generation` incremented**, `attempt_count` **reset to 0**, `failure_disposition` cleared; `id` and `idempotency_key` **unchanged**; `last_error` and attempt history **preserved**; audit row written |

**Every worker-driven transition carries `AND processing_token = :token`** — the lease fence.
0 rows affected means the caller is stale (its lease expired and another worker now owns the
message): it logs and abandons, and **must not** overwrite the new owner's state
([ADR-0012 §15](architecture/ADR-0012-domain-events-and-outbox.md#15-lease-fencing--the-correction)).

`PROCESSED` and `FAILED` are terminal; only an explicit administrative action moves `FAILED`
back. On entering `FAILED` the message also receives a **disposition**:

| `failure_disposition` | Meaning | Set by |
|---|---|---|
| `ACTIVE` | unresolved; needs a human; counts toward `degraded` health | the system |
| `DISMISSED` | a human decided this effect must never be retried; **not claimable** | `Owner`, reason required |

There is **no `RESOLVED` disposition**. A requeued message that succeeds ends at
`status = PROCESSED` with `failure_disposition = NULL` → success is what "resolved" means, and
the schema forbids a disposition outside `FAILED`. History survives in
`outbox_message_attempt`, keyed by generation.

**Retry rounds.** `attempt_count` is the budget of the **current** round and resets on requeue;
historical attempts are identified by `(message, execution_generation, attempt_number)`, so
generation 2 attempt 1 never collides with generation 1 attempt 1. A requeue opens a new
generation and leaves `idempotency_key` untouched — it retries the *same* business effect
([ADR-0012 §19](architecture/ADR-0012-domain-events-and-outbox.md#19-attempt-history-and-execution-generations)).

**One terminal-attempt rule:** `attempt_count >= max_attempts` yields `FAILED`, identically for
an explicit retryable failure and for a crash detected by lease expiry.

Dismissal **never deletes** the message, its `last_error` or its attempt history — it records a
decision alongside the facts and stops the noise.

Retry delivery is at-least-once, so **every consumer must be idempotent** — document renders key
on `render_request_id` so a retry cannot produce a second `ISSUED` document.

### 5.6 ChannelOffer (S8B target)

```text
INACTIVE ──activate──► ACTIVE ──deactivate──► INACTIVE
```

| Transition | Guards | Effects |
|---|---|---|
| create ACTIVE | Product active; SalesChannel active; positive intended price; unique Product × channel | set `ActivatedAt`; audit |
| create INACTIVE | referenced identities exist; positive intended price; unique Product × channel | retained planning record; audit |
| INACTIVE → ACTIVE | expected Version; Product and SalesChannel active | set current `ActivatedAt`, clear current `DeactivatedAt`; audit |
| ACTIVE → INACTIVE | expected Version | set `DeactivatedAt`; preserve identity/history; audit |

Product or SalesChannel deactivation does not delete or rewrite ChannelOffer. Product
deactivation makes the exact product-level commercial-active fact false. SalesChannel
deactivation leaves offer intent/status unchanged but blocks new activation, publication and
commercial operations on that channel; historical labels remain resolvable. DIRECT follows this
same machine without any marketplace account/listing side effect.

### 5.7 MarketplaceAccount (S8B target)

Account enablement, connection and sync are separate dimensions:

```text
ACTIVE ──deactivate──► INACTIVE ──activate──► ACTIVE

NOT_CONFIGURED ──configure──► DISCONNECTED ──connect──► CONNECTED
                                  ▲    │                    │
                                  │    └────failure────────► ERROR
                                  └────────disconnect───────┘

NEVER_SYNCED ──successful sync──► SYNCED
      │                              │
      └────failed attempt──────────► ERROR
                                     │
                                     └──successful retry──► SYNCED
```

S8B represents these states but performs no provider connection or sync. Creation requires an
existing active Pricing SalesChannel with `Kind = Marketplace` and code other than `DIRECT`.
SalesChannelId is immutable after creation. Deactivation blocks new provider operations and
preserves that channel mapping, external account identity, listings and ChannelOffer intent.
Credential rotation never changes account identity. Expected Version and audit apply to all
operator mutations.

Capability support (`UNKNOWN|SUPPORTED|UNSUPPORTED`) and account grant
(`UNKNOWN|GRANTED|DENIED`) are structural facts outside these transient machines. Connection or
sync failure may block execution but never changes SUPPORTED to UNSUPPORTED or GRANTED to DENIED.

### 5.7A S8C.1 corrected authorization/session/runtime machines

[ADR-0024](architecture/ADR-0024-s8c1-marketplace-connector-authorization-foundation.md)
supersedes §5.7 connection semantics upon implementation. S8B sync state is unchanged.

| Session transition | Guard / effect |
|---|---|
| create -> PENDING | Owner + antiforgery; valid provider/active marketplace channel; 32-byte random state/browser nonce hashed; expiry = creation + 15 minutes |
| PENDING -> CLAIMED | matching provider/browser/actor, not expired, expected Version; conditional update commits before external exchange; one winner |
| PENDING -> EXPIRED | now >= ExpiresAt, immediate logical invalidity |
| PENDING -> REVOKED | initiating Owner cancels/supersedes transaction; no exchange |
| CLAIMED -> COMPLETED | operation row CONFIRMED in the same transaction as verified identity/context, current credential, account/grants/audit and session |
| CLAIMED -> FAILED | operation FAIL_CLOSED for code rejection, mismatch, exchange failure/ambiguity, lost persistence or abandonment after two minutes |
| terminal -> deleted | Quartz has resolved the operation's irreversible decision and removed transient/non-executable candidate material; audit retained |

COMPLETED, FAILED, EXPIRED and REVOKED are terminal. No CLAIMED reclaim or retry exchange.
Failure after claim always needs a new session. The 60-second callback deadline is below the
two-minute abandonment threshold; completion requires current CLAIMED/version and operation
PENDING. Late workers cannot commit after the terminal arbiter has failed the operation closed.
Actor permission is rechecked on begin/claim/final commit. Two new sessions racing one external
identity produce one database account and a safe reconnect-required conflict; the later exchange
may require the winner to reauthorize. A uniqueness exception rolls back and reloads through a
new transaction/DbContext before resolving the loser and winner under the operation arbiter.

| Credential operation transition | Guard / effect |
|---|---|
| create -> PREPARED / PENDING | durable operation row before any code or refresh-token request; account may be null only for new authorization |
| PREPARED -> EXTERNAL_IN_FLIGHT / PENDING | durable before send; callback operations establish the provider-wide safety fence |
| EXTERNAL_IN_FLIGHT -> SECRET_PERSISTED / PENDING | exact store receipt observed; not authorization confirmation; provider fence remains |
| PENDING -> CONFIRMED | lock operation row before store CAS, hold through DB commit; connection and, for callbacks, session/identity/grants/actor/audit commit together |
| PENDING -> FAIL_CLOSED | same row lock; impossible to confirm later; deny affected credentials after sent/unknown request |
| CONFIRMED or FAIL_CLOSED -> cleanup PENDING -> DONE | unused candidate/retired material removed only after terminal decision; current confirmed reference never deleted |

`Decision {PENDING, CONFIRMED, FAIL_CLOSED}` is irreversible and separate from progress phase
`{PREPARED, EXTERNAL_IN_FLIGHT, SECRET_PERSISTED}` and cleanup state
`{NOT_REQUIRED, PENDING, DONE}`. The PostgreSQL operation row is the only durable credential
operation marker. Every callback, refresh resolver, startup worker and Quartz worker locks it
before terminal arbitration. Row lock waits for an in-progress commit/rollback; one negative
snapshot never proves rollback. A store receipt proves only persistence. CONNECT_NEW/RECONNECT
can be CONFIRMED only by their complete callback transaction, never by receipt-only startup
recovery; REFRESH may confirm a matching receipt after account/reference/version guards. A
provider-wide callback fence blocks other credential execution until terminal resolution; if
identity is unknown after a crash, startup fails the operation and marks provider accounts
REAUTHORIZATION_REQUIRED before releasing the fence. DISCONNECT commits REVOKED and operation
CONFIRMED before secret deletion; cleanup failure leaves deletion pending, never re-enables use.
The single-instance provider execution gate holds a shared lease through each final guard and
provider send; callback holds the exclusive lease from pre-send fence establishment to terminal
resolution. Acquire it before account locks. Late operational responses recheck current confirmed
operation/root Version before persisting runtime or grants.

| Account authorization transition | Meaning |
|---|---|
| migration -> NOT_CONNECTED | legacy identity retained, arbitrary legacy pointer cleared |
| NOT_CONNECTED/REAUTHORIZATION_REQUIRED/REVOKED -> CONNECTED | successful explicitly bound reconnect; identity exact; confirmed protected credential |
| new verified account -> CONNECTED | successful new-session callback with unique identity |
| CONNECTED -> CONNECTED | refresh persisted and confirmed; starting reconnect alone does not remove existing usable credential |
| CONNECTED -> REAUTHORIZATION_REQUIRED | revoked/invalid refresh, after-send ambiguity, lost/unreadable secret, unrecoverable write/CAS or orphan operation |
| CONNECTED -> REAUTHORIZATION_REQUIRED | duplicate/new exchange may supersede the same provider identity under UNKNOWN/MAY_SUPERSEDE impact, including an inactive account; histories preserved |
| any -> REVOKED | Owner disconnect, immediate local denial, idempotent deletion; Active/history/grants retained |

There is no persistent AUTHORIZATION_PENDING or ERROR. A session failure proven before provider
send preserves existing authorization. After reconnect exchange may have been sent, its durable
operation suspends execution; failure cannot silently reinstate old tokens. After confirmed K1
loss/corruption or an unconfirmed higher version left by FAIL_CLOSED, explicit bound recovery
confirms fresh K2/version 1 with session, identity, grants, actor and audit in one transaction.
K1 never becomes executable again even if its file or old key ring reappears. Operation-row
arbitration, not a store receipt, resolves DB commit uncertainty.
Active is independent; deactivation preserves credentials and identity but prevents provider work.
Disconnect works even when inactive and sets runtime UNKNOWN.

| Runtime transition | Guard / effect |
|---|---|
| create/migrate/connect/reconnect/disconnect -> UNKNOWN | no operational observation for this connection generation |
| UNKNOWN/AVAILABLE -> AVAILABLE | successful controlled operation/probe |
| UNKNOWN/AVAILABLE -> UNAVAILABLE | normalized terminal operational failure after allowed bounded read retries |
| UNAVAILABLE -> AVAILABLE | successful Owner-controlled probe honoring Retry-After |
| UNAVAILABLE -> UNAVAILABLE | probe fails; safe last failure updated |

No DEGRADED, failure counter or unstated threshold. Runtime persists across restart with historical
timestamps. UNKNOWN permits the first operation; UNAVAILABLE blocks ordinary automatic work,
but not its Owner recovery probe. Probe still requires active account/usable authorization and
registered inspector; it never fabricates structural support or a grant. Account grant UNKNOWN
disables a business capability but cannot disable authorization/inspection needed to discover it.
A pending operation survives restart: REFRESH may reconcile its exact matching store receipt
only under refresh guards; CONNECT_NEW/RECONNECT require their completed callback transaction
or fail closed. DISCONNECT resumes local deletion and retains REVOKED. Operation fencing is not
a runtime-health state. Effective business execution also requires an existing active Marketplace
SalesChannel, provider support, account grant and Active, confirmed credential and runtime not
UNAVAILABLE. UNKNOWN allows the first attempt; probe bypasses UNAVAILABLE but respects the
channel and Retry-After guards.

### 5.8 MarketplaceListing observed status and linkage (S8B target)

Observed status is normalized provider reality:

```text
DRAFT | ACTIVE | PAUSED | INACTIVE | ERROR
```

Provider observations may move between these values; this is not VERCE's ChannelOffer state.
Native status is a separate bounded code.

Linkage is independent:

```text
UNLINKED ──ambiguous candidates──► NEEDS_REVIEW
    │                                  │
    └──verified/operator link──────────┴──► LINKED
LINKED ──operator unlink/correction────────► UNLINKED
```

Only deterministic verified identity or explicit operator choice enters LINKED. LINKED requires
ProductId; ChannelOfferId may be null while commercial intent is not yet connected. UNLINKED and
NEEDS_REVIEW require both ProductId and ChannelOfferId null, so fuzzy/multiple candidates never
persist a guessed link. A non-null ChannelOfferId requires LINKED and must resolve to the same
Product and the account's immutable SalesChannel. Unlink preserves listing identity and
observation history. Connecting or creating a ChannelOffer is a distinct explicit audited choice;
an existing offer is never overwritten.

### 5.9 MarketplaceListing sync state (S8B target)

```text
NEVER_SYNCED ──successful sync──► SYNCED
      │                              │
      └────failed attempt──────────► ERROR
                                     │
                                     └──successful retry──► SYNCED
```

`ACTIVE` observed status with sync `ERROR` is valid: the former is the last known provider fact,
the latter is freshness/transport health. S8B manual observations normally remain NEVER_SYNCED;
real transitions begin only after S8C.0 and connector implementation. Error text is redacted and
bounded; no secret-bearing response body is stored.

### 5.10 S8C.2 provider listing reads (architecture frozen)

[ADR-0025](architecture/ADR-0025-s8c2-mercado-livre-listing-read-integration.md) §C–§E.

**Mercado Livre status → observed status (§5.8 vocabulary).** `active`→ACTIVE; `paused`→PAUSED;
`closed`, `inactive`→INACTIVE; `not_yet_active`, `programmed`→DRAFT; `under_review`,
`payment_required`, `pending`→ERROR; absent/unknown → **not normalized**: run item
`PERMANENT_ERROR/LISTING_STATUS_UNMAPPED`, no facts applied, never ACTIVE. Native
`status[:sub_status,…]` is preserved, bounded.

**Listing sync state (§5.9) under provider reads.** → SYNCED on a FOUND read; → ERROR (facts,
linkage, history unchanged) on NOT_FOUND, ACCESS_DENIED, SELLER_MISMATCH, TRANSIENT_ERROR or
PERMANENT_ERROR. Absence from an enumeration never changes a listing; only its direct read does.

**Linkage under provider reads (§5.8).** `SKU-LINK-1` on import-created listings and on UNLINKED
listings without auto-link suppression, from an ACTIVE explicit SKU mapping only (UNLINKED /
Product-only LINKED `DETERMINISTIC_SKU` / NEEDS_REVIEW). LINKED and NEEDS_REVIEW listings are never
re-evaluated; operator unlink suppresses auto-link.

**Observation.** Appended when fingerprint v2 differs from the root's latest; otherwise
freshness-only. `A → B → A` yields three rows; identical repeats yield none.

**Listing sync run.** No RETRY_WAIT: request retries are the safe-read policy, item retries are new
FAILED_ONLY runs, crash recovery is reclaim.

```text
start FULL / retry FAILED_ONLY ──► QUEUED ──claim (new LeaseToken)──► RUNNING ──finish──► SUCCEEDED | PARTIAL | FAILED
                                                                        │   ▲
                                                                        └───┘ lease expired: reclaim with a NEW token
                                                                              (attempt_count < max_attempts)
RUNNING, lease expired, attempts exhausted ──dispatcher CAS on stale token──► FAILED
```

| Transition | Guard / effect |
|---|---|
| start → QUEUED | commerce:manage + antiforgery; ADR-0025 §G.2 guards; one active run per account (DB unique; 409 with active RunId); audit |
| retry → QUEUED (new run) | original terminal PARTIAL/FAILED of the same account with ≥ 1 retryable item; copies retryable items as RETRY_TARGET/PENDING; original immutable |
| QUEUED → RUNNING | claim sets a new `lease_token`, lease 120 s, `attempt_count + 1` |
| RUNNING → RUNNING (reclaim) | `lease_until < now` and `attempt_count < max_attempts`; new token; the old token can never validate again |
| every run-owned write | first statement of its transaction: guard `WHERE id AND lease_token AND status='RUNNING'`; 0 rows ⇒ rollback, write nothing |
| RUNNING → SUCCEEDED | all items FOUND, no stop reason |
| RUNNING → PARTIAL | ≥ 1 FOUND and ≥ 1 blocking item (or a stop after meaningful work, FULL enumeration complete) |
| RUNNING → FAILED | start/guard failure; FULL stop before enumeration completed; zero FOUND with blocking items or stop; volume limit |
| RUNNING → FAILED (exhausted) | lease expired, attempts exhausted; PENDING items → TRANSIENT_ERROR `RUN_ENDED_BEFORE_ITEM_PROCESSED` |

**Run item.** `PENDING → FOUND | NOT_FOUND | ACCESS_DENIED | SELLER_MISMATCH | TRANSIENT_ERROR |
PERMANENT_ERROR`, once, fenced; terminal outcomes never change. A run finish or stop turns remaining
PENDING items into TRANSIENT_ERROR. FOUND = successful; every other terminal outcome is blocking;
NOT_FOUND, ACCESS_DENIED, TRANSIENT_ERROR are retryable by FAILED_ONLY; SELLER_MISMATCH (security) and
PERMANENT_ERROR are not (only a new FULL run re-reads them). This processing state is distinct from
the provider listing state.

**SKU mapping.** `(confirm) → ACTIVE → INACTIVE` with reason `OPERATOR_DEACTIVATED` or
`REPLACED_BY_NEW_MAPPING`; INACTIVE is terminal (a new mapping is confirmed instead). A mapping
controls future auto-link only; it never unlinks or relinks existing linked listings.

---

## 6. Error codes emitted by transitions

| Code | HTTP | Meaning |
|---|---|---|
| `QUOTE_REVISION_NOT_CURRENT` | 409 | Attempted transition on a superseded revision |
| `QUOTE_REVISION_ALREADY_DECIDED` | 409 | Revision is terminal |
| `QUOTE_REVISION_EXPIRED` | 409 | Validity elapsed |
| `QUOTE_INVALID_TRANSITION` | 409 | Target state not reachable from current state |
| `QUOTE_HAS_NO_ITEMS` | 422 | Approval/sending of an empty quote |
| `QUOTE_ITEM_INVALID_PRICE` | 422 | Non-positive final price |
| `PRODUCTION_ORDER_IN_PROGRESS` | 409 | §3 case B block |
| `PRODUCTION_INVALID_TRANSITION` | 409 | Target state not reachable |
| `DOCUMENT_TEMPLATE_NOT_PUBLISHED` | 409 | Render attempted with a draft template |
| `PRICING_INVALID_DENOMINATOR` | 422 | commission + margin ≥ 1 |
| `CONCURRENCY_CONFLICT` | 409 | The aggregate changed since it was loaded ([ADR-0011 §2](architecture/ADR-0011-identifiers-and-concurrency.md)) |
| `LAST_OWNER_PROTECTED` | 409 | Attempt to remove, deactivate or demote the last active Owner |
| `DOMAIN_EVENT_WAVE_LIMIT_EXCEEDED` | 500 | Event cycle detected; programming error, transaction rolled back |
| `CHANNEL_OFFER_ALREADY_EXISTS` | 409 | Product and SalesChannel already have their durable ChannelOffer |
| `CHANNEL_OFFER_ACTIVATION_BLOCKED` | 409 | Product or SalesChannel is inactive, so the offer cannot activate |
| `COMMERCE_CAPABILITY_UNAVAILABLE` | 409 | Structural provider support or account grant is absent/unknown |
| `COMMERCE_OPERATION_UNAVAILABLE` | 503 | Runtime provider/account health currently blocks an otherwise permitted operation |
| `MARKETPLACE_ACCOUNT_INACTIVE` | 409 | A provider operation was requested for an inactive account |
| `MARKETPLACE_ACCOUNT_CHANNEL_INVALID` | 422 | Account creation referenced a missing, inactive, non-Marketplace or DIRECT channel |
| `MARKETPLACE_ACCOUNT_CHANNEL_IMMUTABLE` | 409 | Attempted to change an existing account's SalesChannelId |
| `LISTING_LINKAGE_CONFLICT` | 409 | Link/unlink expected Version or Product/offer consistency failed |
| `LISTING_LINKAGE_AMBIGUOUS` | 422 | Candidate identity is fuzzy or non-unique and requires operator review |
| `LISTING_SYNC_ALREADY_ACTIVE` | 409 | The account already has a QUEUED/RUNNING listing sync; the response carries its RunId (S8C.2) |
| `MARKETPLACE_AUTHORIZATION_NOT_USABLE` | 409 | Account authorization not CONNECTED/confirmed, or a credential operation/provider fence is pending (S8C.2 sync start) |
| `MARKETPLACE_PROVIDER_NOT_SUPPORTED` | 409 | No listing reader is registered for the provider in this environment (S8C.2) |
| `LISTING_SYNC_NOTHING_TO_RETRY` | 409 | Retry requested for a run with no retryable item, or a non-terminal/foreign run (S8C.2) |
| `SKU_MAPPING_CONFLICT` | 409 | An ACTIVE mapping exists for the account SKU and the request did not name it for replacement (S8C.2) |
| `SKU_MAPPING_PRODUCT_INELIGIBLE` | 422 | The mapped Product does not exist or is inactive (S8C.2) |
| `SKU_MAPPING_SKU_INVALID` | 422 | The SKU is empty after normalization or exceeds 200 characters (S8C.2) |

Codes are stable strings; the frontend maps them to pt-BR messages. Message text is never
matched programmatically.
